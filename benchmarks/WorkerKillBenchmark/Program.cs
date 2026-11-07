using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Orchestrator.Core;

// 200 scenarios, each against a distinct layer-0 task (no dependencies, so it can run with no
// scheduling setup): spawn a real `dotnet Orchestrator.Worker.dll single-task ... --crash-delay-ms`
// child process, wait for it to print READY_TO_COMMIT (meaning its result and side-effect row
// are written but its transaction has not yet committed), send it a real OS-level Kill(), then
// spawn a second, ordinary worker process for the exact same task. After both processes exit,
// verify: the task is Completed, it has exactly 1 SideEffectRecord (0 duplicates), and its
// result hash is the one TaskExecutor would have computed, independent of either worker's own
// claimed success.

const int Scenarios = 200;

// Matches whichever configuration this benchmark itself was built in, so `dotnet run -c
// Release` finds the Release worker build and `dotnet run` (Debug) finds the Debug one,
// rather than a hardcoded configuration silently running an unbuilt or stale binary.
#if DEBUG
const string Configuration = "Debug";
#else
const string Configuration = "Release";
#endif
var workerDllPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, $"../../../../../src/Orchestrator.Worker/bin/{Configuration}/net8.0/Orchestrator.Worker.dll"));
if (!File.Exists(workerDllPath))
{
    Console.Error.WriteLine($"Orchestrator.Worker.dll not found at {workerDllPath}; build the solution first ({Configuration} configuration).");
    Environment.Exit(1);
}

var manifest = Manifest.Build();
var layerZeroTaskIds = manifest.Tasks.Where(t => t.Layer == 0).Select(t => t.Id).Take(Scenarios).ToList();
if (layerZeroTaskIds.Count < Scenarios)
{
    throw new InvalidOperationException($"need {Scenarios} layer-0 tasks, manifest only has {layerZeroTaskIds.Count}");
}

var dbPath = Path.Combine(Path.GetTempPath(), $"worker-kill-{Guid.NewGuid():N}.db");
var connectionString = $"Data Source={dbPath}";

using (var seedDb = DbContextFactory.CreateSqlite(connectionString))
{
    await DbSeeder.SeedAsync(seedDb, manifest);
}

var scenarioResults = new List<object>();
var duplicateSideEffects = 0;
var killsDelivered = 0;
var killsConfirmedBeforeCommit = 0;

foreach (var taskId in layerZeroTaskIds)
{
    var expectedHash = TaskExecutor.Compute(manifest.Tasks.First(t => t.Id == taskId), new Dictionary<int, string>());

    var crashPsi = BuildWorkerPsi(workerDllPath, connectionString, taskId, $"crash-worker-{taskId}", crashDelayMs: 2000);
    using var crashProcess = new Process { StartInfo = crashPsi };
    var sawReadyToCommit = false;
    crashProcess.OutputDataReceived += (_, e) =>
    {
        if (e.Data == "READY_TO_COMMIT") sawReadyToCommit = true;
    };
    crashProcess.Start();
    crashProcess.BeginOutputReadLine();

    var waitStart = Stopwatch.StartNew();
    while (!sawReadyToCommit && waitStart.Elapsed < TimeSpan.FromSeconds(10))
    {
        await Task.Delay(5);
    }
    var killedBeforeCommit = sawReadyToCommit;
    if (killedBeforeCommit) killsConfirmedBeforeCommit++;

    crashProcess.Kill(entireProcessTree: true);
    killsDelivered++;
    try { crashProcess.WaitForExit(5000); } catch { /* killed; exit code is irrelevant here */ }

    var retryPsi = BuildWorkerPsi(workerDllPath, connectionString, taskId, $"retry-worker-{taskId}", crashDelayMs: null);
    using var retryProcess = Process.Start(retryPsi)!;
    retryProcess.WaitForExit();

    using var checkDb = DbContextFactory.CreateSqlite(connectionString);
    var sideEffectCount = await checkDb.SideEffects.CountAsync(s => s.TaskId == taskId);
    var record = await checkDb.Tasks.FirstAsync(t => t.Id == taskId);
    var isDuplicate = sideEffectCount != 1;
    if (isDuplicate) duplicateSideEffects++;

    scenarioResults.Add(new
    {
        task_id = taskId,
        killed_before_commit_confirmed = killedBeforeCommit,
        side_effect_count = sideEffectCount,
        duplicate = isDuplicate,
        final_status = record.Status.ToString(),
        result_hash_matches_expected = record.ResultHash == expectedHash,
    });
}

var report = new
{
    claim = "200/200 injected worker kills gave 0 duplicate side effects",
    scenarios = Scenarios,
    kills_delivered = killsDelivered,
    kills_confirmed_before_commit = killsConfirmedBeforeCommit,
    duplicate_side_effects = duplicateSideEffects,
    all_tasks_completed = scenarioResults.Cast<dynamic>().All(r => (string)r.final_status == "Completed"),
    all_result_hashes_match_expected = scenarioResults.Cast<dynamic>().All(r => (bool)r.result_hash_matches_expected),
    meets_claim = duplicateSideEffects == 0 && killsConfirmedBeforeCommit == Scenarios,
};

Console.WriteLine(JsonSerializer.Serialize(new { summary = report, scenarios = scenarioResults }, new JsonSerializerOptions { WriteIndented = true }));

try { File.Delete(dbPath); } catch { /* best effort cleanup */ }

static ProcessStartInfo BuildWorkerPsi(string workerDllPath, string connectionString, int taskId, string workerId, int? crashDelayMs)
{
    var args = $"\"{workerDllPath}\" single-task --connection \"{connectionString}\" --provider sqlite --task-id {taskId} --worker-id {workerId}";
    if (crashDelayMs is int ms) args += $" --crash-delay-ms {ms}";
    return new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = args,
        RedirectStandardOutput = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
}
