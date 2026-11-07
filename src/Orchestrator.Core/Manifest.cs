namespace Orchestrator.Core;

/// <summary>
/// One task in the DAG, before it is ever written to a database: a pure value, so the same
/// manifest always produces the same graph regardless of which store (Postgres, SQLite) it is
/// seeded into.
/// </summary>
public sealed record TaskNode(int Id, int PipelineId, int LocalId, int Layer, IReadOnlyList<int> DependsOn);

/// <summary>
/// The full DAG: <see cref="PipelineCount"/> independent pipelines of <see cref="TasksPerPipeline"/>
/// tasks each, arranged in <see cref="LayerCount"/> layers. A task in layer L &gt; 0 depends on 1
/// to 3 tasks in layer L - 1 of the same pipeline, chosen by a seeded RNG so the shape is fixed
/// before any run and reproducible from <see cref="Seed"/> alone; no two task IDs ever collide
/// across pipelines, and no dependency ever crosses a pipeline boundary.
/// </summary>
public sealed class Manifest
{
    public const int Seed = 20261102;
    public const int PipelineCount = 30;
    public const int LayerCount = 8;
    public const int TasksPerLayer = 10;
    public const int TasksPerPipeline = LayerCount * TasksPerLayer; // 80
    public const int TotalTaskCount = PipelineCount * TasksPerPipeline; // 2,400

    public IReadOnlyList<TaskNode> Tasks { get; }

    private Manifest(IReadOnlyList<TaskNode> tasks) => Tasks = tasks;

    /// <summary>Builds the pinned 30-pipeline, 2,400-task manifest every benchmark and the
    /// production worker use. The parameterized overload below exists only so tests can build
    /// a tiny manifest of the same shape in milliseconds; nothing in this repo's measured
    /// numbers calls it with non-default arguments.</summary>
    public static Manifest Build() => Build(PipelineCount, LayerCount, TasksPerLayer, Seed);

    public static Manifest Build(int pipelineCount, int layerCount, int tasksPerLayer, int seed)
    {
        var tasksPerPipeline = layerCount * tasksPerLayer;
        var tasks = new List<TaskNode>(pipelineCount * tasksPerPipeline);
        for (var pipelineId = 0; pipelineId < pipelineCount; pipelineId++)
        {
            // One RNG per pipeline, seeded from the global seed and the pipeline index alone, so
            // adding or removing a pipeline never reshuffles any other pipeline's dependency
            // choices.
            var rng = new Random(seed + pipelineId);
            for (var localId = 0; localId < tasksPerPipeline; localId++)
            {
                var layer = localId / tasksPerLayer;
                var globalId = pipelineId * tasksPerPipeline + localId;
                IReadOnlyList<int> dependsOn = Array.Empty<int>();
                if (layer > 0)
                {
                    var previousLayerStart = pipelineId * tasksPerPipeline + (layer - 1) * tasksPerLayer;
                    var depCount = rng.Next(1, Math.Min(4, tasksPerLayer + 1)); // 1 to 3 dependencies
                    var candidates = Enumerable.Range(previousLayerStart, tasksPerLayer).ToList();
                    var chosen = new List<int>(depCount);
                    for (var i = 0; i < depCount; i++)
                    {
                        var idx = rng.Next(candidates.Count);
                        chosen.Add(candidates[idx]);
                        candidates.RemoveAt(idx);
                    }
                    chosen.Sort();
                    dependsOn = chosen;
                }
                tasks.Add(new TaskNode(globalId, pipelineId, localId, layer, dependsOn));
            }
        }
        return new Manifest(tasks);
    }
}
