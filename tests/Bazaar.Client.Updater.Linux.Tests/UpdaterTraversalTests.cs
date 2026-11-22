using static Bazaar.Client.Updater.Linux.Tests.TarBuilder;

namespace Bazaar.Client.Updater.Linux.Tests;

/// <summary>A hostile tarball that is correctly hashed and signed must still never install.</summary>
public class UpdaterTraversalTests
{
    public static TheoryData<string, byte[]> Hostile() => new()
    {
        { "dot-dot name", Build(File("../escape")) },
        { "absolute name", Build(File("/tmp/escape")) },
        { "link outside", Build(Link("l", "../../etc")) },
        { "write through link", Build(Link("l", "."), File("l/escape")) },
        { "hard link", Build(File("a"), HardLink("b", "a")) },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task A_signed_but_hostile_tarball_is_rejected_and_leaves_no_trace(string _, byte[] tarball)
    {
        var rig = new Rig();
        rig.Publish("0.2.0");
        await rig.Updater.ApplyAsync(CancellationToken.None);
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        rig.Publish("0.3.0", tarball: tarball);

        var result = await rig.Updater.ApplyAsync(CancellationToken.None);

        Assert.Equal(UpdateStatus.ExtractionRejected, result.Status);
        Assert.Equal("releases/0.2.0", rig.Current);
        Assert.Null(rig.Previous);
        Assert.DoesNotContain(rig.Fs.Paths, p => p.Contains("0.3.0", StringComparison.Ordinal) || p.Contains(".partial", StringComparison.Ordinal) || p.Contains("staging", StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Fs.Paths, p => p.Contains("escape", StringComparison.Ordinal));
    }
}
