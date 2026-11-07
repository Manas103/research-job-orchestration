namespace Orchestrator.Core;

public enum OrchestrationStatus
{
    Pending = 0,
    Claimed = 1,
    Completed = 2,
}

/// <summary>
/// The durable record of one task. ResultHash is null until Completed; it and Status are
/// written together, in the same database transaction as the row in <see cref="SideEffectRecord"/>
/// that marks the task's one external side effect as applied, which is what makes the pair
/// crash-safe: either both commit, or neither does.
/// </summary>
public sealed class TaskRecord
{
    public int Id { get; set; }
    public int PipelineId { get; set; }
    public int LocalId { get; set; }
    public int Layer { get; set; }
    public OrchestrationStatus Status { get; set; }
    public string? ResultHash { get; set; }
    public string? ClaimedBy { get; set; }
    public int Attempts { get; set; }
}

/// <summary>Composite-keyed edge: TaskId depends on DependsOnTaskId.</summary>
public sealed class TaskDependency
{
    public int TaskId { get; set; }
    public int DependsOnTaskId { get; set; }
}

/// <summary>
/// The idempotency marker: a unique index on TaskId means a second attempt to apply the same
/// task's side effect fails at the database level with a constraint violation, never silently
/// double-applying. AppliedBy records which worker instance actually committed it, for the
/// worker-kill benchmark's own bookkeeping.
/// </summary>
public sealed class SideEffectRecord
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public string AppliedBy { get; set; } = "";
    public DateTime AppliedAtUtc { get; set; }
}

/// <summary>One full run of the manifest to completion, for the determinism benchmark.</summary>
public sealed class PipelineRunRecord
{
    public int Id { get; set; }
    public string ManifestDigest { get; set; } = "";
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? FinalStateDigest { get; set; }
}
