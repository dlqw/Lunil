using System.Collections.Immutable;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaInterpreterTests
{
    [Fact]
    public void ExecutesArithmeticTablesAndStructuredControlFlow()
    {
        const string source = """
            local sum = 0
            local t = { 10, 20, 30 }
            for i = 1, 3 do sum = sum + t[i] end
            while sum < 63 do sum = sum + 1 end
            repeat sum = sum - 1 until sum == 60
            return sum, t[2]
            """;

        var values = Execute(source);

        Assert.Equal([LuaValue.FromInteger(60), LuaValue.FromInteger(20)], values);
    }

    [Fact]
    public void ExecutesClosuresOpenUpvaluesVarargsAndMultipleReturns()
    {
        const string source = """
            local function make(n)
                return function(x) n = n + x; return n end
            end
            local add = make(10)
            local first = add(2)
            local second = add(3)
            local function values(a, ...) return a, ... end
            return first, second, values(7, 8, 9)
            """;

        var values = Execute(source);

        Assert.Equal(
            [
                LuaValue.FromInteger(12),
                LuaValue.FromInteger(15),
                LuaValue.FromInteger(7),
                LuaValue.FromInteger(8),
                LuaValue.FromInteger(9),
            ],
            values);
    }

    [Fact]
    public void PreservesAssignmentTargetEvaluationOrderAndGotoClosing()
    {
        const string source = """
            local i = 1
            local t = { 10, 20 }
            i, t[i] = 2, 99
            do
                local captured = 40
                local function read() return captured end
                t[3] = read
                goto done
            end
            ::done::
            return i, t[1], t[2], t[3]()
            """;

        var values = Execute(source);

        Assert.Equal(
            [
                LuaValue.FromInteger(2),
                LuaValue.FromInteger(99),
                LuaValue.FromInteger(20),
                LuaValue.FromInteger(40),
            ],
            values);
    }

    [Fact]
    public void CallsHostFunctionsThroughTheGlobalEnvironment()
    {
        var state = new LuaState();
        var calls = 0;
        state.SetGlobal("twice", LuaValue.FromFunction(new LuaNativeFunction(
            "twice",
            (_, arguments) =>
            {
                calls++;
                return [LuaValue.FromInteger(arguments[0].AsInteger() * 2)];
            })));

        var values = Execute("return twice(21)", state);

        Assert.Equal(1, calls);
        Assert.Equal([LuaValue.FromInteger(42)], values);
    }

    [Theory]
    [InlineData("for i = 1, 3 do t[i] = function() return i end end")]
    [InlineData("for i in iterator, 3, 0 do t[i] = function() return i end end")]
    [InlineData("repeat local i = n + 1; t[i] = function() return i end; n = i until n == 3")]
    public void ClosesCapturedLoopVariablesAtEveryIteration(string loop)
    {
        var source = $$"""
            local function iterator(limit, current)
                current = current + 1
                if current <= limit then return current end
            end
            local t = {}
            local n = 0
            {{loop}}
            return t[1](), t[2](), t[3]()
            """;

        var values = Execute(source);

        Assert.Equal(
            [LuaValue.FromInteger(1), LuaValue.FromInteger(2), LuaValue.FromInteger(3)],
            values);
    }

    [Fact]
    public void PassesFixedArgumentsToRepeatedLuaCalls()
    {
        const string source = """
            local function iterator(limit, current)
                return current + 1, limit
            end
            local a, b = iterator(3, 0)
            local c, d = iterator(b, a)
            return a, b, c, d
            """;

        Assert.Equal(
            [
                LuaValue.FromInteger(1),
                LuaValue.FromInteger(3),
                LuaValue.FromInteger(2),
                LuaValue.FromInteger(3),
            ],
            Execute(source));
    }

    [Fact]
    public void PassesEveryFixedParameterToLuaClosure()
    {
        Assert.Equal(
            [LuaValue.FromInteger(3), LuaValue.FromInteger(4)],
            Execute("local function f(a, b) return a, b end; return f(3, 4)"));
    }

    [Fact]
    public void EnforcesInstructionBudgetWithoutUsingClrStackRecursion()
    {
        var module = Compile("while true do end");
        var state = new LuaState();
        var interpreter = new LuaInterpreter(
            LuaInterpreterOptions.Default with { MaximumInstructionCount = 100 });

        Assert.Throws<LuaRuntimeException>(() =>
            interpreter.Execute(state, state.CreateMainClosure(module)));
        Assert.Equal(LuaThreadStatus.Error, state.MainThread.Status);
    }

    [Fact]
    public void ChargesExactlyOneBudgetUnitPerCanonicalInstruction()
    {
        var instructions = new[]
        {
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1),
        }.ToImmutableArray();
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    RegisterCount = 1,
                    Constants = [LuaIrConstant.FromInteger(42)],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
        const int instructionCount = 2;
        var exactState = new LuaState();
        var exact = new LuaInterpreter(LuaInterpreterOptions.Default with
        {
            MaximumInstructionCount = instructionCount,
        });

        var completed = exact.Execute(exactState, exactState.CreateMainClosure(module));

        Assert.True(completed.Values.SequenceEqual([LuaValue.FromInteger(42)]));
        var insufficientState = new LuaState();
        var insufficient = new LuaInterpreter(LuaInterpreterOptions.Default with
        {
            MaximumInstructionCount = instructionCount - 1,
        });
        Assert.Throws<LuaRuntimeException>(() =>
            insufficient.Execute(insufficientState, insufficientState.CreateMainClosure(module)));
    }

    [Fact]
    public void ExecutesRecursiveCallsWithExplicitFrames()
    {
        const string source = """
            local function factorial(n)
                if n == 0 then return 1 end
                return n * factorial(n - 1)
            end
            return factorial(10)
            """;

        Assert.Equal([LuaValue.FromInteger(3_628_800)], Execute(source));
    }

    [Fact]
    public void ExecutesColonMethodsWithAnImplicitSelfParameter()
    {
        const string source = """
            local object = { value = 10 }
            function object:add(amount)
                self.value = self.value + amount
                return self.value
            end
            return object:add(5), object.value
            """;

        Assert.Equal(
            [LuaValue.FromInteger(15), LuaValue.FromInteger(15)],
            Execute(source));
    }

    [Fact]
    public void ImplementsLuaIntegerWrapFloorModuloAndShiftRules()
    {
        const string source = """
            return 0x7fffffffffffffff + 1, -3 // 2, -3 % 2,
                   1 << 64, 8 >> -1, 9007199254740993 == 9007199254740992.0
            """;

        Assert.Equal(
            [
                LuaValue.FromInteger(long.MinValue),
                LuaValue.FromInteger(-2),
                LuaValue.FromInteger(1),
                LuaValue.FromInteger(0),
                LuaValue.FromInteger(16),
                LuaValue.FromBoolean(false),
            ],
            Execute(source));
    }

    [Fact]
    public void CoercesCompleteNumericStringsForArithmetic()
    {
        const string source = """
            local integer = "12"
            local hexadecimalFloat = "0x1.ap1"
            return integer + 3, -integer, hexadecimalFloat * 2
            """;

        Assert.Equal(
            [LuaValue.FromInteger(15), LuaValue.FromInteger(-12), LuaValue.FromFloat(6.5)],
            Execute(source));
    }

    [Fact]
    public void NumericForCoercesStringsAndStopsBeforeIntegerOverflow()
    {
        const string source = """
            local sum = 0
            for i = "1", "3", "1" do sum = sum + i end
            local count = 0
            for i = 0x7ffffffffffffffe, 0x7fffffffffffffff do
                count = count + 1
            end
            return sum, count
            """;

        Assert.Equal(
            [LuaValue.FromInteger(6), LuaValue.FromInteger(2)],
            Execute(source));
    }

    [Fact]
    public void NumericForRejectsAZeroStep()
    {
        var state = new LuaState();
        var module = Compile("for i = 1, 2, 0 do end");

        var exception = Assert.Throws<LuaRuntimeException>(() =>
            new LuaInterpreter().Execute(state, state.CreateMainClosure(module)));

        Assert.Contains("step is zero", exception.ErrorValue.AsString().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyNumericForHotPathStaysWithinTheAllocationBudget()
    {
        var module = Compile("for i = 1, 10000 do end");
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        var interpreter = new LuaInterpreter();
        _ = interpreter.Execute(state, closure);

        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = interpreter.Execute(state, closure);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 16_384);
    }

    private static LuaValue[] Execute(string source, LuaState? state = null)
    {
        state ??= new LuaState();
        var module = Compile(source);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(module))
            .Values.ToArray();
    }

    private static Lunil.IR.Canonical.LuaIrModule Compile(string source)
    {
        var syntax = LuaParser.Parse(SourceText.FromUtf8(source));
        var semanticModel = LuaBinder.Bind(syntax);
        var lowering = LuaLowerer.Lower(semanticModel);
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(lowering.Module);
    }
}
