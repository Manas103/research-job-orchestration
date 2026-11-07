using Orchestrator.Core;

namespace Orchestrator.Tests;

public class TaskExecutorTests
{
    [Fact]
    public void Compute_is_deterministic_for_the_same_node_and_dependency_hashes()
    {
        var node = new TaskNode(5, 0, 5, 1, new[] { 1, 2 });
        var deps = new Dictionary<int, string> { [1] = "AAAA", [2] = "BBBB" };
        Assert.Equal(TaskExecutor.Compute(node, deps), TaskExecutor.Compute(node, deps));
    }

    [Fact]
    public void Compute_is_order_independent_in_the_dependency_dictionary()
    {
        var node = new TaskNode(5, 0, 5, 1, new[] { 1, 2 });
        var depsA = new Dictionary<int, string> { [1] = "AAAA", [2] = "BBBB" };
        var depsB = new Dictionary<int, string> { [2] = "BBBB", [1] = "AAAA" };
        Assert.Equal(TaskExecutor.Compute(node, depsA), TaskExecutor.Compute(node, depsB));
    }

    [Fact]
    public void Compute_differs_when_a_dependency_hash_differs()
    {
        var node = new TaskNode(5, 0, 5, 1, new[] { 1 });
        var depsA = new Dictionary<int, string> { [1] = "AAAA" };
        var depsB = new Dictionary<int, string> { [1] = "ZZZZ" };
        Assert.NotEqual(TaskExecutor.Compute(node, depsA), TaskExecutor.Compute(node, depsB));
    }

    [Fact]
    public void Compute_differs_between_two_tasks_with_no_dependencies_but_different_identity()
    {
        var a = new TaskNode(0, 0, 0, 0, Array.Empty<int>());
        var b = new TaskNode(1, 0, 1, 0, Array.Empty<int>());
        var empty = new Dictionary<int, string>();
        Assert.NotEqual(TaskExecutor.Compute(a, empty), TaskExecutor.Compute(b, empty));
    }
}
