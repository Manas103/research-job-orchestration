using Microsoft.EntityFrameworkCore;
using Orchestrator.Core;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? builder.Configuration["ConnectionStrings:Default"]
    ?? "Data Source=orchestrator.db";
var useNpgsql = connectionString.StartsWith("Host=") || connectionString.StartsWith("postgres");

builder.Services.AddDbContext<OrchestratorDbContext>(opts =>
{
    if (useNpgsql) opts.UseNpgsql(connectionString); else opts.UseSqlite(connectionString);
});

var app = builder.Build();

var manifest = Manifest.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
    await DbSeeder.SeedAsync(db, manifest);
}

// /health is what docker compose / Terraform's health-check wiring (see docs/bringup_timing.md)
// polls to decide the environment is actually up, not just that the container started.
app.MapGet("/health", async (OrchestratorDbContext db) =>
{
    var canReachDb = await db.Tasks.AnyAsync();
    return canReachDb ? Results.Ok(new { status = "ok" }) : Results.StatusCode(503);
});

app.MapGet("/status", async (OrchestratorDbContext db) =>
{
    var total = await db.Tasks.CountAsync();
    var completed = await db.Tasks.CountAsync(t => t.Status == OrchestrationStatus.Completed);
    var pipelineCount = await db.Tasks.Select(t => t.PipelineId).Distinct().CountAsync();
    return Results.Ok(new { total, completed, pipelineCount, manifestDigest = DbSeeder.ManifestDigest(manifest) });
});

app.Run();
