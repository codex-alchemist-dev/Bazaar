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
internal sealed class HostAdminStatusSource(HostState state, ProviderCapabilityReport providers) : IAdminStatusSource
{
    public const string DisplayName = "Bazaar";

    public Task<AdminStatusView> GetAsync(CancellationToken ct = default)
    {
        var lines = new List<string>(providers.Lines)
        {
            $"os: {RuntimeInformation.OSDescription}",
            $"runtime: {RuntimeInformation.FrameworkDescription}",
        };

        return Task.FromResult(new AdminStatusView(
            DisplayName,
            ProductVersion.Informational,
            ProductVersion.CurrentCodename is { } c && ProductVersion.Major(ProductVersion.Informational) >= 1 ? c : null,
            state.StartedAt,
            (long)state.Uptime.TotalSeconds,
            lines));
    }
}
