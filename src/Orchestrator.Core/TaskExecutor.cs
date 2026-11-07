using System.Security.Cryptography;
using System.Text;

namespace Orchestrator.Core;

/// <summary>
/// The deterministic "research computation" every task performs: a pure function of the task's
/// own identity and its already-computed dependency hashes, so re-running the same manifest
/// against a fresh database always produces the same result for the same task, and a task's
/// result depends on the whole upstream subgraph, not just its own identity, the way a real
/// pipeline step's output actually depends on its inputs.
/// </summary>
public static class TaskExecutor
{
    public static string Compute(TaskNode node, IReadOnlyDictionary<int, string> dependencyHashes)
    {
        var sortedDepHashes = node.DependsOn
            .Select(id => dependencyHashes[id])
            .OrderBy(h => h, StringComparer.Ordinal);
        var canonical = $"{node.PipelineId}:{node.LocalId}:{node.Layer}:{string.Join(",", sortedDepHashes)}";
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
