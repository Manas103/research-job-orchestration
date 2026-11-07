using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Orchestrator.Core;

public static class DbSeeder
{
    /// <summary>A stable fingerprint of the manifest's shape, independent of any run's results,
    /// used to confirm every benchmark run actually executed the same pinned graph.</summary>
    public static string ManifestDigest(Manifest manifest)
    {
        var sb = new StringBuilder();
        foreach (var task in manifest.Tasks.OrderBy(t => t.Id))
        {
            sb.Append(task.Id).Append(':').Append(string.Join(",", task.DependsOn)).Append(';');
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash);
    }

    public static async Task SeedAsync(OrchestratorDbContext db, Manifest manifest)
    {
        await db.Database.EnsureCreatedAsync();
        if (await db.Tasks.AnyAsync())
        {
            return; // already seeded; a fresh database is expected per run in this repo's benchmarks
        }

        foreach (var task in manifest.Tasks)
        {
            db.Tasks.Add(new TaskRecord
            {
                Id = task.Id,
                PipelineId = task.PipelineId,
                LocalId = task.LocalId,
                Layer = task.Layer,
                Status = OrchestrationStatus.Pending,
            });
            foreach (var dep in task.DependsOn)
            {
                db.Dependencies.Add(new TaskDependency { TaskId = task.Id, DependsOnTaskId = dep });
            }
        }
        await db.SaveChangesAsync();
    }
}
