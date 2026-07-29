namespace Lunil.Hosting;

internal interface ILuaClrCallbackScheduler
{
    void Register(LuaClrCallbackRegistration registration);

    void Schedule(LuaClrCallbackRegistration registration, object?[] arguments);
}
