namespace Lunil.Unity.Unity6
{
    /// <summary>Unity 6-only capability marker isolated from the 2022.3 adapter assembly.</summary>
    public static class LuaUnity6Runtime
    {
        public static bool IsAvailable
        {
            get { return true; }
        }
    }
}
