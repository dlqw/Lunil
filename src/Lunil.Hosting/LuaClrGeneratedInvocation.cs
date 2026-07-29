using System.Collections.Immutable;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

public sealed partial class LuaClrBridge
{
    private bool ReflectionFallbackAllowed =>
        _options.BindingMode == LuaClrBindingMode.RegistryThenReflection;

    private void EnsureReflectionFallback(Type type)
    {
        if (!ReflectionFallbackAllowed)
        {
            throw new LuaClrException(LuaClrErrorCode.TypeNotAllowed,
                $"CLR type '{type.FullName}' has no registered static binding.");
        }
    }

    private static LuaClrTypeInfo Describe(LuaClrTypeBinding binding)
    {
        var constructors = binding.Constructors.Select(static constructor =>
            new LuaClrConstructorInfo([.. constructor.Parameters.Select(static parameter =>
                parameter.ParameterType.FullName ?? parameter.ParameterType.Name)])).ToImmutableArray();
        var type = binding.ClrType;
        return new LuaClrTypeInfo(
            binding.TypeName,
            binding.AssemblyName,
            type.IsValueType,
            !type.IsAbstract && !type.IsInterface && (constructors.Length > 0 || type.IsValueType),
            constructors);
    }

    private static LuaClrMemberInfo DescribeMember(LuaClrMemberBinding member) => new(
        member.Name,
        member.Kind,
        member.IsStatic,
        member.CanRead,
        member.CanWrite,
        [.. member.Parameters.Select(static parameter =>
            parameter.ParameterType.FullName ?? parameter.ParameterType.Name)],
        member.ReturnType.FullName ?? member.ReturnType.Name);

    private LuaClrConstructorBinding? SelectConstructor(
        LuaClrTypeBinding binding,
        ReadOnlySpan<LuaValue> arguments,
        out object?[] converted)
    {
        converted = [];
        var candidates = new List<(LuaClrConstructorBinding Binding, object?[] Arguments, int Score, string Signature)>();
        foreach (var constructor in binding.Constructors)
        {
            if (constructor.Parameters.Length != arguments.Length)
            {
                continue;
            }
            var values = new object?[arguments.Length];
            var score = 0;
            var valid = true;
            for (var index = 0; index < arguments.Length; index++)
            {
                if (!TryConvert(arguments[index], constructor.Parameters[index].ParameterType,
                    out values[index], out var cost))
                {
                    valid = false;
                    break;
                }
                score += cost;
            }
            if (valid)
            {
                candidates.Add((constructor, values, score,
                    string.Join('|', constructor.Parameters.Select(static parameter =>
                        parameter.ParameterType.AssemblyQualifiedName))));
            }
        }
        var selected = candidates.OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Signature, StringComparer.Ordinal)
            .FirstOrDefault();
        converted = selected.Arguments ?? [];
        return selected.Binding;
    }

    private LuaValue GetGeneratedMember(
        LuaClrTypeBinding binding,
        LuaValue target,
        object? instance,
        string memberName,
        ReadOnlySpan<LuaValue> indexArguments)
    {
        var members = binding.Members.Where(member =>
            member.IsStatic == (instance is null) &&
            string.Equals(member.Name, memberName, StringComparison.Ordinal)).ToArray();
        if (members.Any(static member => member.Kind is LuaClrMemberKind.Method or LuaClrMemberKind.Operator))
        {
            return CreateBoundMethod(target, binding.ClrType, memberName);
        }

        var candidates = new List<(LuaClrMemberBinding Member, object?[] Arguments, int Score)>();
        foreach (var member in members.Where(static member => member.CanRead))
        {
            if (member.Parameters.Length != indexArguments.Length)
            {
                continue;
            }
            if (TryConvertGeneratedArguments(indexArguments, member.Parameters, out var converted,
                    out var score))
            {
                candidates.Add((member, converted, score));
            }
        }
        var selected = candidates.OrderBy(static candidate => candidate.Score).FirstOrDefault();
        if (selected.Member is null)
        {
            throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
                $"Generated CLR member '{binding.TypeName}.{memberName}' is not readable.");
        }
        try
        {
            return ToLuaValue(selected.Member.Invoker(instance, selected.Arguments));
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    private void SetGeneratedMember(
        LuaClrTypeBinding binding,
        object? instance,
        string memberName,
        LuaValue value)
    {
        var candidates = new List<(LuaClrMemberBinding Member, object? Value, int Score)>();
        foreach (var member in binding.Members.Where(member =>
                     member.IsStatic == (instance is null) && member.CanWrite &&
                     string.Equals(member.Name, memberName, StringComparison.Ordinal) &&
                     member.Parameters.Length == 0))
        {
            if (TryConvert(value, member.ReturnType, out var converted, out var score))
            {
                candidates.Add((member, converted, score));
            }
        }
        var selected = candidates.OrderBy(static candidate => candidate.Score).FirstOrDefault();
        if (selected.Member is null)
        {
            throw NoMatchingMember(memberName);
        }
        try
        {
            selected.Member.Invoker(instance, [selected.Value]);
        }
        catch (Exception exception)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    private LuaClrInvocationResult InvokeGeneratedMember(
        LuaClrTypeBinding binding,
        object? instance,
        string memberName,
        ReadOnlySpan<LuaValue> arguments,
        ReadOnlySpan<LuaClrNamedArgument> namedArguments)
    {
        var selected = SelectGeneratedMethod(binding.Members.Where(member =>
            member.IsStatic == (instance is null) &&
            member.Kind is LuaClrMemberKind.Method or LuaClrMemberKind.Operator &&
            string.Equals(member.Name, memberName, StringComparison.Ordinal)), arguments, namedArguments);
        if (selected is null)
        {
            throw NoMatchingMember(memberName);
        }
        try
        {
            var result = selected.Value.Member.Invoker(instance, selected.Value.Arguments);
            var positional = ImmutableArray.CreateBuilder<LuaValue>();
            var named = ImmutableArray.CreateBuilder<LuaClrRefOutValue>();
            for (var index = 0; index < selected.Value.Member.Parameters.Length; index++)
            {
                var parameter = selected.Value.Member.Parameters[index];
                if (!parameter.IsByRef)
                {
                    continue;
                }
                var value = ToLuaValue(selected.Value.Arguments[index]);
                positional.Add(value);
                named.Add(new LuaClrRefOutValue(parameter.Name, value));
            }
            return new LuaClrInvocationResult(ToLuaValue(result), positional.ToImmutable(), named.ToImmutable());
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    private (LuaClrMemberBinding Member, object?[] Arguments)? SelectGeneratedMethod(
        IEnumerable<LuaClrMemberBinding> members,
        ReadOnlySpan<LuaValue> arguments,
        ReadOnlySpan<LuaClrNamedArgument> namedArguments)
    {
        var candidates = new List<(LuaClrMemberBinding Member, object?[] Arguments, int Score, string Signature)>();
        foreach (var member in members)
        {
            var parameters = member.Parameters;
            if (arguments.Length > parameters.Length ||
                arguments.Length + namedArguments.Length > parameters.Length)
            {
                continue;
            }
            var values = new object?[parameters.Length];
            var assigned = new bool[parameters.Length];
            var score = 0;
            var valid = true;
            for (var index = 0; index < arguments.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.IsOut || !TryConvert(arguments[index], parameter.ParameterType,
                        out values[index], out var cost))
                {
                    valid = false;
                    break;
                }
                assigned[index] = true;
                score += cost;
            }
            if (!valid)
            {
                continue;
            }
            foreach (var named in namedArguments)
            {
                var parameterIndex = -1;
                for (var index = 0; index < parameters.Length; index++)
                {
                    if (string.Equals(parameters[index].Name, named.Name, StringComparison.Ordinal))
                    {
                        parameterIndex = index;
                        break;
                    }
                }
                if (parameterIndex < 0 || assigned[parameterIndex] || parameters[parameterIndex].IsOut ||
                    !TryConvert(named.Value, parameters[parameterIndex].ParameterType,
                        out values[parameterIndex], out var cost))
                {
                    valid = false;
                    break;
                }
                assigned[parameterIndex] = true;
                score += cost;
            }
            if (!valid)
            {
                continue;
            }
            for (var index = 0; index < parameters.Length; index++)
            {
                if (assigned[index])
                {
                    continue;
                }
                var parameter = parameters[index];
                if (parameter.IsOut)
                {
                    values[index] = parameter.ParameterType.IsValueType
                        ? CreateDefaultValueType(parameter.ParameterType) : null;
                    assigned[index] = true;
                }
                else if (parameter.HasDefaultValue)
                {
                    values[index] = parameter.DefaultValue;
                    assigned[index] = true;
                    score++;
                }
                else
                {
                    valid = false;
                    break;
                }
            }
            if (valid)
            {
                candidates.Add((member, values, score, string.Join('|', parameters.Select(static parameter =>
                    parameter.ParameterType.AssemblyQualifiedName))));
            }
        }
        var selected = candidates.OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Signature, StringComparer.Ordinal).FirstOrDefault();
        return selected.Member is null ? null : (selected.Member, selected.Arguments);
    }

    private bool TryConvertGeneratedArguments(
        ReadOnlySpan<LuaValue> values,
        ImmutableArray<LuaClrParameterBinding> parameters,
        out object?[] converted,
        out int score)
    {
        converted = new object?[parameters.Length];
        score = 0;
        if (values.Length != parameters.Length)
        {
            return false;
        }
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryConvert(values[index], parameters[index].ParameterType,
                out converted[index], out var cost))
            {
                converted = [];
                score = 0;
                return false;
            }
            score += cost;
        }
        return true;
    }
}
