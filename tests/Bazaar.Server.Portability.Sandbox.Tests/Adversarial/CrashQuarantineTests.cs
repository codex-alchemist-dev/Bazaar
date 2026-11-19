using System.Text;

namespace Bazaar.Server.Portability.Sandbox.Tests.Adversarial;

public class CrashQuarantineTests
{
    [Fact]
    public void A_file_that_keeps_crashing_the_parser_is_quarantined_by_content_not_by_name()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "uses /bin/sh");
        var runner = HostileChild.Runner(new HostileChild("exit 9"), o => o with { CrashLoopThreshold = 3 });
        var content = Encoding.UTF8.GetBytes("the poisoned file");

        for (var i = 0; i < 3; i++)
        {
            // Every attempt uses a different file name and a different importer id: neither resets the count.
            var attempt = i;
            Assert.Equal(ParserFailureKind.Crash, Assert.Throws<ParserSandboxException>(() => runner.Parse("imp" + attempt, new ImportSource("name" + attempt + ".docx", content))).Kind);
        }

        var refused = Assert.Throws<ParserSandboxException>(() => runner.Parse("imp-new", new ImportSource("renamed.txt", content)));
        Assert.Equal(ParserFailureKind.CrashLoop, refused.Kind);
        Assert.True(runner.InputTracker.TrackedImporterCount >= 1);
    }

    [Fact]
    public void Other_files_are_not_quarantined_by_one_poisoned_file()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "uses /bin/sh");
        var runner = HostileChild.Runner(new HostileChild("exit 9"), o => o with { CrashLoopThreshold = 3 });

        for (var i = 0; i < 2; i++)
        {
            Assert.Throws<ParserSandboxException>(() => runner.Parse("p", new ImportSource("a", Encoding.UTF8.GetBytes("poisoned"))));
        }

        var other = Assert.Throws<ParserSandboxException>(() => runner.Parse("q", new ImportSource("b", Encoding.UTF8.GetBytes("different"))));
        Assert.Equal(ParserFailureKind.Crash, other.Kind);
    }

    [Fact]
    public void The_tracker_memory_is_bounded_under_many_distinct_keys()
    {
        var tracker = new ParserCrashTracker(2, TimeSpan.FromHours(1), maxTrackedKeys: 100);

        for (var i = 0; i < 10_000; i++)
        {
            tracker.RecordFailure("sha256:" + i, "crash");
            tracker.RecordFailure("sha256:" + i, "crash");
        }

        Assert.True(tracker.TrackedImporterCount <= 100, tracker.TrackedImporterCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
