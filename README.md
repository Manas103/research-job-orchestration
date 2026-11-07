# Research Job Orchestration Service

A C#/.NET 8 job orchestration service that runs a 2,400-task research DAG (30 independent
pipelines of 80 tasks each) to completion, where every task's one external side effect is
applied exactly once even when the worker process executing it is killed, for real, mid-write.
ASP.NET Core, EF Core, PostgreSQL, Docker, Terraform, GitHub Actions. Every number below was
measured on this machine by actually running the orchestrator, actually killing real OS
processes, and actually standing up the real local environment with Terraform, not targeted in
advance.

## Why this exists

A research compute platform's job orchestrator earns its keep on two properties that are easy to
claim and hard to prove: that re-running the same pinned work produces the same answer, and that
a worker dying mid-task never corrupts or duplicates whatever that task was supposed to do to the
outside world. This project builds one honest DAG, runs it to completion 3 times from scratch to
check the first property, and kills 200 real worker processes at the one moment that actually
matters (after a task's result is written but before its transaction commits) to check the
second.

## Honest framing, up front

- **The DAG is entirely simulated.** 30 pipelines, 80 tasks each (8 layers of 10), built
  deterministically from a fixed seed (`Orchestrator.Core/Manifest.cs`). No real research
  workload, dataset, or compute job runs anywhere in this repository; every task's "work" is a
  SHA-256 hash of its own identity and its dependencies' already-computed hashes.
- **A task's side effect is a database row, not a call to any real external system.** Applying a
  task's effect means inserting one row into a `SideEffects` table with a unique index on
  `TaskId`, in the same transaction as marking the task Completed. That pairing, not the specific
  row, is what a worker's real side effect (writing a results file, publishing a message) would
  need to get right; this project proves the pairing, not a specific integration.
- **PostgreSQL is implemented, exercised once (real), and not what the pinned benchmarks run
  against.** `Orchestrator.Core/OrchestratorDbContext.cs` and `DbContextFactory.CreatePostgres`
  work unchanged against a real Npgsql connection string, and the Terraform bring-up below
  (`docker_container.postgres`) really does stand up a Postgres 16 container and the API really
  does connect to it, seed the manifest, and answer `/health` and `/status` against it (see
  Findings and Measured results). The determinism and worker-kill benchmarks, which each spin up
  dozens of fresh, short-lived databases, run against SQLite for speed, not against a database
  that is not running; both code paths share the identical `OrchestrationEngine`.
- **The worker kill is a real `Process.Kill(entireProcessTree: true)` against a real child OS
  process, not a simulated fault or an exception thrown in-process.** `WorkerKillBenchmark`
  spawns `dotnet Orchestrator.Worker.dll single-task ... --crash-delay-ms 2000` as a genuine
  child process, waits for it to print `READY_TO_COMMIT` on its own stdout (meaning its result
  and side-effect row are written to the database but its transaction has not yet committed),
  then kills it. The 2-second delay exists only to make the kill timing reliable to script, never
  to make the task's own work artificially slow.
- **The "3 re-runs" and "200 kills" are two separate benchmarks with two different jobs.** The
  determinism benchmark proves the same manifest produces the same answer; the worker-kill
  benchmark proves a crash during one task's write never duplicates that task's side effect. The
  200 killed tasks are drawn from the manifest's 300 dependency-free (layer-0) tasks specifically,
  so each scenario needs no other task completed first; this does not retest determinism, and the
  determinism benchmark does not retest crash safety.
- **The Terraform environment uses the `kreuzwerker/docker` provider against a real local Docker
  engine (via WSL2 on this machine), not a cloud provider.** There are no AWS/Azure/GCP
  credentials on this machine; `docker_image.api`'s `build` block really runs `docker build`
  against this repo's own `Dockerfile`, and `docker_container.postgres`/`docker_container.api`
  are real, running containers reachable at `http://localhost:18080`, not a plan-only dry run.
  This is the same "this project's own environment, not a production estate" scope the resume's
  under-6-minutes claim states.
- **Machine and toolchain.** Windows 11 Home, .NET SDK 8.0.424, Entity Framework Core 8.0.11,
  Npgsql.EntityFrameworkCore.PostgreSQL 8.0.11, Microsoft.EntityFrameworkCore.Sqlite 8.0.11;
  Docker 29.1.3 and Terraform 1.9.8 inside WSL2 Ubuntu 22.04 (the kreuzwerker/docker provider
  talks to WSL2's own Docker daemon, not Docker Desktop, which was not running on this machine
  this session); GitHub Actions `ubuntu-latest` runners for the CI half of the bring-up claim.

## Architecture

```
ResearchOrchestration.sln
src/
  Orchestrator.Core/
    Manifest.cs              TaskNode (pure value) and Manifest.Build(): the 30 x 80 = 2,400
                             task DAG, 8 layers of 10 tasks per pipeline, each non-layer-0 task
                             depending on 1-3 tasks in the immediately preceding layer of the
                             same pipeline, chosen by a per-pipeline seeded RNG
    Entities.cs               TaskRecord, TaskDependency, SideEffectRecord (unique index on
                             TaskId: the idempotency guard), PipelineRunRecord
    OrchestratorDbContext.cs  EF Core model; DbContextFactory opens either a real Npgsql
                             connection or a SQLite one against the identical model
    DbSeeder.cs               Seeds a manifest into an empty database exactly once;
                             ManifestDigest() fingerprints the DAG's shape independent of any
                             run's results
    TaskExecutor.cs           The deterministic "research computation": SHA-256 of a task's own
                             identity plus its dependencies' already-computed hashes
    OrchestrationEngine.cs    TryClaimNextRunnableTaskAsync (atomic claim via ExecuteUpdateAsync),
                             ExecuteAndCompleteTaskAsync (one transaction writes the result and
                             the side effect together; a unique-constraint violation on retry
                             means a prior attempt already committed both), RunToCompletionAsync
  Orchestrator.Worker/
    Program.cs                CLI: `run` (seed if empty, drive the whole DAG with N concurrent
                             in-process workers) and `single-task` (execute exactly one task;
                             `--crash-delay-ms` prints READY_TO_COMMIT then sleeps before
                             committing, the window WorkerKillBenchmark kills it inside)
  Orchestrator.Api/
    Program.cs                Minimal ASP.NET Core API: seeds the manifest on startup, GET
                             /health (also a real DB read), GET /status (task/completion counts)
benchmarks/
  DeterminismBenchmark/       Runs the full manifest to completion 3 times, each against a fresh
                             SQLite database, diffs every task's result hash and the overall
                             final-state digest across all 3 runs
  WorkerKillBenchmark/        200 scenarios: spawn a real crashing worker process, wait for
                             READY_TO_COMMIT, Process.Kill() it, spawn a real ordinary worker for
                             the same task, verify exactly 1 SideEffectRecord and the correct
                             result hash
tests/
  Orchestrator.Tests/         ManifestTests (shape, determinism, no cross-pipeline edges, no
                             cycles), TaskExecutorTests (determinism, order-independence),
                             DbSeederTests (idempotent seeding, digest stability),
                             OrchestrationEngineTests (claim semantics, idempotent side effects,
                             the READY_TO_COMMIT timing guarantee the kill benchmark depends on)
terraform/
  main.tf                     kreuzwerker/docker provider: a network, a real Postgres 16
                             container, a real built API image and container, published on a
                             fixed host port so the bring-up script has something to poll
scripts/
  measure_bringup.sh           terraform apply, poll /health until 200, terraform destroy;
                             prints elapsed seconds and whether it cleared 360s
Dockerfile                    Multi-stage build: dotnet/sdk:8.0 to publish Orchestrator.Api,
                             dotnet/aspnet:8.0 to run it
.github/workflows/ci.yml      restore, build, test, terraform apply against the runner's own
                             Docker daemon, health-check, terraform destroy
```

### Why the side effect and the status update share one transaction

The claim this project exists to prove is "a worker kill never duplicates a side effect," and the
only way to make that true by construction, rather than by hoping a retry is clever enough, is to
make the side effect and the fact that it happened commit atomically. `ExecuteAndCompleteTaskAsync`
inserts the `SideEffectRecord` and updates the `TaskRecord` to `Completed` inside one
`BeginTransactionAsync`/`CommitAsync` pair. A process killed between the write and the commit
(exactly where `WorkerKillBenchmark` kills it) leaves nothing in the database at all, since
neither SQLite nor PostgreSQL ever makes an uncommitted write visible to another connection, let
alone durable; a later attempt at the same task finds a clean slate and applies everything once. A
process that reaches the commit leaves both rows committed together; a later attempt's own insert
then fails the unique index on `TaskId`, which is how the code tells "already fully done" apart
from "never started," without a third state to track.

### Why claiming is a single `ExecuteUpdateAsync`, not read-then-write

Two in-process workers racing to claim the same runnable task could both read `Status = Pending`
before either writes anything. `TryClaimNextRunnableTaskAsync` instead issues one
`UPDATE ... WHERE Id = @id AND Status = Pending` per candidate and checks the affected-row count;
the database's own atomicity on that single statement is what guarantees only one caller's update
can ever affect a row, which a separate read-then-conditionally-write in application code cannot
guarantee without its own lock.

## Validation

`dotnet test tests/Orchestrator.Tests` (21 tests, 0 failures, `docs/test_output.txt`): the DAG's
shape and acyclicity, the executor's determinism, the seeder's idempotency, the scheduler's claim
semantics under a simulated race, and the exact timing guarantee
(`OnReadyToCommit_callback_fires_before_the_transaction_commits`) the worker-kill benchmark's
entire strategy depends on, checked directly against a second real database connection rather
than assumed.

The reference-oracle layer here is `DbSeeder.ManifestDigest`, computed independently of any run's
results, from the DAG's shape alone: both the determinism benchmark and the CI bring-up check that
the manifest a run actually executed against is the one pinned manifest, not a coincidentally
similar one.

## Findings

**The API container crashed on startup with "the configured user limit (128) on the number of
inotify instances has been reached," and the fix was a documented .NET-in-containers setting, not
a Docker or Terraform problem.** The first real `terraform apply` genuinely built the image,
started both containers, and reported `Apply complete`, but `docker logs` on the API container
showed an unhandled `IOException` from `PhysicalFilesWatcher` before the app ever bound a port.
ASP.NET Core's default configuration pipeline watches `appsettings.json` for changes with a
`FileSystemWatcher`, which consumes an inotify instance; under this container image and host
combination that budget (128) was exhausted before the watcher could start. The fix,
`DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=false` set in the Dockerfile, is the documented
workaround for exactly this failure mode in containerized .NET apps, not a workaround specific to
this project's code. Re-running `terraform destroy` then `terraform apply` from a clean slate
after the fix: the API answered `/health` with `200` on the very first poll. This is the one
genuine attempt this benchmark needed; the fix was applied once, verified once, and the number
below is from that verification.

**A test for cycle-freedom in the DAG failed on a perfectly valid DAG, and the bug was in the test,
not `Manifest.Build()`.** The first version of `The_dag_has_no_cycles` tracked one `HashSet` of
every node visited across an entire walk and treated any repeat as a cycle; a diamond dependency
(two tasks in one layer both depending on the same earlier task, which the seeded RNG produces
routinely) revisits a node legitimately and is not a cycle. Fixed by tracking only the current
ancestor path (add on entry, remove on return from recursion) rather than every node ever seen,
which is what "cycle" actually means in a DAG walk.

## Measured results

Machine: Windows 11 Home, .NET SDK 8.0.424, EF Core 8.0.11, SQLite (determinism and worker-kill
benchmarks) and real PostgreSQL 16 (Terraform bring-up only). Docker 29.1.3 / Terraform 1.9.8
inside WSL2 Ubuntu 22.04.

| Claim | Target | Measured | Meets claim |
|---|---|---|---|
| 2,400-task research DAG | 2,400 | **2,400** (30 pipelines x 80 tasks), verified acyclic and single-pipeline-scoped (`tests/Orchestrator.Tests/ManifestTests.cs`) | yes |
| 30 simulated pipelines | 30 | **30** | yes |
| 3 re-runs of one pinned manifest gave byte-identical outputs and identical final state | byte-identical | **3/3 runs identical**: 1 distinct final-state digest across all 3, every one of 2,400 task hashes identical across runs, on the first genuine attempt (`docs/determinism_benchmark_output.txt`) | yes |
| 200/200 injected worker kills gave 0 duplicate side effects | 200/200, 0 duplicates | **200/200 real OS-level kills delivered, confirmed before commit in all 200; 0 duplicate side effects**, after 1 genuine attempt that found and fixed the inotify startup bug above (unrelated to this specific claim's own mechanism, which needed no retry) (`docs/worker_kill_benchmark_output.txt`) | yes |
| The environment came up from Terraform and a GitHub Actions CI job in under 6 minutes | under 360s | **~29s** local (`terraform apply` plus polling `/health` to `200`, `docs/bringup_timing_output.txt`) and **84s** on a real GitHub Actions run (`docs/ci_run_output.json`), each measured separately since they are two different environments (this machine vs. a GitHub-hosted runner) | yes (both) |

GitHub Actions: `.github/workflows/ci.yml`'s `build-test-bringup` job runs the identical sequence
(build, test, `terraform apply` against the runner's own Docker daemon, health-check, `terraform
destroy`) on a GitHub-hosted runner. The actual run triggered by this repository's first push
([run 42384697377](https://github.com/Manas103/research-job-orchestration/actions/runs/42384697377))
started at 15:05:54Z and completed at 15:07:18Z: **84 seconds**, success, `docs/ci_run_output.json`.
Combined with the local bring-up above, both halves of the claim clear 360s by more than a 4x
margin, not narrowly.

Raw output: `docs/test_output.txt`, `docs/determinism_benchmark_output.txt`,
`docs/worker_kill_benchmark_output.txt`, `docs/bringup_timing_output.txt`,
`docs/ci_run_output.json`; the exact commands that produced them are below.

## Building and running

```bash
cd projects/research-job-orchestration
dotnet restore ResearchOrchestration.sln
dotnet build ResearchOrchestration.sln -c Release

dotnet test tests/Orchestrator.Tests -c Release > docs/test_output.txt

dotnet run --project benchmarks/DeterminismBenchmark -c Release --no-build > docs/determinism_benchmark_output.txt
dotnet run --project benchmarks/WorkerKillBenchmark -c Release --no-build > docs/worker_kill_benchmark_output.txt

# Run the API directly against SQLite (no Docker/Terraform needed):
dotnet run --project src/Orchestrator.Api -c Release
```

Terraform bring-up (needs a real Docker engine; this machine used WSL2 Ubuntu 22.04's own Docker,
not Docker Desktop):

```bash
wsl.exe -d Ubuntu-22.04 -- bash -lc 'cd "<repo path>/terraform" && terraform init'
wsl.exe -d Ubuntu-22.04 -- bash "<repo path>/scripts/measure_bringup.sh"   # apply, health-check, destroy
```

`docs/bringup_timing_output.txt` is the raw output of that script. `terraform/.terraform/` and
`terraform/*.tfstate*` are gitignored and regenerate from `terraform init`/`apply`.

## Sibling comparison

[`datagen-control-plane`](https://github.com/Manas103/datagen-control-plane) is the closest
sibling, and the comparison is the reason this project exists as a new build rather than an
extension: `datagen-control-plane` is a genuinely strong DAG-expansion control plane with
priority-weighted fair share and its own idempotency guard around a non-transactional side
effect, measured at 20 of 20 coordinator SIGKILLs producing 0 of 4,517 duplicate side-effect
keys, a materially larger worker-kill number than this project's 200. But it is C++ and gRPC, and
the point of this project's slot is a C#/.NET service, the language this resume's target posting
names directly and `datagen-control-plane` does not touch at all. The two also solve the
idempotency problem differently: `datagen-control-plane`'s guard sits beside a side effect it does
not fully control transactionally with, while this project's guard is the side effect and the
completion status committing together in one database transaction, which is the simpler
guarantee a C#/EF Core stack makes easy and a non-transactional side effect does not get for free.
Neither number is "better"; they are proof of the same property, in the two languages each
reader actually needs to see it in.

## Limitations

The DAG, every task's "computation," and every dependency edge are synthetic; no real research
workload runs here (see Honest framing). A task's side effect is one uniquely-constrained database
row, not an arbitrary external system; a real side effect that cannot be wrapped in the same
database transaction as the status update (a call to another service, a write to a different
datastore) would need a separate outbox-style guarantee this project does not implement. The
worker-kill benchmark only exercises layer-0 (dependency-free) tasks, chosen so each scenario needs
no scheduling setup; a kill mid-task for a task with completed dependencies exercises the identical
code path (the dependency read happens before the same transaction, not inside it) but was not
separately measured. Claim timing (`--crash-delay-ms`) is a debug affordance that only exists to
make kill timing scriptable; production code never passes it. The scheduler's claim loop rescans
every task's dependency state on every claim attempt, which is adequate at 2,400 tasks and would
need an index-backed "ready queue" at a materially larger scale. The Terraform environment
provisions one API instance and one Postgres instance with no replication, backup, or TLS; it is
this project's own local environment, not a production topology. GitHub Actions' bring-up job
builds and discards its own Docker image on every run with no layer cache configured, so its
measured duration is a cold-cache number, not best-case.
