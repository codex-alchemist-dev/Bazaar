using Bazaar.Kodo.Probes;
using Bazaar.Kodo.Probes.Host;
using Bazaar.Kodo.Probes.Self;
using Bazaar.Kodo.Tests.Probes.Host;

namespace Bazaar.Kodo.Tests.Probes;

public sealed class BuiltInProbeCatalogueTests
{
    [Fact]
    public void JournalWritableIsCriticalAt30SecondsInTheLocalPool()
    {
        ProbeCatalogueEntry entry = Assert.Single(
            BuiltInProbeCatalogue.Entries,
            e => e.Id == JournalWritableProbe.Id);

        Assert.True(entry.Critical);
        Assert.Equal(TimeSpan.FromSeconds(30), entry.Cadence);
        Assert.Equal("local", entry.Pool);
    }

    [Fact]
    public void EveryBuiltInProbeHasACatalogueRow()
    {
        string[] ids = BuiltInProbeCatalogue.Entries.Select(e => e.Id).ToArray();

        Assert.Contains(ids, id => id == "host.disk.free.");
        Assert.Contains(MemoryPressureProbe.Id, ids);
        Assert.Contains(ClockDriftProbe.Id, ids);
        Assert.Contains(JournalWritableProbe.Id, ids);
        Assert.Contains(SpillAgeProbe.Id, ids);
        Assert.Equal(ids.Distinct().Count(), ids.Length);
    }

    [Fact]
    public void DiskRowMatchesTheVolumePrefixOfItsProbeId()
    {
        var probe = new DiskFreeProbe(new FakeHostReadings(), "data");

        Assert.StartsWith("host.disk.free.", probe.Id);
        Assert.Contains(BuiltInProbeCatalogue.Entries, e => e.Id == "host.disk.free.");
    }
}
