using Bazaar.Kodo.Probes.Self;

namespace Bazaar.Kodo.Tests.Probes.Self;

/// <summary>A scriptable <see cref="IKodoSelfReadings"/>. Each read can be replaced by a test with a value or a throw.</summary>
internal sealed class FakeKodoSelfReadings : IKodoSelfReadings
{
    public Func<CancellationToken, Task<bool>> JournalWritable { get; set; } = _ => Task.FromResult(true);

    public Func<CancellationToken, Task<SpillFileReading?>> Spill { get; set; } = _ => Task.FromResult<SpillFileReading?>(null);

    public Task<bool> CanWriteJournalAsync(CancellationToken cancellationToken) => JournalWritable(cancellationToken);

    public Task<SpillFileReading?> ReadSpillFileAsync(CancellationToken cancellationToken) => Spill(cancellationToken);
}
