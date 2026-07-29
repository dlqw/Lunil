namespace Lunil.Fuzz.Fixture;

public static class FuzzBindingTarget
{
    public static long Add(long left, long right) => checked(left + right);

    public static bool Negate(bool value) => !value;

    public static string Echo(string value) => value;

    public static long Hidden() => 13;
}
