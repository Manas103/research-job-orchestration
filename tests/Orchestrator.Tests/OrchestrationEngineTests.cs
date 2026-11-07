using Microsoft.EntityFrameworkCore;
using Orchestrator.Core;

namespace Orchestrator.Tests;

public class OrchestrationEngineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"orchestrator-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    private OrchestratorDbContext OpenDb() => DbContextFactory.CreateSqlite(ConnectionString);

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private async Task<Manifest> SeedSmallManifestAsync()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 3, tasksPerLayer: 4, seed: 7);
        using var db = OpenDb();
        await DbSeeder.SeedAsync(db, manifest);
        return manifest;
    }

    [Fact]
    public async Task RunToCompletionAsync_completes_every_task_exactly_once()
    {
        var manifest = await SeedSmallManifestAsync();
        var engine = new OrchestrationEngine(OpenDb, manifest);
        await engine.RunToCompletionAsync(workerCount: 4);

        using var db = OpenDb();
        var tasks = await db.Tasks.ToListAsync();
        Assert.Equal(manifest.Tasks.Count, tasks.Count);
        Assert.All(tasks, t => Assert.Equal(OrchestrationStatus.Completed, t.Status));
        Assert.All(tasks, t => Assert.NotNull(t.ResultHash));

        var sideEffectCounts = await db.SideEffects.GroupBy(s => s.TaskId).Select(g => g.Count()).ToListAsync();
        Assert.All(sideEffectCounts, c => Assert.Equal(1, c));
    }

    [Fact]
    public async Task RunToCompletionAsync_two_fresh_databases_produce_the_same_final_state_digest()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 3, tasksPerLayer: 4, seed: 7);

        var pathA = Path.Combine(Path.GetTempPath(), $"digest-a-{Guid.NewGuid():N}.db");
        var pathB = Path.Combine(Path.GetTempPath(), $"digest-b-{Guid.NewGuid():N}.db");
        try
        {
            OrchestratorDbContext OpenA() => DbContextFactory.CreateSqlite($"Data Source={pathA}");
            OrchestratorDbContext OpenB() => DbContextFactory.CreateSqlite($"Data Source={pathB}");

            using (var seedA = OpenA()) await DbSeeder.SeedAsync(seedA, manifest);
            using (var seedB = OpenB()) await DbSeeder.SeedAsync(seedB, manifest);

            await new OrchestrationEngine(OpenA, manifest).RunToCompletionAsync(workerCount: 3);
            await new OrchestrationEngine(OpenB, manifest).RunToCompletionAsync(workerCount: 5);

            using var finalA = OpenA();
            using var finalB = OpenB();
            var digestA = await OrchestrationEngine.ComputeFinalStateDigestAsync(finalA);
            var digestB = await OrchestrationEngine.ComputeFinalStateDigestAsync(finalB);
            Assert.Equal(digestA, digestB);
        }
        finally
        {
            try { File.Delete(pathA); } catch { }
            try { File.Delete(pathB); } catch { }
        }
    }

    [Fact]
    public async Task TryClaimNextRunnableTaskAsync_never_claims_a_task_whose_dependency_is_still_pending()
    {
        var manifest = await SeedSmallManifestAsync();
        var engine = new OrchestrationEngine(OpenDb, manifest);

        // Drain only layer-0 claims by repeatedly claiming until a non-layer-0 id would be the
        // only thing left; every claimed id at this point must be layer 0, since nothing else's
        // dependencies can be satisfied yet (nothing has been completed).
        var claimed = new List<int>();
        for (var i = 0; i < manifest.Tasks.Count(t => t.Layer == 0); i++)
        {
            var id = await engine.TryClaimNextRunnableTaskAsync($"w{i}");
            Assert.NotNull(id);
            claimed.Add(id!.Value);
        }
        Assert.All(claimed, id => Assert.Equal(0, manifest.Tasks.First(t => t.Id == id).Layer));

        // With every layer-0 task merely Claimed (not yet Completed), no layer-1 task is
        // runnable yet.
        var next = await engine.TryClaimNextRunnableTaskAsync("w-extra");
        Assert.Null(next);
    }

    [Fact]
    public async Task ExecuteAndCompleteTaskAsync_called_twice_for_the_same_task_applies_the_side_effect_once()
    {
        var manifest = await SeedSmallManifestAsync();
        var engine = new OrchestrationEngine(OpenDb, manifest);
        var leafTaskId = manifest.Tasks.First(t => t.Layer == 0).Id;

        var firstHash = await engine.ExecuteAndCompleteTaskAsync(leafTaskId, "worker-a");
        var secondHash = await engine.ExecuteAndCompleteTaskAsync(leafTaskId, "worker-b");
        Assert.Equal(firstHash, secondHash);

        using var db = OpenDb();
        var count = await db.SideEffects.CountAsync(s => s.TaskId == leafTaskId);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task OnReadyToCommit_callback_fires_before_the_transaction_commits()
    {
        // The worker-kill benchmark's entire timing strategy depends on this: the callback
        // must fire strictly before the row is visible to another connection, or killing the
        // process "after" the callback would sometimes be killing it after a real commit.
        var manifest = await SeedSmallManifestAsync();
        var engine = new OrchestrationEngine(OpenDb, manifest);
        var leafTaskId = manifest.Tasks.First(t => t.Layer == 0).Id;

        var callbackFired = false;
        var visibleToOtherConnectionAtCallbackTime = true;
        var task = engine.ExecuteAndCompleteTaskAsync(leafTaskId, "w", crashDelayMs: 200, onReadyToCommit: () =>
        {
            callbackFired = true;
            using var otherConnection = OpenDb();
            visibleToOtherConnectionAtCallbackTime = otherConnection.SideEffects.Any(s => s.TaskId == leafTaskId);
        });
        await task;

        Assert.True(callbackFired);
        Assert.False(visibleToOtherConnectionAtCallbackTime);
    }
}
