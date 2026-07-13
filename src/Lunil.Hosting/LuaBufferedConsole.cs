using Lunil.StandardLibrary;

namespace Lunil.Hosting;

/// <summary>A binary-safe in-memory console suitable for restricted and deterministic hosts.</summary>
public sealed class LuaBufferedConsole : ILuaConsole
{
    private readonly object _sync = new();
    private readonly byte[] _standardInput;
    private readonly List<byte> _standardOutput = [];
    private readonly List<byte> _standardError = [];

    public LuaBufferedConsole(ReadOnlySpan<byte> standardInput = default)
    {
        _standardInput = standardInput.ToArray();
    }

    public byte[] ReadStandardInput() => (byte[])_standardInput.Clone();

    public void Write(ReadOnlyMemory<byte> bytes)
    {
        lock (_sync)
        {
            _standardOutput.AddRange(bytes.Span);
        }
    }

    public void WriteLine()
    {
        lock (_sync)
        {
            _standardOutput.Add((byte)'\n');
        }
    }

    public void WriteError(ReadOnlyMemory<byte> bytes)
    {
        lock (_sync)
        {
            _standardError.AddRange(bytes.Span);
        }
    }

    public byte[] GetStandardOutput()
    {
        lock (_sync)
        {
            return [.. _standardOutput];
        }
    }

    public byte[] GetStandardError()
    {
        lock (_sync)
        {
            return [.. _standardError];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _standardOutput.Clear();
            _standardError.Clear();
        }
    }
}
