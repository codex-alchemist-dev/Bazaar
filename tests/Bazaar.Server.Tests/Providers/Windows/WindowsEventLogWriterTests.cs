using System.Runtime.Versioning;
using Bazaar.Server.Providers.Windows;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Tests.Providers.Windows;

[SupportedOSPlatform("windows")]
public class WindowsEventLogWriterTests
{
    [Fact]
    public void Probe_is_unusable_with_a_reason_when_the_source_cannot_be_used()
    {
        // A source name no installer creates, so the answer is the same on every machine.
        var writer = new WindowsEventLogWriter("Bazaar.Server.Test.Unregistered." + Guid.NewGuid().ToString("N"));

        var status = writer.Probe();

        Assert.Equal("windows_event_log", status.Kind);
        Assert.False(status.Usable);
        Assert.NotNull(status.Reason);
        Assert.Contains(OperatingSystem.IsWindows() ? "not registered" : "requires Windows", status.Reason);
    }

    [Fact]
    public void Write_returns_false_rather_than_throwing_when_unusable()
    {
        var writer = new WindowsEventLogWriter("Bazaar.Server.Test.Unregistered." + Guid.NewGuid().ToString("N"));

        var written = writer.Write(LogLevel.Warning, "test warning");

        Assert.False(written);
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    public void Write_drops_levels_below_information_on_every_os(LogLevel level)
    {
        var writer = new WindowsEventLogWriter();

        Assert.False(writer.Write(level, "not an event log level"));
    }

    [Fact]
    public void Probe_on_the_default_source_reports_the_registration_state()
    {
        var status = new WindowsEventLogWriter().Probe();

        if (OperatingSystem.IsWindows())
        {
            // Usable only when the installer has registered "Bazaar.Server"; the probe must agree with the registry.
            Assert.Equal(System.Diagnostics.EventLog.SourceExists(WindowsEventLogWriter.DefaultSource), status.Usable);
        }
        else
        {
            Assert.False(status.Usable);
            Assert.Equal("requires Windows", status.Reason);
        }
    }

    [Fact]
    public void Constructor_rejects_a_blank_source()
    {
        Assert.ThrowsAny<ArgumentException>(() => new WindowsEventLogWriter(" "));
    }
}
