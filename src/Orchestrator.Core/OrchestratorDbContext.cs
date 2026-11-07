using Microsoft.EntityFrameworkCore;

namespace Orchestrator.Core;

public sealed class OrchestratorDbContext : DbContext
{
    public DbSet<TaskRecord> Tasks => Set<TaskRecord>();
    public DbSet<TaskDependency> Dependencies => Set<TaskDependency>();
    public DbSet<SideEffectRecord> SideEffects => Set<SideEffectRecord>();
    public DbSet<PipelineRunRecord> Runs => Set<PipelineRunRecord>();

    public OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Task IDs are assigned by Manifest.Build(), never by the database; without this, EF's
        // default store-generated-key convention for an int PK collides explicit IDs against
        // each other during SaveChanges.
        modelBuilder.Entity<TaskRecord>().Property(t => t.Id).ValueGeneratedNever();

        modelBuilder.Entity<TaskDependency>().HasKey(d => new { d.TaskId, d.DependsOnTaskId });
        modelBuilder.Entity<TaskDependency>().HasIndex(d => d.DependsOnTaskId);

        modelBuilder.Entity<SideEffectRecord>().HasIndex(e => e.TaskId).IsUnique();

        modelBuilder.Entity<TaskRecord>().HasIndex(t => new { t.PipelineId, t.Status });
    }
}

/// <summary>
/// Connection-string construction for both supported providers. Production targets
/// PostgreSQL (DATABASE_URL or discrete env vars); every benchmark and test in this repository
/// runs against SQLite, since PostgreSQL is not installed as a running service on this machine
/// (see README Honest framing). Both code paths run through the identical
/// <see cref="OrchestratorDbContext"/> and <see cref="OrchestrationEngine"/>; only the
/// provider registration differs.
/// </summary>
public static class DbContextFactory
{
    public static OrchestratorDbContext CreatePostgres(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new OrchestratorDbContext(options);
    }

    public static OrchestratorDbContext CreateSqlite(string connectionString)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new OrchestratorDbContext(options);
    }
}
