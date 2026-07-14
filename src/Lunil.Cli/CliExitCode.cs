namespace Lunil.Cli;

internal enum CliExitCode
{
    Success = 0,
    Diagnostics = 1,
    Usage = 2,
    InputOutput = 3,
    Execution = 4,
    Build = 5,
    Cancelled = 130,
}
