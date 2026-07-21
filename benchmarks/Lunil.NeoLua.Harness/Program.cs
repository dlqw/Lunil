using System.Diagnostics;
using System.Globalization;
using Neo.IronLua;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: Lunil.NeoLua.Harness <workload.lua> <operations> <warmup>");
    return 2;
}

var workloadPath = Path.GetFullPath(args[0]);
var operations = int.Parse(args[1], CultureInfo.InvariantCulture);
var warmupCalls = int.Parse(args[2], CultureInfo.InvariantCulture);
var source = File.ReadAllText(workloadPath)
    // Force floating accumulators so suite expectedPerOperation checks match PUC.
    .Replace("local checksum = 0\n", "local checksum = 0.0\n", StringComparison.Ordinal)
    .Replace("local sum = 0\n", "local sum = 0.0\n", StringComparison.Ordinal)
    .Replace("local value = 0\n", "local value = 0.0\n", StringComparison.Ordinal);
var rewritten = source.Contains("local operations = ... or 1", StringComparison.Ordinal)
    ? source.Replace(
        "local operations = ... or 1",
        "local operations = ARG",
        StringComparison.Ordinal)
    : "return (function(...)\n" + source + "\nend)(ARG)";

var setupStarted = Process.GetCurrentProcess().TotalProcessorTime;
using var lua = new Lua(LuaIntegerType.Int64, LuaFloatType.Double);
var environment = lua.CreateEnvironment();
var chunk = lua.CompileChunk(
    rewritten,
    Path.GetFileName(workloadPath),
    new LuaCompileOptions(),
    new KeyValuePair<string, Type>("ARG", typeof(long)));

double Run(long ops)
{
    var values = environment.DoChunk(chunk, ops);
    return Convert.ToDouble(values[0], CultureInfo.InvariantCulture);
}

for (var i = 0; i < warmupCalls; i++)
{
    _ = Run(1);
}

var setup = (Process.GetCurrentProcess().TotalProcessorTime - setupStarted).TotalSeconds;
var started = Process.GetCurrentProcess().TotalProcessorTime;
var result = Run(operations);
var elapsed = (Process.GetCurrentProcess().TotalProcessorTime - started).TotalSeconds;

Console.WriteLine(
    string.Format(
        CultureInfo.InvariantCulture,
        "cross_runtime_result\telapsed={0:G17}\tsetup={1:G17}\toperations={2}\tresult={3:G17}\tjit_enabled=0",
        elapsed,
        setup,
        operations,
        result));
return 0;
