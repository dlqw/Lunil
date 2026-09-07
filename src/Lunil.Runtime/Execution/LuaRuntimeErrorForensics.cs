using System.Collections.Immutable;
using Lunil.IR.Canonical;
using System.Diagnostics;
using System.Text;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>
/// Reconstructs Lua error position, operand, and kind information for runtime failures
/// after an unwind. Isolated from the scheduler so diagnostics engineering cannot affect
/// the execution path.
/// </summary>
internal static class LuaRuntimeErrorForensics
{
    internal static LuaRuntimeException EnrichRuntimeException(
        LuaThread thread,
        LuaFrame frame,
        LuaRuntimeException exception)
    {
        if (exception.HasErrorValue)
        {
            return exception;
        }

        // Producers classify their errors with a kind; the message-shape checks remain
        // only as a fallback for unclassified (legacy or host-supplied) exceptions.
        var kind = exception.Kind;
        var isAttemptError = Matches(kind, LuaRuntimeErrorKind.AttemptTo, exception, static m => m.StartsWith("attempt to ", StringComparison.Ordinal));
        var isIntegerConversionError = Matches(kind, LuaRuntimeErrorKind.IntegerConversion, exception, static m => m.Contains("has no integer representation", StringComparison.Ordinal));
        var isNumericForError = Matches(kind, LuaRuntimeErrorKind.NumericFor, exception, static m => m.StartsWith("bad 'for' ", StringComparison.Ordinal));
        var isBadArgumentError = Matches(kind, LuaRuntimeErrorKind.BadArgument, exception, static m => m.StartsWith("bad argument #", StringComparison.Ordinal));
        var isAssertionError = kind == LuaRuntimeErrorKind.Assertion ||
            (kind == LuaRuntimeErrorKind.None &&
             string.Equals(exception.Message, "assertion failed!", StringComparison.Ordinal));
        if (!isAttemptError && !isIntegerConversionError && !isNumericForError &&
            !isBadArgumentError && !isAssertionError)
        {
            return exception;
        }

        var instructions = frame.Function.Instructions;
        if (instructions.IsEmpty)
        {
            return exception;
        }

        var pc = Math.Clamp(frame.ProgramCounter, 0, instructions.Length - 1);
        var instruction = instructions[pc];
        var message = exception.Message;
        if (isBadArgumentError &&
            instruction.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall &&
            LuaExecutionEngine.Read(thread, frame, instruction.A).TryGetNativeFunction() is { } native &&
            native.Name.Contains('.', StringComparison.Ordinal))
        {
            var marker = " to '";
            var nameStart = message.IndexOf(marker, StringComparison.Ordinal);
            if (nameStart >= 0)
            {
                nameStart += marker.Length;
                var nameEnd = message.IndexOf('\'', nameStart);
                if (nameEnd >= 0)
                {
                    message = message[..nameStart] + native.Name + message[nameEnd..];
                }
            }

            return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
        }

        if (isAttemptError && !message.Contains(" (", StringComparison.Ordinal) &&
            message.StartsWith("attempt to call ", StringComparison.Ordinal) &&
            instruction.Opcode is LuaIrOpcode.GetTable or LuaIrOpcode.SetTable or
                LuaIrOpcode.Unary or LuaIrOpcode.Binary)
        {
            message = $"{message} (metamethod '{LuaExecutionEngine.GetOperationMetamethodName(instruction)}')";
            return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
        }

        var register = isAssertionError
            ? -1
            : isIntegerConversionError && !isBadArgumentError
                ? GetIntegerConversionOperandRegister(thread, frame, instruction)
                : GetErrorOperandRegister(thread, frame, instruction);
        if (register >= 0 &&
            TryDescribeRegisterOrigin(frame, pc, register, depth: 0, out var origin))
        {
            if (instruction.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall &&
                origin.Kind == LuaValueOriginKind.Field &&
                origin.SourceRegister == instruction.A + 1)
            {
                origin = origin with { Kind = LuaValueOriginKind.Method };
            }

            const string badSelfPrefix = "bad argument #1 to '";
            if (isBadArgumentError &&
                origin.Kind == LuaValueOriginKind.Method &&
                message.StartsWith(badSelfPrefix, StringComparison.Ordinal) &&
                message.IndexOf('\'', badSelfPrefix.Length) is var functionNameEnd &&
                functionNameEnd >= 0)
            {
                var functionName = message[badSelfPrefix.Length..functionNameEnd];
                message = $"calling '{functionName}' on bad self" + message[(functionNameEnd + 1)..];
            }
            else if (isBadArgumentError && origin.Kind == LuaValueOriginKind.Method)
            {
                const string argumentPrefix = "bad argument #";
                var numberEnd = message.IndexOf(' ', argumentPrefix.Length);
                if (numberEnd > argumentPrefix.Length &&
                    int.TryParse(message.AsSpan(argumentPrefix.Length, numberEnd - argumentPrefix.Length),
                        out var argumentNumber) &&
                    argumentNumber > 1)
                {
                    message = argumentPrefix + (argumentNumber - 1) + message[numberEnd..];
                }
            }
            else if (!message.Contains(" (", StringComparison.Ordinal) &&
                FormatOrigin(origin) is { } formattedOrigin)
            {
                message = isIntegerConversionError &&
                    message.StartsWith("number has ", StringComparison.Ordinal)
                        ? "number (" + formattedOrigin + ") " + message["number ".Length..]
                        : $"{message} ({formattedOrigin})";
            }
        }

        return new LuaRuntimeException(FormatRuntimeErrorLocation(frame, instruction, message));
    }

    internal static int GetIntegerConversionOperandRegister(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        if (instruction.Opcode == LuaIrOpcode.Unary)
        {
            return instruction.B;
        }

        if (instruction.Opcode != LuaIrOpcode.Binary)
        {
            return -1;
        }

        return LuaExecutionEngine.Read(thread, frame, instruction.B).TryGetInteger(out _)
            ? instruction.C
            : instruction.B;
    }

    internal static string? FormatOrigin(LuaValueOrigin origin) => origin.Kind switch
    {
        LuaValueOriginKind.Local => $"local '{origin.Name}'",
        LuaValueOriginKind.Upvalue => $"upvalue '{origin.Name}'",
        LuaValueOriginKind.Global => $"global '{origin.Name}'",
        LuaValueOriginKind.Field => $"field '{origin.Name}'",
        LuaValueOriginKind.Method => $"method '{origin.Name}'",
        _ => null,
    };

    internal static string FormatRuntimeErrorLocation(
        LuaFrame frame,
        LuaIrInstruction instruction,
        string message)
    {
        if (instruction.SourceLine <= 0 || frame.Function.SourceName.IsEmpty)
        {
            return $"?:-1: {message}";
        }

        const int maximumLength = 59;
        var source = Encoding.UTF8.GetString(frame.Function.SourceName.AsSpan());
        string shortSource;
        if (source.StartsWith('@'))
        {
            source = source[1..];
            shortSource = source.Length <= maximumLength
                ? source
                : "..." + source[^(maximumLength - 3)..];
        }
        else if (source.StartsWith('='))
        {
            source = source[1..];
            shortSource = source.Length <= maximumLength ? source : source[..maximumLength];
        }
        else
        {
            const string prefix = "[string \"";
            const string suffix = "\"]";
            var available = maximumLength - prefix.Length - suffix.Length;
            var newLine = source.IndexOf('\n');
            if (newLine >= 0 || source.Length > available)
            {
                var end = newLine < 0 ? source.Length : newLine;
                source = source[..Math.Min(end, available - 3)] + "...";
            }

            shortSource = prefix + source + suffix;
        }

        return $"{shortSource}:{instruction.SourceLine}: {message}";
    }

    internal static int GetErrorOperandRegister(
        LuaThread thread,
        LuaFrame frame,
        LuaIrInstruction instruction)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.Call:
            case LuaIrOpcode.TailCall:
                return instruction.A;
            case LuaIrOpcode.GetTable:
                return instruction.B;
            case LuaIrOpcode.SetTable:
                return instruction.A;
            case LuaIrOpcode.Unary:
                return instruction.B;
            case LuaIrOpcode.Binary:
                var operation = (LuaIrBinaryOperator)instruction.D;
                var left = LuaExecutionEngine.Read(thread, frame, instruction.B);
                if (IsArithmeticErrorOperation(operation))
                {
                    return LuaValueOperations.TryToNumber(left, out _)
                        ? instruction.C
                        : instruction.B;
                }

                if (IsBitwiseErrorOperation(operation))
                {
                    return left.Kind is LuaValueKind.Integer or LuaValueKind.Float
                        ? instruction.C
                        : instruction.B;
                }

                if (operation == LuaIrBinaryOperator.Concatenate)
                {
                    return left.Kind is LuaValueKind.String or LuaValueKind.Integer or LuaValueKind.Float
                        ? instruction.C
                        : instruction.B;
                }

                return -1;
            default:
                return -1;
        }
    }

    internal static bool TryDescribeRegisterOrigin(
        LuaFrame frame,
        int pc,
        int register,
        int depth,
        out LuaValueOrigin origin)
    {
        origin = default;
        if (depth >= 16 || register < 0 || register >= frame.Function.RegisterCount)
        {
            return false;
        }

        var function = frame.Function;
        var activeLocals = frame.FunctionVersion.GetActiveDebugLocals(pc);
        if (register < activeLocals.Length)
        {
            var name = Encoding.UTF8.GetString(activeLocals[register].Name.AsSpan());
            origin = string.Equals(name, "_ENV", StringComparison.Ordinal)
                ? new LuaValueOrigin(LuaValueOriginKind.Environment, name)
                : new LuaValueOrigin(LuaValueOriginKind.Local, name);
            return true;
        }

        for (var candidatePc = Math.Min(pc - 1, function.Instructions.Length - 1);
             candidatePc >= 0;
             candidatePc--)
        {
            var candidate = function.Instructions[candidatePc];
            if (!WritesRegister(candidate, register))
            {
                continue;
            }

            if (IsShortCircuitMerge(function, candidatePc, pc, register))
            {
                // The producer belongs to an arm that a forward conditional jump can
                // skip while preserving the same register. Lua deliberately omits a
                // variable name because either arm may be the source. A producer at or
                // after the jump target is instead an ordinary register reuse following
                // a statement-level branch and must retain its origin.
                return false;
            }

            switch (candidate.Opcode)
            {
                case LuaIrOpcode.Move:
                    return TryDescribeRegisterOrigin(
                        frame,
                        candidatePc,
                        candidate.B,
                        depth + 1,
                        out origin);
                case LuaIrOpcode.GetUpvalue:
                    if ((uint)candidate.B >= (uint)function.Upvalues.Length)
                    {
                        return false;
                    }

                    var upvalueName = function.Upvalues[candidate.B].Name;
                    origin = string.Equals(upvalueName, "_ENV", StringComparison.Ordinal)
                        ? new LuaValueOrigin(LuaValueOriginKind.Environment, upvalueName)
                        : new LuaValueOrigin(LuaValueOriginKind.Upvalue, upvalueName);
                    return true;
                case LuaIrOpcode.GetTable:
                    var hasKey = TryGetStringConstantProducer(
                            function,
                            candidatePc,
                            candidate.C,
                            out var key);
                    if (!hasKey)
                    {
                        return false;
                    }

                    _ = TryDescribeRegisterOrigin(
                        frame,
                        candidatePc,
                        candidate.B,
                        depth + 1,
                        out var tableOrigin);
                    origin = tableOrigin.Kind == LuaValueOriginKind.Environment
                        ? new LuaValueOrigin(LuaValueOriginKind.Global, key, candidate.B)
                        : new LuaValueOrigin(LuaValueOriginKind.Field, key, candidate.B);
                    return true;
                default:
                    return false;
            }
        }

        return false;
    }

    internal static bool IsShortCircuitMerge(
        LuaIrFunction function,
        int producerPc,
        int consumerPc,
        int register)
    {
        for (var branchPc = 0; branchPc < consumerPc; branchPc++)
        {
            var branch = function.Instructions[branchPc];
            if (branch.Opcode is LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue &&
                branch.A == register &&
                branch.B > producerPc &&
                branch.B <= consumerPc)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool TryGetStringConstantProducer(
        LuaIrFunction function,
        int pc,
        int register,
        out string value)
    {
        for (var candidatePc = Math.Min(pc - 1, function.Instructions.Length - 1);
             candidatePc >= 0;
             candidatePc--)
        {
            var candidate = function.Instructions[candidatePc];
            if (!WritesRegister(candidate, register))
            {
                continue;
            }

            if (candidate.Opcode == LuaIrOpcode.Move)
            {
                return TryGetStringConstantProducer(function, candidatePc, candidate.B, out value);
            }

            if (candidate.Opcode == LuaIrOpcode.LoadConstant &&
                (uint)candidate.B < (uint)function.Constants.Length &&
                function.Constants[candidate.B] is { Kind: LuaIrConstantKind.String } constant)
            {
                value = Encoding.UTF8.GetString(constant.Bytes.AsSpan());
                return true;
            }

            break;
        }

        value = string.Empty;
        return false;
    }

    internal static bool WritesRegister(LuaIrInstruction instruction, int register) =>
        instruction.Opcode switch
        {
            LuaIrOpcode.LoadNil => register >= instruction.A &&
                register < instruction.A + Math.Max(instruction.B, 1),
            LuaIrOpcode.VarArg => register == instruction.A ||
                instruction.B > 1 && register >= instruction.A &&
                register < instruction.A + instruction.B,
            LuaIrOpcode.Call => register == instruction.A ||
                instruction.C > 1 && register >= instruction.A &&
                register < instruction.A + instruction.C,
            LuaIrOpcode.LoadConstant or LuaIrOpcode.Move or LuaIrOpcode.GetUpvalue or
                LuaIrOpcode.NewTable or LuaIrOpcode.GetTable or LuaIrOpcode.Closure or
                LuaIrOpcode.CreateVarArgTable or LuaIrOpcode.GetVarArg or
                LuaIrOpcode.Unary or LuaIrOpcode.Binary => register == instruction.A,
            _ => false,
        };

    internal static bool IsArithmeticErrorOperation(LuaIrBinaryOperator operation) =>
        operation is LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Subtract or
            LuaIrBinaryOperator.Multiply or LuaIrBinaryOperator.Divide or
            LuaIrBinaryOperator.FloorDivide or LuaIrBinaryOperator.Modulo or
            LuaIrBinaryOperator.Power;

    internal static bool IsBitwiseErrorOperation(LuaIrBinaryOperator operation) =>
        operation is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight;

    internal enum LuaValueOriginKind : byte
    {
        None,
        Environment,
        Local,
        Upvalue,
        Global,
        Field,
        Method,
    }

    internal readonly record struct LuaValueOrigin(
        LuaValueOriginKind Kind,
        string Name,
        int SourceRegister = -1);

    private static bool Matches(
        LuaRuntimeErrorKind kind,
        LuaRuntimeErrorKind expected,
        LuaRuntimeException exception,
        Func<string, bool> messagePredicate) =>
        kind == expected ||
        (kind == LuaRuntimeErrorKind.None && messagePredicate(exception.Message));
}
