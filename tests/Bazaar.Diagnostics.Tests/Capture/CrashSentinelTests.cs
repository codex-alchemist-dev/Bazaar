using Bazaar.Diagnostics.Capture;

namespace Bazaar.Diagnostics.Tests.Capture;

public class CrashSentinelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-sentinel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string SentinelPath => Path.Combine(_root, "state", "crash.sentinel");

    [Fact]
    public void First_begin_reports_no_earlier_run_and_writes_the_file()
    {
        var sentinel = new CrashSentinel(SentinelPath);

        var previousUnclean = sentinel.Begin();

        Assert.False(previousUnclean);
        Assert.True(File.Exists(SentinelPath));
    }

    [Fact]
    public void Begin_creates_a_missing_parent_directory()
    {
        Assert.False(Directory.Exists(Path.GetDirectoryName(SentinelPath)));

        new CrashSentinel(SentinelPath).Begin();

        Assert.True(File.Exists(SentinelPath));
    }

    [Fact]
    public void Clean_end_removes_the_file_so_the_next_start_is_clean()
    {
        var first = new CrashSentinel(SentinelPath);
        first.Begin();
        first.End();

        var next = new CrashSentinel(SentinelPath);

        Assert.False(File.Exists(SentinelPath));
        Assert.False(next.Begin());
    }

    [Fact]
    public void Begin_without_end_reports_an_unclean_earlier_run()
    {
        new CrashSentinel(SentinelPath).Begin();

        var next = new CrashSentinel(SentinelPath);

        Assert.True(next.Begin());
        Assert.True(File.Exists(SentinelPath));
    }

    [Fact]
    public void End_when_the_file_is_already_gone_does_nothing()
    {
        var sentinel = new CrashSentinel(SentinelPath);

        sentinel.End();
        sentinel.End();

        Assert.False(File.Exists(SentinelPath));
    }

    [Fact]
    public void Blank_path_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new CrashSentinel("  "));
    }
}
