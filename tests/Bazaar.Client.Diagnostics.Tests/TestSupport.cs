using Bazaar.Contracts.Telemetry;
using Bazaar.Diagnostics.Capture;
using Bazaar.Diagnostics.Consent;
using Bazaar.Diagnostics.Redaction;

namespace Bazaar.Client.Diagnostics.Tests;

/// <summary>A clock the test moves by hand. Only the instant is faked; the rest of <see cref="TimeProvider"/> is the base behaviour.</summary>
internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now;

    public TestClock(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A temporary directory, removed when the test ends.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bazaar-client-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}

internal sealed class FixedClientInfoProvider : IClientInfoProvider
{
    public ClientInfo Current() => new(
        "0.1.0+a1b2c3d",
        new DeviceInfo("Linux 6.18", ".NET 11.0.0", "x64"),
        [new ModuleInfo("Bazaar.Client.Diagnostics", "0.1.0"), new ModuleInfo("Bazaar.Diagnostics", "0.1.0")]);
}

/// <summary>Builds reports directly, without a capture, for queue and uploader tests.</summary>
internal static class Samples
{
    public static readonly Guid InstallId = new("0196f3a2-0000-7000-8000-000000000001");

    public static CrashReport Report(Guid reportId, DateTimeOffset createdAt, string residualScan = "pass", string schema = CrashReportSchemas.V1) => new(
        schema,
        reportId,
        createdAt,
        InstallId,
        new CrashClientInfo("linux", "0.1.0+a1b2c3d", "Beta", ".NET 11.0.0"),
        new CrashErrorInfo("BZ-GC-0042", "Bazaar.Client.Core.GcViewException", "Failed to render", null),
        [new CrashFrame("Bazaar.Client.Core", "<unknown>", "Bazaar.Client.Core.Panes.GcView", "RenderPage", 0, 0, null, null, true)],
        0,
        [new CrashModuleInfo("Bazaar.Client.Core", "0.1.0", "<unknown>")],
        [],
        new CrashDeviceInfo("linux", "Linux 6.18", "x64", "16-32 GiB", 1.0, "desktop", "open", "en-GB"),
        new CrashScreenInfo(null, new CrashMaskingInfo(0, 0)),
        new CrashRedactionInfo(RedactionRules.Version, new CrashRedactionCounts(0, 0, 0, 0), residualScan),
        "fp1:00000000000000000000000000000000",
        new CrashConsentInfo("ask", "queued", "off"));

    public static CrashReportContext Context() => new(
        InstallId,
        "linux",
        "Beta",
        new CrashDeviceProfile("linux", "16-32 GiB", 1.0, "desktop", "open", "en-GB"));

    public static ConsentSettings Mode(ConsentMode mode) => new() { Mode = mode };
}
