namespace Luac.Runtime;

public sealed class LuaRuntimeException : Exception
{
    public LuaRuntimeException(string message)
        : base(message)
    {
    }

    public LuaRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
