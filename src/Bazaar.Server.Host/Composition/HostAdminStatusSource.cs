using System.Runtime.InteropServices;
using Bazaar.Contracts;
using Bazaar.Server.Host.Startup;
using Bazaar.Server.Providers;
using Bazaar.Server.WebAdmin;

namespace Bazaar.Server.Host.Composition;

/// <summary>
/// Host adapter for the web admin status section (doc 51 section 5). It reports facts the host already holds: the product version
/// (<c>0.MM.PP+commit</c>, docs/VERSIONING.md), uptime, the provider selection lines and the OS and runtime. No setting values or secrets.
/// </summary>
internal sealed class HostAdminStatusSource(
    HostState state,
    ProviderCapabilityReport providers,
    IEnumerable<IAdminQueueDepthSource>? queueSources = null,
    IEnumerable<IAdminHealthSource>? healthSources = null) : IAdminStatusSource
{
    public const string DisplayName = "Bazaar";

    public async Task<AdminStatusView> GetAsync(CancellationToken ct = default)
    {
        var lines = new List<string>(providers.Lines)
        {
            $"os: {RuntimeInformation.OSDescription}",
            $"runtime: {RuntimeInformation.FrameworkDescription}",
        };

        var queues = new List<AdminQueueDepth>();
        foreach (var source in queueSources ?? [])
        {
            queues.AddRange(await source.GetAsync(ct).ConfigureAwait(false));
        }

        var checks = new List<AdminHealthCheck>();
        foreach (var source in healthSources ?? [])
        {
            checks.AddRange(await source.GetAsync(ct).ConfigureAwait(false));
        }

        return new AdminStatusView(
            DisplayName,
            ProductVersion.Informational,
            ProductVersion.CurrentCodename is { } c && ProductVersion.Major(ProductVersion.Informational) >= 1 ? c : null,
            state.StartedAt,
            (long)state.Uptime.TotalSeconds,
            lines,
            queues,
            Summarize(checks));
    }

    /// <summary>No checks means <c>unknown</c>, never a made-up <c>healthy</c>. Any failing check fails the whole; otherwise any degraded check degrades it.</summary>
    internal static AdminHealthSummary Summarize(IReadOnlyList<AdminHealthCheck> checks)
    {
        if (checks.Count == 0)
        {
            return AdminHealthSummary.Unknown;
        }

        var state = checks.Any(c => c.State == "failing") ? "failing"
            : checks.Any(c => c.State is "degraded" or "unknown") ? "degraded"
            : "healthy";
        return new AdminHealthSummary(state, checks);
    }
}
