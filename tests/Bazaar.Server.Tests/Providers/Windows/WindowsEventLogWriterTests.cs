using System.Runtime.Versioning;
using Bazaar.Server.Providers.Windows;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Providers.Windows;

[SupportedOSPlatform("windows")]
public class WindowsEventLogWriterTests
{
    [Fact]
    public void Probe_is_unusable_with_a_reason_when_the_source_is_not_registered()
    {
        var writer = new WindowsEventLogWriter("Bazaar.Server.Test", sourceExists: _ => false);

        var status = writer.Probe();

        Assert.Equal("windows_event_log", status.Kind);
        Assert.False(status.Usable);
        Assert.NotNull(status.Reason);
        Assert.Contains(OperatingSystem.IsWindows() ? "is not registered" : "requires Windows", status.Reason);
    }

    [Fact]
    public void Probe_reports_cannot_check_instead_of_throwing_when_the_registry_is_inaccessible()
    {
        var writer = new WindowsEventLogWriter(
            "Bazaar.Server.Test",
            sourceExists: _ => throw new System.Security.SecurityException("Inaccessible logs: Security"));

        var status = writer.Probe();

        Assert.False(status.Usable);
        Assert.NotNull(status.Reason);
        Assert.Contains(OperatingSystem.IsWindows() ? "could not be checked" : "requires Windows", status.Reason);
    }

    [Fact]
    public void Probe_is_usable_when_the_source_is_registered()
    {
        var status = new WindowsEventLogWriter("Bazaar.Server.Test", sourceExists: _ => true).Probe();

        if (OperatingSystem.IsWindows())
        {
            Assert.True(status.Usable);
            Assert.Null(status.Reason);
        }
        else
        {
            Assert.False(status.Usable);
            Assert.Equal("requires Windows", status.Reason);
        }
    }

    [Fact]
    public void Write_returns_false_rather_than_throwing_when_unusable()
    {
        var writer = new WindowsEventLogWriter("Bazaar.Server.Test", sourceExists: _ => false);

        Assert.False(writer.Write(LogLevel.Warning, "test warning"));
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    public void Write_drops_levels_below_information_on_every_os(LogLevel level)
    {
        var writer = new WindowsEventLogWriter("Bazaar.Server.Test", sourceExists: _ => true);

        Assert.False(writer.Write(level, "not an event log level"));
    }

    [Fact]
    public void Probe_on_the_default_source_never_throws()
    {
        // The real registry check. A non-elevated run must still return a status, not an exception.
        var status = new WindowsEventLogWriter().Probe();

        Assert.Equal("windows_event_log", status.Kind);
        Assert.Equal(status.Usable, status.Reason is null);
    }

    [Fact]
    public void Constructor_rejects_a_blank_source()
    {
        Assert.ThrowsAny<ArgumentException>(() => new WindowsEventLogWriter(" "));
    }
}
