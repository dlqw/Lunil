using System.Text;
using System.Globalization;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaLibraryHelpers
{
    public static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(value)));

    public static void Set(LuaState state, LuaTable table, string name, LuaValue value) =>
        table.Set(String(state, name), value);

    public static void SetFunction(
        LuaState state,
        LuaTable table,
        string name,
        LuaNativeFunctionBody body) =>
        Set(state, table, name, LuaValue.FromFunction(new LuaNativeFunction(name, body)));

    public static void SetFunction(
        LuaState state,
        LuaTable table,
        string name,
        LuaNativeFunctionStepBody body) =>
        Set(state, table, name, LuaValue.FromFunction(new LuaNativeFunction(name, body)));

    public static LuaValue Required(ReadOnlySpan<LuaValue> arguments, int index, string function)
    {
        if ((uint)index >= (uint)arguments.Length)
        {
            throw BadArgument(function, index, "value expected");
        }

        return arguments[index];
    }

    public static double CheckNumber(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = Required(arguments, index, function);
        if (LuaValueOperations.TryToNumber(value, out var number))
        {
            return number.AsFloat();
        }

        throw BadArgument(function, index, $"number expected, got {TypeName(value)}");
    }

    public static long CheckInteger(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = Required(arguments, index, function);
        if (LuaValueOperations.TryToNumber(value, out var number) &&
            number.TryGetInteger(out var integer))
        {
            return integer;
        }

        throw BadArgument(function, index, $"number has no integer representation");
    }

    public static double OptionalNumber(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        double defaultValue,
        string function) =>
        index >= arguments.Length || arguments[index].IsNil
            ? defaultValue
            : CheckNumber(arguments, index, function);

    public static long OptionalInteger(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        long defaultValue,
        string function) =>
        index >= arguments.Length || arguments[index].IsNil
            ? defaultValue
            : CheckInteger(arguments, index, function);

    public static string TypeName(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Integer or LuaValueKind.Float => "number",
        LuaValueKind.LightUserdata => "userdata",
        _ => value.Kind.ToString().ToLowerInvariant(),
    };

    public static byte[] CheckStringBytes(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = Required(arguments, index, function);
        return value.Kind switch
        {
            LuaValueKind.String => value.AsString().ToArray(),
            LuaValueKind.Integer => Encoding.ASCII.GetBytes(
                value.AsInteger().ToString(CultureInfo.InvariantCulture)),
            LuaValueKind.Float => Encoding.ASCII.GetBytes(
                LuaValueOperations.FormatFloat(value.AsFloat())),
            _ => throw BadArgument(
                function,
                index,
                $"string expected, got {TypeName(value)}"),
        };
    }

    public static LuaRuntimeException BadArgument(string function, int zeroBasedIndex, string detail) =>
        new($"bad argument #{zeroBasedIndex + 1} to '{function}' ({detail})");
}
