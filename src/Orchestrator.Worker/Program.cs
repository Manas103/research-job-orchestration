using Orchestrator.Core;

// Two modes, both against the manifest's fixed graph:
//   run         --connection <cs> --provider sqlite|postgres [--workers N]
//       Seeds the manifest if the database is empty, then runs every pending task to
//       completion with N concurrent in-process workers.
//   single-task --connection <cs> --provider sqlite|postgres --task-id <id> --worker-id <id>
//               [--crash-delay-ms <ms>]
//       Seeds if empty, executes exactly one task. With --crash-delay-ms set, prints
//       READY_TO_COMMIT to stdout right after writing (but before committing) the task's
//       result and side effect, then sleeps; this is the window
//       scripts in WorkerKillBenchmark use to send a real OS-level kill to this exact
//       process. Without it, the task runs and commits normally.

var args0 = args.Length > 0 ? args[0] : "";
var options = ParseOptions(args);

var connectionString = options.GetValueOrDefault("connection") ?? "Data Source=orchestrator.db";
var provider = options.GetValueOrDefault("provider", "sqlite");

OrchestratorDbContext OpenDb() => provider == "postgres"
    ? DbContextFactory.CreatePostgres(connectionString)
    : DbContextFactory.CreateSqlite(connectionString);

var manifest = Manifest.Build();

switch (args0)
{
    case "run":
    {
        var workerCount = int.Parse(options.GetValueOrDefault("workers", "8"));
        using (var seedDb = OpenDb())
        {
            await DbSeeder.SeedAsync(seedDb, manifest);
        }
        var engine = new OrchestrationEngine(OpenDb, manifest);
        await engine.RunToCompletionAsync(workerCount);
        using var finalDb = OpenDb();
        var digest = await OrchestrationEngine.ComputeFinalStateDigestAsync(finalDb);
        Console.WriteLine($"RUN_COMPLETE tasks={manifest.Tasks.Count} final_state_digest={digest}");
        break;
    }
    case "single-task":
    {
        var taskId = int.Parse(options["task-id"]);
        var workerId = options.GetValueOrDefault("worker-id", $"worker-{Environment.ProcessId}");
        int? crashDelayMs = options.TryGetValue("crash-delay-ms", out var v) ? int.Parse(v) : null;

        using (var seedDb = OpenDb())
        {
            await DbSeeder.SeedAsync(seedDb, manifest);
        }
        var engine = new OrchestrationEngine(OpenDb, manifest);
        var hash = await engine.ExecuteAndCompleteTaskAsync(
            taskId,
            workerId,
            crashDelayMs,
            onReadyToCommit: () =>
            {
                Console.WriteLine("READY_TO_COMMIT");
                Console.Out.Flush();
            });
        Console.WriteLine($"TASK_COMPLETE task_id={taskId} result_hash={hash}");
        break;
    }
    default:
        Console.Error.WriteLine("usage: Orchestrator.Worker (run|single-task) --connection <cs> --provider sqlite|postgres [options]");
        Environment.Exit(1);
        break;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>();
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i].StartsWith("--"))
        {
            result[args[i][2..]] = args[i + 1];
        }
    }
    return result;
}
