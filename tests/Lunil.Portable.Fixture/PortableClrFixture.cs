namespace Lunil.Portable.Fixture;

public sealed class PortableClrFixture
{
    private readonly long _value;

    public PortableClrFixture(long value) => _value = value;

    public long Add(long amount) => _value + amount;
}
