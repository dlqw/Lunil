using System.Diagnostics;
using System.Globalization;
using UniLua;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Lunil.UniLua.Harness <workload.lua> <operations> <warmup>");
    return 2;
}

var workloadPath = Path.GetFullPath(args[0]);
var operations = int.Parse(args[1], CultureInfo.InvariantCulture);
var warmupCalls = int.Parse(args[2], CultureInfo.InvariantCulture);
var source = File.ReadAllText(workloadPath)
    .Replace("local checksum = 0\n", "local checksum = 0.0\n", StringComparison.Ordinal)
    .Replace("local sum = 0\n", "local sum = 0.0\n", StringComparison.Ordinal)
    .Replace("local value = 0\n", "local value = 0.0\n", StringComparison.Ordinal);

var setupStarted = Process.GetCurrentProcess().TotalProcessorTime;
var lua = LuaAPI.NewState();
lua.L_OpenLibs();

var status = lua.L_LoadString(source);
if (status != ThreadStatus.LUA_OK)
{
    Console.Error.WriteLine($"UniLua load failed: {lua.ToString(-1)}");
    return 1;
}

double Run(long ops)
{
    lua.PushValue(-1);
    lua.PushNumber(ops);
    var call = lua.PCall(1, 1, 0);
    if (call != ThreadStatus.LUA_OK)
    {
        throw new InvalidOperationException($"UniLua call failed: {lua.ToString(-1)}");
    }
    var result = lua.ToNumber(-1);
    lua.Pop(1);
    return result;
}

for (var i = 0; i < warmupCalls; i++)
{
    _ = Run(1);
}

var setup = (Process.GetCurrentProcess().TotalProcessorTime - setupStarted).TotalSeconds;
var started = Process.GetCurrentProcess().TotalProcessorTime;
var value = Run(operations);
var elapsed = (Process.GetCurrentProcess().TotalProcessorTime - started).TotalSeconds;

Console.WriteLine(
    string.Format(
        CultureInfo.InvariantCulture,
        "cross_runtime_result\telapsed={0:G17}\tsetup={1:G17}\toperations={2}\tresult={3:G17}\tjit_enabled=0",
        elapsed,
        setup,
        operations,
        value));
return 0;
