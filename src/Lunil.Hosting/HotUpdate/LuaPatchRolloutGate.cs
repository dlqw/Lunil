namespace Lunil.Hosting;

/// <summary>
/// Serializes patch rollouts so barrier commits and ring deployments cannot overlap or
/// recurse inside one process. The process default keeps the historical single-rollout
/// guarantee across every coordinator instance; isolated hosts and tests can inject a
/// dedicated gate whose operations cannot collide with the process-wide barrier.
/// </summary>
internal sealed class LuaPatchRolloutGate
{
    internal static readonly LuaPatchRolloutGate Process = new();

    internal readonly object SyncRoot = new();

    /// <summary>Whether a rollout is currently active under this gate; guarded by <see cref="SyncRoot"/>.</summary>
    internal bool OperationActive;
}
