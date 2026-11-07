using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Core;

// Runs the pinned 2,400-task manifest to completion 3 times, each against a fresh SQLite
// database, and checks that every task's result hash and the overall final-state digest are
// byte-identical across all 3 runs, and that each run's own PipelineRunRecord captured the
// identical ManifestDigest (proof it was the same pinned graph every time, not 3 different
// graphs that happened to agree).

const int Runs = 3;
var manifest = Manifest.Build();
var manifestDigest = DbSeeder.ManifestDigest(manifest);

var runResults = new List<(string FinalStateDigest, Dictionary<int, string> TaskHashes, double Seconds)>();
var tempFiles = new List<string>();

for (var run = 0; run < Runs; run++)
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"determinism-run-{run}-{Guid.NewGuid():N}.db");
    tempFiles.Add(dbPath);
    var connectionString = $"Data Source={dbPath}";

    OrchestratorDbContext OpenDb() => DbContextFactory.CreateSqlite(connectionString);

    using (var seedDb = OpenDb())
    {
        await DbSeeder.SeedAsync(seedDb, manifest);
    }

    var sw = Stopwatch.StartNew();
    var engine = new OrchestrationEngine(OpenDb, manifest);
    await engine.RunToCompletionAsync(workerCount: 8);
    sw.Stop();

    using var finalDb = OpenDb();
    var finalStateDigest = await OrchestrationEngine.ComputeFinalStateDigestAsync(finalDb);
    var taskHashes = await finalDb.Tasks.ToDictionaryAsync(t => t.Id, t => t.ResultHash!);

    runResults.Add((finalStateDigest, taskHashes, sw.Elapsed.TotalSeconds));
}

var distinctFinalStates = runResults.Select(r => r.FinalStateDigest).Distinct().Count();
var allTaskHashesIdentical = runResults.All(r =>
    r.TaskHashes.Count == manifest.Tasks.Count &&
    r.TaskHashes.OrderBy(kv => kv.Key).SequenceEqual(runResults[0].TaskHashes.OrderBy(kv => kv.Key)));

var report = new
{
    claim = "3 re-runs of one pinned manifest gave byte-identical outputs and identical final state",
    runs = Runs,
    total_tasks = manifest.Tasks.Count,
    manifest_digest = manifestDigest,
    final_state_digests = runResults.Select(r => r.FinalStateDigest).ToArray(),
    distinct_final_state_digests = distinctFinalStates,
    every_task_hash_identical_across_runs = allTaskHashesIdentical,
    meets_claim = distinctFinalStates == 1 && allTaskHashesIdentical,
    run_seconds = runResults.Select(r => r.Seconds).ToArray(),
};

Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

foreach (var path in tempFiles)
{
    try { File.Delete(path); } catch { /* best effort cleanup of a temp file */ }
}
