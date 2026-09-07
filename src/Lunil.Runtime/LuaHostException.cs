namespace Lunil.Runtime;

/// <summary>
/// Base for host-domain exceptions that must cross the Lua execution boundary unchanged.
/// Native bodies may throw arbitrary CLR exceptions, which the scheduler contains as Lua
/// errors so a script's <c>pcall</c> can guard them. Exceptions derived from this base are
/// host-level contract violations instead: they keep their typed CLR identity when they
/// unwind out of the virtual machine so programmatic hosts observe the original type.
/// </summary>
public abstract class LuaHostException : Exception
{
    protected LuaHostException(string message)
        : base(message)
    {
    }

    protected LuaHostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
