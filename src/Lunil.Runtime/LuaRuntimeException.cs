using Lunil.Runtime.Values;

namespace Lunil.Runtime;

public sealed class LuaRuntimeException : Exception
{
    private readonly LuaValue _errorValue;

    public LuaRuntimeException(string message)
        : base(message)
    {
    }

    public LuaRuntimeException(LuaValue errorValue)
        : base(errorValue.ToString())
    {
        _errorValue = errorValue;
        HasErrorValue = true;
    }

    public LuaRuntimeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal LuaRuntimeException(string message, bool bypassProtectedNativeCallback)
        : base(message)
    {
        BypassProtectedNativeCallback = bypassProtectedNativeCallback;
    }

    public bool HasErrorValue { get; }

    internal bool BypassProtectedNativeCallback { get; }

    public LuaValue ErrorValue => HasErrorValue
        ? _errorValue
        : throw new InvalidOperationException("The exception does not carry a Lua error value.");
}
