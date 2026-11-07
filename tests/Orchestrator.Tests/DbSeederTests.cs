using Microsoft.EntityFrameworkCore;
using Orchestrator.Core;

namespace Orchestrator.Tests;

public class DbSeederTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"seeder-test-{Guid.NewGuid():N}.db");
    private string ConnectionString => $"Data Source={_dbPath}";

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task SeedAsync_writes_one_task_record_per_manifest_task()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 3);
        using var db = DbContextFactory.CreateSqlite(ConnectionString);
        await DbSeeder.SeedAsync(db, manifest);
        Assert.Equal(manifest.Tasks.Count, await db.Tasks.CountAsync());
        Assert.All(await db.Tasks.ToListAsync(), t => Assert.Equal(OrchestrationStatus.Pending, t.Status));
    }

    [Fact]
    public async Task SeedAsync_writes_every_dependency_edge()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 3);
        using var db = DbContextFactory.CreateSqlite(ConnectionString);
        await DbSeeder.SeedAsync(db, manifest);
        var expectedEdges = manifest.Tasks.Sum(t => t.DependsOn.Count);
        Assert.Equal(expectedEdges, await db.Dependencies.CountAsync());
    }

    [Fact]
    public async Task SeedAsync_is_a_no_op_on_an_already_seeded_database()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 3);
        using var db = DbContextFactory.CreateSqlite(ConnectionString);
        await DbSeeder.SeedAsync(db, manifest);
        var firstCount = await db.Tasks.CountAsync();
        await DbSeeder.SeedAsync(db, manifest);
        Assert.Equal(firstCount, await db.Tasks.CountAsync());
    }

    [Fact]
    public void ManifestDigest_is_stable_for_the_same_manifest_and_differs_for_a_different_one()
    {
        var a = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 3);
        var b = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 3);
        var c = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 99);
        Assert.Equal(DbSeeder.ManifestDigest(a), DbSeeder.ManifestDigest(b));
        Assert.NotEqual(DbSeeder.ManifestDigest(a), DbSeeder.ManifestDigest(c));
    }
}
