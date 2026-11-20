using Bazaar.Contracts.Telemetry;

namespace Bazaar.Server.Diagnostics.Tests;

internal static class TestReports
{
    public static readonly Guid Install = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static CrashReport Report(
        Guid? reportId = null,
        string method = "Connect",
        string version = "0.1.0+a1b2c3d",
        string message = "connection refused",
        string? code = "BZ-NET-0001",
        Guid? attachmentId = null) => new(
        CrashReportSchemas.V1,
        reportId ?? Guid.NewGuid(),
        new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero),
        Install,
        new CrashClientInfo("windows", version, "beta", ".NET 11.0"),
        new CrashErrorInfo(code, "System.InvalidOperationException", message, null),
        [new CrashFrame("Bazaar.Client.Core", "abc123", "Bazaar.Client.Core.Session", method, 100, 4, 42, "Session.cs", true)],
        0,
        [new CrashModuleInfo("Bazaar.Client.Core", "0.1.0", "abc123")],
        [],
        new CrashDeviceInfo("windows", "11", "x64", "16-32 GiB", 1.0, "desktop", "normal", "en-US"),
        new CrashScreenInfo(attachmentId, new CrashMaskingInfo(0, 0)),
        new CrashRedactionInfo("1.0.0", new CrashRedactionCounts(0, 0, 0, 0), "pass"),
        null,
        new CrashConsentInfo("auto", "sent", "off"));
}
