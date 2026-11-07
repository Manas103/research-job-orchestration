using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Orchestrator.Core;

/// <summary>
/// Drives the DAG to completion against whatever store <paramref name="dbFactory"/> opens.
/// Every method opens its own short-lived <see cref="OrchestratorDbContext"/> rather than
/// sharing one across calls, so the engine behaves the same whether the caller is a single
/// in-process loop (the determinism benchmark) or several real OS processes polling the same
/// database (the real worker, the worker-kill benchmark).
/// </summary>
public sealed class OrchestrationEngine
{
    private readonly Func<OrchestratorDbContext> _dbFactory;
    private readonly Manifest _manifest;

    public OrchestrationEngine(Func<OrchestratorDbContext> dbFactory, Manifest manifest)
    {
        _dbFactory = dbFactory;
        _manifest = manifest;
    }

    /// <summary>
    /// Scans for a pending task whose dependencies are all completed and atomically claims it
    /// (a single ExecuteUpdate whose WHERE clause re-checks Status = Pending), so two workers
    /// racing on the same candidate never both believe they claimed it. Returns null when
    /// nothing is currently claimable (either everything is done, or every remaining task is
    /// still waiting on an in-flight dependency).
    /// </summary>
    public async Task<int?> TryClaimNextRunnableTaskAsync(string workerId)
    {
        using var db = _dbFactory();
        var completedIds = await db.Tasks
            .Where(t => t.Status == OrchestrationStatus.Completed)
            .Select(t => t.Id)
            .ToListAsync();
        var completed = new HashSet<int>(completedIds);

        foreach (var task in _manifest.Tasks)
        {
            if (completed.Contains(task.Id)) continue;
            if (!task.DependsOn.All(completed.Contains)) continue;

            var rows = await db.Tasks
                .Where(t => t.Id == task.Id && t.Status == OrchestrationStatus.Pending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, OrchestrationStatus.Claimed)
                    .SetProperty(t => t.ClaimedBy, workerId));
            if (rows == 1)
            {
                return task.Id;
            }
            // Someone else claimed it between the scan above and this update; try the next
            // candidate rather than retrying this one, since another worker is already on it.
        }
        return null;
    }

    /// <summary>
    /// Executes one task and commits its result and its one side effect in a single
    /// transaction, so a process killed between the write and the commit leaves nothing
    /// behind: a later attempt at the same task either finds the prior attempt's work fully
    /// committed (the SideEffects insert below fails its unique constraint, caught, no
    /// duplicate applied) or finds nothing at all and applies it cleanly. <paramref
    /// name="crashDelayMs"/> and <paramref name="onReadyToCommit"/> exist only so the
    /// worker-kill benchmark can reliably kill the process inside that window; production and
    /// the determinism benchmark never pass them.
    /// </summary>
    public async Task<string> ExecuteAndCompleteTaskAsync(
        int taskId, string workerId, int? crashDelayMs = null, Action? onReadyToCommit = null)
    {
        var node = _manifest.Tasks.First(t => t.Id == taskId);
        using var db = _dbFactory();

        var dependencyHashes = new Dictionary<int, string>();
        if (node.DependsOn.Count > 0)
        {
            var rows = await db.Tasks
                .Where(t => node.DependsOn.Contains(t.Id))
                .Select(t => new { t.Id, t.ResultHash })
                .ToListAsync();
            foreach (var row in rows)
            {
                dependencyHashes[row.Id] = row.ResultHash!;
            }
        }
        var resultHash = TaskExecutor.Compute(node, dependencyHashes);

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            db.SideEffects.Add(new SideEffectRecord { TaskId = taskId, AppliedBy = workerId, AppliedAtUtc = DateTime.UtcNow });
            var record = await db.Tasks.FirstAsync(t => t.Id == taskId);
            record.Status = OrchestrationStatus.Completed;
            record.ResultHash = resultHash;
            record.Attempts += 1;
            await db.SaveChangesAsync();

            if (crashDelayMs is int ms)
            {
                onReadyToCommit?.Invoke();
                await Task.Delay(ms);
            }

            await transaction.CommitAsync();
            return resultHash;
        }
        catch (DbUpdateException)
        {
            // The unique index on SideEffectRecord.TaskId rejected a second insert: a prior
            // attempt's transaction already committed both the side effect and the completed
            // status together. Roll back this attempt's uncommitted work and read back what
            // the prior attempt actually produced.
            await transaction.RollbackAsync();
            var existing = await db.Tasks.FirstAsync(t => t.Id == taskId);
            return existing.ResultHash!;
        }
    }

    /// <summary>Runs every task in the manifest to completion using <paramref
    /// name="workerCount"/> concurrent in-process loops.</summary>
    public async Task RunToCompletionAsync(int workerCount)
    {
        var total = _manifest.Tasks.Count;
        var completed = 0;
        var workers = Enumerable.Range(0, workerCount).Select(async i =>
        {
            var workerId = $"inproc-{i}";
            while (Volatile.Read(ref completed) < total)
            {
                var taskId = await TryClaimNextRunnableTaskAsync(workerId);
                if (taskId is null)
                {
                    await Task.Delay(2);
                    continue;
                }
                await ExecuteAndCompleteTaskAsync(taskId.Value, workerId);
                Interlocked.Increment(ref completed);
            }
        });
        await Task.WhenAll(workers);
    }

    /// <summary>A single hash over every task's (Id, ResultHash) pair, sorted by Id: identical
    /// across two runs if and only if every task produced the exact same result.</summary>
    public static async Task<string> ComputeFinalStateDigestAsync(OrchestratorDbContext db)
    {
        var rows = await db.Tasks.OrderBy(t => t.Id).Select(t => new { t.Id, t.ResultHash }).ToListAsync();
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(row.Id).Append(':').Append(row.ResultHash).Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
