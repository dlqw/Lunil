using System.Globalization;
using System.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaOsLibrary
{
    public static LuaTable Install(LuaState state, LuaStandardLibraryOptions? options)
    {
        if (options is not null)
        {
            LuaStandardLibraryContext.Configure(state, options);
        }

        var module = state.CreateTable();
        LuaLibraryHelpers.SetFunction(state, module, "clock", Clock);
        LuaLibraryHelpers.SetFunction(state, module, "date", Date);
        LuaLibraryHelpers.SetFunction(state, module, "difftime", DifferenceTime);
        LuaLibraryHelpers.SetFunction(state, module, "execute", Execute);
        LuaLibraryHelpers.SetFunction(state, module, "exit", Exit);
        LuaLibraryHelpers.SetFunction(state, module, "getenv", GetEnvironmentVariable);
        LuaLibraryHelpers.SetFunction(state, module, "remove", Remove);
        LuaLibraryHelpers.SetFunction(state, module, "rename", Rename);
        LuaLibraryHelpers.SetFunction(state, module, "setlocale", SetLocale);
        LuaLibraryHelpers.SetFunction(state, module, "time", Time);
        LuaLibraryHelpers.SetFunction(state, module, "tmpname", TemporaryName);
        LuaLibraryHelpers.Set(state, state.Globals, "os", LuaValue.FromTable(module));
        return module;
    }

    private static LuaValue[] Clock(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromFloat(LuaStandardLibraryContext.Get(state).Options.OperatingSystem.Clock)];

    private static LuaValue[] Date(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var format = arguments.Length == 0 || arguments[0].IsNil
            ? "%c"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 0, "date"));
        var system = LuaStandardLibraryContext.Get(state).Options.OperatingSystem;
        var utc = format.StartsWith('!');
        if (utc)
        {
            format = format[1..];
        }

        DateTimeOffset instant;
        if (arguments.Length < 2 || arguments[1].IsNil)
        {
            instant = system.Now;
        }
        else
        {
            var seconds = LuaLibraryHelpers.CheckInteger(arguments, 1, "date");
            try
            {
                instant = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
                return [LuaValue.Nil];
            }
        }

        var date = utc
            ? instant.UtcDateTime
            : TimeZoneInfo.ConvertTime(instant, system.LocalTimeZone).DateTime;
        if (format == "*t")
        {
            return [LuaValue.FromTable(CreateDateTable(state, date, utc ? null : system.LocalTimeZone))];
        }

        return [LuaLibraryHelpers.String(state, FormatDate(date, format, utc ? null : system.LocalTimeZone))];
    }

    private static LuaValue[] DifferenceTime(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var end = LuaLibraryHelpers.CheckNumber(arguments, 0, "difftime");
        var beginning = LuaLibraryHelpers.CheckNumber(arguments, 1, "difftime");
        return [LuaValue.FromFloat(end - beginning)];
    }

    private static LuaValue[] Execute(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        string? command = null;
        if (arguments.Length > 0 && !arguments[0].IsNil)
        {
            command = Encoding.UTF8.GetString(
                LuaLibraryHelpers.CheckStringBytes(arguments, 0, "execute"));
        }

        try
        {
            var result = LuaStandardLibraryContext.Get(state).Options.OperatingSystem.Execute(command);
            if (command is null)
            {
                return [LuaValue.FromBoolean(result.Started)];
            }

            return result.Started && result.Status == 0
                ? [LuaValue.FromBoolean(true), LuaLibraryHelpers.String(state, result.Kind), LuaValue.FromInteger(0)]
                : [LuaValue.Nil, LuaLibraryHelpers.String(state, result.Kind), LuaValue.FromInteger(result.Status)];
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return
            [
                LuaValue.Nil,
                LuaLibraryHelpers.String(state, exception.Message),
                LuaValue.FromInteger(exception.HResult & 0xffff),
            ];
        }
    }

    private static LuaValue[] Exit(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var status = 0;
        if (arguments.Length > 0 && !arguments[0].IsNil)
        {
            status = arguments[0].Kind == LuaValueKind.Boolean
                ? arguments[0].AsBoolean() ? 0 : 1
                : checked((int)LuaLibraryHelpers.CheckInteger(arguments, 0, "exit"));
        }

        var close = arguments.Length > 1 && arguments[1].IsTruthy;
        LuaStandardLibraryContext.Get(state).Options.OperatingSystem.Terminate(status, close);
        return [];
    }

    private static LuaValue[] GetEnvironmentVariable(
        LuaState state,
        ReadOnlySpan<LuaValue> arguments)
    {
        var name = Encoding.UTF8.GetString(
            LuaLibraryHelpers.CheckStringBytes(arguments, 0, "getenv"));
        var value = LuaStandardLibraryContext.Get(state).Options.Environment.GetEnvironmentVariable(name);
        return [value is null ? LuaValue.Nil : LuaLibraryHelpers.String(state, value)];
    }

    private static LuaValue[] Remove(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var path = Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 0, "remove"));
        try
        {
            LuaStandardLibraryContext.Get(state).Options.FileSystem.Delete(path);
            return [LuaValue.FromBoolean(true)];
        }
        catch (Exception exception) when (IsFileException(exception))
        {
            return FileFailure(state, exception, path);
        }
    }

    private static LuaValue[] Rename(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var from = Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 0, "rename"));
        var to = Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "rename"));
        try
        {
            LuaStandardLibraryContext.Get(state).Options.FileSystem.Move(from, to);
            return [LuaValue.FromBoolean(true)];
        }
        catch (Exception exception) when (IsFileException(exception))
        {
            return FileFailure(state, exception, from);
        }
    }

    private static LuaValue[] SetLocale(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        string? locale = null;
        if (arguments.Length > 0 && !arguments[0].IsNil)
        {
            locale = Encoding.UTF8.GetString(
                LuaLibraryHelpers.CheckStringBytes(arguments, 0, "setlocale"));
        }

        var category = arguments.Length < 2 || arguments[1].IsNil
            ? "all"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "setlocale"));
        if (category is not ("all" or "collate" or "ctype" or "monetary" or "numeric" or "time"))
        {
            throw LuaLibraryHelpers.BadArgument("setlocale", 1, "invalid option");
        }

        var result = LuaStandardLibraryContext.Get(state).Options.OperatingSystem
            .SetLocale(locale, category);
        return [result is null ? LuaValue.Nil : LuaLibraryHelpers.String(state, result)];
    }

    private static readonly string[] TimeReadFields =
        ["year", "month", "day", "hour", "min", "sec", "isdst"];

    private static readonly string[] TimeWriteFields =
        ["year", "month", "day", "hour", "min", "sec", "yday", "wday", "isdst"];

    private static LuaNativeStep Time(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        var system = LuaStandardLibraryContext.Get(context.State).Options.OperatingSystem;
        if (continuationId == 0 && (values.Length == 0 || values[0].IsNil))
        {
            return LuaNativeStep.Completed(LuaValue.FromInteger(system.Now.ToUnixTimeSeconds()));
        }

        LuaValue tableValue;
        var fieldIndex = 0;
        var collected = new List<LuaValue>();
        if (continuationId == 0)
        {
            tableValue = LuaLibraryHelpers.Required(values, 0, "time");
            if (tableValue.Kind != LuaValueKind.Table)
            {
                throw LuaLibraryHelpers.BadArgument(
                    "time", 0, $"table expected, got {LuaLibraryHelpers.TypeName(tableValue)}");
            }
        }
        else if (continuationId == 1)
        {
            tableValue = context.InvocationState[0];
            fieldIndex = checked((int)context.InvocationState[1].AsInteger());
            collected.AddRange(context.InvocationState.Skip(2));
            collected.Add(values.Length == 0 ? LuaValue.Nil : values[0]);
        }
        else
        {
            return ContinueTimeWrites(context);
        }

        while (fieldIndex < TimeReadFields.Length)
        {
            var get = LuaRuntimeOperations.GetIndex(
                context.State,
                tableValue,
                LuaLibraryHelpers.String(context.State, TimeReadFields[fieldIndex]));
            fieldIndex++;
            if (get.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    get.Callable,
                    get.Arguments,
                    continuationId: 1,
                    stateValues: [tableValue, LuaValue.FromInteger(fieldIndex), .. collected],
                    callIsYieldable: false);
            }

            collected.Add(get.Value);
        }

        var year = DateInteger(collected[0], "year", required: true, 0);
        if (year < int.MinValue + 1900)
        {
            throw new LuaRuntimeException("field 'year' is out-of-bound");
        }

        var month = DateInteger(collected[1], "month", required: true, 0);
        var day = DateInteger(collected[2], "day", required: true, 0);
        var hour = DateInteger(collected[3], "hour", required: false, 12);
        var minute = DateInteger(collected[4], "min", required: false, 0);
        var second = DateInteger(collected[5], "sec", required: false, 0);
        try
        {
            var requested = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)
                .AddMonths(month - 1).AddDays(day - 1).AddHours(hour).AddMinutes(minute)
                .AddSeconds(second);
            var offset = SelectLocalOffset(system.LocalTimeZone, requested, collected[6]);
            var instant = new DateTimeOffset(requested, offset);
            var normalized = TimeZoneInfo.ConvertTime(instant, system.LocalTimeZone).DateTime;
            var writeValues = DateValues(
                normalized,
                system.LocalTimeZone.IsDaylightSavingTime(normalized));
            return ContinueTimeWrites(context.WithInvocationState(
            [
                tableValue,
                LuaValue.FromInteger(0),
                LuaValue.FromInteger(instant.ToUnixTimeSeconds()),
                .. writeValues,
            ]));
        }
        catch (ArgumentOutOfRangeException)
        {
            return LuaNativeStep.Completed(LuaValue.Nil);
        }
    }

    private static LuaNativeStep ContinueTimeWrites(LuaNativeCallContext context)
    {
        var state = context.InvocationState;
        var table = state[0];
        var index = checked((int)state[1].AsInteger());
        var result = state[2];
        var writeValues = state.Skip(3).ToArray();
        while (index < TimeWriteFields.Length)
        {
            var set = LuaRuntimeOperations.SetIndex(
                context.State,
                table,
                LuaLibraryHelpers.String(context.State, TimeWriteFields[index]),
                writeValues[index]);
            index++;
            if (set.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    set.Callable,
                    set.Arguments,
                    continuationId: 2,
                    stateValues:
                    [
                        table,
                        LuaValue.FromInteger(index),
                        result,
                        .. writeValues,
                    ],
                    callIsYieldable: false);
            }
        }

        return LuaNativeStep.Completed(result);
    }

    private static int DateInteger(
        LuaValue value,
        string name,
        bool required,
        int defaultValue)
    {
        if (value.IsNil && !required)
        {
            return defaultValue;
        }

        if (value.IsNil)
        {
            throw new LuaRuntimeException($"field '{name}' missing in date table");
        }

        if (!LuaValueOperations.TryToNumber(value, out var number) || !number.TryGetInteger(out var result))
        {
            throw new LuaRuntimeException($"field '{name}' is not an integer");
        }

        if (result is < int.MinValue or > int.MaxValue)
        {
            throw new LuaRuntimeException($"field '{name}' is out-of-bound");
        }

        return (int)result;
    }

    private static LuaValue[] DateValues(DateTime date, bool isDst) =>
    [
        LuaValue.FromInteger(date.Year),
        LuaValue.FromInteger(date.Month),
        LuaValue.FromInteger(date.Day),
        LuaValue.FromInteger(date.Hour),
        LuaValue.FromInteger(date.Minute),
        LuaValue.FromInteger(date.Second),
        LuaValue.FromInteger(date.DayOfYear),
        LuaValue.FromInteger((int)date.DayOfWeek + 1),
        LuaValue.FromBoolean(isDst),
    ];

    private static LuaValue[] TemporaryName(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        try
        {
            var name = LuaStandardLibraryContext.Get(state).Options.FileSystem.CreateTemporaryName();
            return [LuaLibraryHelpers.String(state, name)];
        }
        catch (Exception exception) when (IsFileException(exception))
        {
            throw new LuaRuntimeException($"unable to generate a unique filename ({exception.Message})");
        }
    }

    private static LuaTable CreateDateTable(LuaState state, DateTime date, TimeZoneInfo? zone)
    {
        var table = state.CreateTable();
        SetDateFields(state, table, date, zone?.IsDaylightSavingTime(date) ?? false);
        return table;
    }

    private static void SetDateFields(LuaState state, LuaTable table, DateTime date, bool isDst)
    {
        SetInteger(state, table, "year", date.Year);
        SetInteger(state, table, "month", date.Month);
        SetInteger(state, table, "day", date.Day);
        SetInteger(state, table, "hour", date.Hour);
        SetInteger(state, table, "min", date.Minute);
        SetInteger(state, table, "sec", date.Second);
        SetInteger(state, table, "wday", (int)date.DayOfWeek + 1);
        SetInteger(state, table, "yday", date.DayOfYear);
        LuaLibraryHelpers.Set(state, table, "isdst", LuaValue.FromBoolean(isDst));
    }

    private static TimeSpan SelectLocalOffset(
        TimeZoneInfo zone,
        DateTime date,
        LuaValue isDst)
    {
        if (!zone.IsAmbiguousTime(date))
        {
            return zone.GetUtcOffset(date);
        }

        var offsets = zone.GetAmbiguousTimeOffsets(date);
        if (isDst.Kind == LuaValueKind.Boolean)
        {
            return isDst.AsBoolean() ? offsets.Max() : offsets.Min();
        }

        return offsets[0];
    }

    private static string FormatDate(DateTime date, string format, TimeZoneInfo? zone)
    {
        var culture = CultureInfo.CurrentCulture;
        var output = new StringBuilder();
        for (var index = 0; index < format.Length; index++)
        {
            if (format[index] != '%')
            {
                output.Append(format[index]);
                continue;
            }

            if (++index >= format.Length)
            {
                throw new LuaRuntimeException("bad argument #1 to 'date' (invalid conversion specifier '%')");
            }

            var directive = format[index];
            output.Append(directive switch
            {
                '%' => "%",
                'a' => date.ToString("ddd", culture),
                'A' => date.ToString("dddd", culture),
                'b' or 'h' => date.ToString("MMM", culture),
                'B' => date.ToString("MMMM", culture),
                'c' => date.ToString(culture),
                'C' => (date.Year / 100).ToString("D2", CultureInfo.InvariantCulture),
                'd' => date.Day.ToString("D2", CultureInfo.InvariantCulture),
                'D' => date.ToString("MM/dd/yy", CultureInfo.InvariantCulture),
                'e' => date.Day.ToString(CultureInfo.InvariantCulture).PadLeft(2, ' '),
                'F' => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                'g' => ISOWeek.GetYear(date).ToString("D2", CultureInfo.InvariantCulture)[^2..],
                'G' => ISOWeek.GetYear(date).ToString("D4", CultureInfo.InvariantCulture),
                'H' => date.Hour.ToString("D2", CultureInfo.InvariantCulture),
                'I' => ((date.Hour + 11) % 12 + 1).ToString("D2", CultureInfo.InvariantCulture),
                'j' => date.DayOfYear.ToString("D3", CultureInfo.InvariantCulture),
                'm' => date.Month.ToString("D2", CultureInfo.InvariantCulture),
                'M' => date.Minute.ToString("D2", CultureInfo.InvariantCulture),
                'n' => "\n",
                'p' => date.ToString("tt", culture),
                'r' => date.ToString("hh:mm:ss tt", culture),
                'R' => date.ToString("HH:mm", CultureInfo.InvariantCulture),
                'S' => date.Second.ToString("D2", CultureInfo.InvariantCulture),
                't' => "\t",
                'T' or 'X' => date.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                'u' => (((int)date.DayOfWeek + 6) % 7 + 1).ToString(CultureInfo.InvariantCulture),
                'U' => WeekNumber(date, DayOfWeek.Sunday).ToString("D2", CultureInfo.InvariantCulture),
                'V' => ISOWeek.GetWeekOfYear(date).ToString("D2", CultureInfo.InvariantCulture),
                'w' => ((int)date.DayOfWeek).ToString(CultureInfo.InvariantCulture),
                'W' => WeekNumber(date, DayOfWeek.Monday).ToString("D2", CultureInfo.InvariantCulture),
                'x' => date.ToString("d", culture),
                'y' => (date.Year % 100).ToString("D2", CultureInfo.InvariantCulture),
                'Y' => date.Year.ToString("D4", CultureInfo.InvariantCulture),
                'z' => FormatOffset(zone?.GetUtcOffset(date) ?? TimeSpan.Zero),
                'Z' => zone is null ? "UTC" : zone.IsDaylightSavingTime(date)
                    ? zone.DaylightName
                    : zone.StandardName,
                _ => throw new LuaRuntimeException(
                    $"bad argument #1 to 'date' (invalid conversion specifier '%{directive}')"),
            });
        }

        return output.ToString();
    }

    private static int WeekNumber(DateTime date, DayOfWeek firstDay)
    {
        var first = new DateTime(date.Year, 1, 1);
        var daysUntil = ((int)firstDay - (int)first.DayOfWeek + 7) % 7;
        var firstWeek = first.AddDays(daysUntil);
        return date < firstWeek ? 0 : (date.DayOfYear - firstWeek.DayOfYear) / 7 + 1;
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return $"{sign}{offset.Hours:D2}{offset.Minutes:D2}";
    }

    private static void SetInteger(LuaState state, LuaTable table, string name, int value) =>
        LuaLibraryHelpers.Set(state, table, name, LuaValue.FromInteger(value));

    private static LuaValue[] FileFailure(LuaState state, Exception exception, string path) =>
    [
        LuaValue.Nil,
        LuaLibraryHelpers.String(state, $"{path}: {exception.Message}"),
        LuaValue.FromInteger(exception.HResult & 0xffff),
    ];

    private static bool IsFileException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;
}
