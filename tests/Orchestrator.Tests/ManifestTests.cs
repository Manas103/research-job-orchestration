using Orchestrator.Core;

namespace Orchestrator.Tests;

public class ManifestTests
{
    [Fact]
    public void Build_produces_exactly_2400_tasks_across_30_pipelines()
    {
        var manifest = Manifest.Build();
        Assert.Equal(2400, manifest.Tasks.Count);
        Assert.Equal(30, manifest.Tasks.Select(t => t.PipelineId).Distinct().Count());
    }

    [Fact]
    public void Build_is_deterministic_for_the_fixed_seed()
    {
        var a = Manifest.Build();
        var b = Manifest.Build();
        Assert.Equal(DbSeeder.ManifestDigest(a), DbSeeder.ManifestDigest(b));
    }

    [Fact]
    public void No_dependency_crosses_a_pipeline_boundary()
    {
        var manifest = Manifest.Build();
        foreach (var task in manifest.Tasks)
        {
            foreach (var depId in task.DependsOn)
            {
                var dep = manifest.Tasks.First(t => t.Id == depId);
                Assert.Equal(task.PipelineId, dep.PipelineId);
            }
        }
    }

    [Fact]
    public void Every_dependency_is_in_the_immediately_preceding_layer()
    {
        var manifest = Manifest.Build();
        foreach (var task in manifest.Tasks)
        {
            foreach (var depId in task.DependsOn)
            {
                var dep = manifest.Tasks.First(t => t.Id == depId);
                Assert.Equal(task.Layer - 1, dep.Layer);
            }
        }
    }

    [Fact]
    public void Layer_zero_tasks_have_no_dependencies()
    {
        var manifest = Manifest.Build();
        Assert.All(manifest.Tasks.Where(t => t.Layer == 0), t => Assert.Empty(t.DependsOn));
    }

    [Fact]
    public void Every_non_layer_zero_task_has_1_to_3_dependencies()
    {
        var manifest = Manifest.Build();
        Assert.All(manifest.Tasks.Where(t => t.Layer > 0), t => Assert.InRange(t.DependsOn.Count, 1, 3));
    }

    [Fact]
    public void The_dag_has_no_cycles()
    {
        // Diamond dependencies (two paths converging on the same ancestor) are a legitimate DAG
        // shape, not a cycle, so this walks every dependency chain checking only that no node
        // reappears within its own current path (the ancestor chain from the root task down),
        // not that no node is ever visited twice across the whole walk.
        var manifest = Manifest.Build();
        var byId = manifest.Tasks.ToDictionary(t => t.Id);
        foreach (var task in manifest.Tasks)
        {
            AssertNoCycle(task.Id, byId, new HashSet<int> { task.Id });
        }
    }

    private static void AssertNoCycle(int taskId, Dictionary<int, TaskNode> byId, HashSet<int> pathSoFar)
    {
        foreach (var depId in byId[taskId].DependsOn)
        {
            Assert.True(pathSoFar.Add(depId), $"cycle: task {depId} reappears in its own ancestor chain starting from {taskId}");
            AssertNoCycle(depId, byId, pathSoFar);
            pathSoFar.Remove(depId);
        }
    }

    [Fact]
    public void Custom_shape_builds_the_requested_task_count()
    {
        var manifest = Manifest.Build(pipelineCount: 2, layerCount: 2, tasksPerLayer: 3, seed: 1);
        Assert.Equal(12, manifest.Tasks.Count);
    }
}
