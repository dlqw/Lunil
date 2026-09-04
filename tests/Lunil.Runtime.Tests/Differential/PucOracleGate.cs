namespace Lunil.Runtime.Tests.Differential;

/// <summary>
/// Differential tests skip when the PUC Lua oracle is not installed on the machine.
/// CI sets <c>LUNIL_REQUIRE_PUC_ORACLE=1</c> on runners that install the oracle so a
/// missing binary fails loudly instead of silently zeroing differential coverage.
/// </summary>
internal static class PucOracleGate
{
    public static void RefuseIfRequired(string oracle)
    {
        if (Environment.GetEnvironmentVariable("LUNIL_REQUIRE_PUC_ORACLE") == "1")
        {
            throw new InvalidOperationException(
                $"LUNIL_REQUIRE_PUC_ORACLE=1 but '{oracle}' is unavailable; " +
                "the differential suite would silently skip and report false confidence.");
        }
    }
}
