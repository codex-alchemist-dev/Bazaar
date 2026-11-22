using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench;

/// <summary>An in-memory task source. Tests and hosts that already hold the tasks use it; the content is not validated here.</summary>
public sealed class StaticCodingBenchTaskSource : ICodingBenchTaskSource
{
    private readonly IReadOnlyList<CodingTask> _tasks;

    public StaticCodingBenchTaskSource(IEnumerable<CodingTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        _tasks = tasks.ToList();
    }

    public Task<IReadOnlyList<CodingTask>> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_tasks);
    }
}
