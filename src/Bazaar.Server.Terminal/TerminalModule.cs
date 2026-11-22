using Bazaar.Core;
using Bazaar.Server.Terminal.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Terminal;

/// <summary>
/// The terminal module's server half (doc 52, section 4) as a Bazaar Component. No hard dependencies. The soft dependencies are
/// <c>vms</c> (it supplies <see cref="IGuestPtyChannel"/>) and <c>audit</c> (it supplies <see cref="ITerminalAudit"/>). Without
/// them the broker fails closed: no backend means no terminals, and no audit sink means no human control of a terminal.
/// </summary>
public sealed class TerminalModule : IBazaarComponent
{
    /// <summary>The component manifest.</summary>
    public static ComponentManifest ManifestDefinition { get; } = new(
        Id: "terminal",
        Version: "0.1.0",
        Kind: ComponentKind.Feature,
        DependsOn: [],
        Provides: ["feature:terminal"],
        SoftDependsOn: ["vms", "audit"]);

    /// <inheritdoc />
    public ComponentManifest Manifest => ManifestDefinition;

    /// <inheritdoc />
    public void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddBazaarTerminal(TerminalServiceCollectionExtensions.ReadOptions(configuration));
    }
}

/// <summary>Service registration for the terminal module.</summary>
public static class TerminalServiceCollectionExtensions
{
    /// <summary>
    /// Registers the broker, the default access policy and the options. Every collaborator uses <c>TryAdd</c>, so the VM module's
    /// channel, an audit module's sink or a grant source registered earlier wins. The fail-closed defaults are a channel with no
    /// backend, an audit sink that always fails, and a grant source that grants nothing.
    /// </summary>
    public static IServiceCollection AddBazaarTerminal(this IServiceCollection services, TerminalOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var effective = (options ?? new TerminalOptions()).Normalize();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(effective);
        services.TryAddSingleton<IGuestPtyChannel, NoBackendGuestPtyChannel>();
        services.TryAddSingleton<ITerminalAudit, UnavailableTerminalAudit>();
        services.TryAddSingleton<ITerminalGrantSource, NoTerminalGrants>();
        services.TryAddSingleton<ITerminalAccessPolicy>(sp => new DefaultTerminalAccessPolicy(sp.GetRequiredService<ITerminalGrantSource>()));
        services.TryAddSingleton<ITerminalBroker>(sp => new TerminalBroker(
            sp.GetRequiredService<IGuestPtyChannel>(),
            sp.GetRequiredService<ITerminalAccessPolicy>(),
            sp.GetRequiredService<ITerminalAudit>(),
            sp.GetRequiredService<TerminalOptions>(),
            sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Reads operator overrides from the <c>Terminal</c> configuration section. Unset or unparsable keys keep the safe default.</summary>
    internal static TerminalOptions ReadOptions(IConfiguration configuration)
    {
        var defaults = new TerminalOptions();
        return defaults with
        {
            ScrollbackBytes = Int(configuration, "ScrollbackBytes", defaults.ScrollbackBytes),
            SubscriberQueueFrames = Int(configuration, "SubscriberQueueFrames", defaults.SubscriberQueueFrames),
            MaxSessionsPerAgent = Int(configuration, "MaxSessionsPerAgent", defaults.MaxSessionsPerAgent),
            MaxSessionsPerCompany = Int(configuration, "MaxSessionsPerCompany", defaults.MaxSessionsPerCompany),
            MaxFramePayloadBytes = Int(configuration, "MaxFramePayloadBytes", defaults.MaxFramePayloadBytes),
            InputBytesPerSecond = Int(configuration, "InputBytesPerSecond", defaults.InputBytesPerSecond),
            IdleClose = TimeSpan.FromMinutes(Int(configuration, "IdleCloseMinutes", (int)defaults.IdleClose.TotalMinutes)),
            AuditEveryHumanInput = string.Equals(configuration["Terminal:AuditEveryHumanInput"], "true", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static int Int(IConfiguration configuration, string key, int fallback) =>
        int.TryParse(configuration["Terminal:" + key], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}

/// <summary>The guest channel used until the VM module registers one: unavailable, so the broker refuses every open.</summary>
internal sealed class NoBackendGuestPtyChannel : IGuestPtyChannel
{
    public bool IsAvailable => false;

    public Task<IGuestPty> OpenAsync(string vmId, string terminalId, int cols, int rows, CancellationToken ct) =>
        throw new InvalidOperationException("No VM backend is registered for terminals.");
}

/// <summary>The audit sink used until an audit module registers one: it always fails, so human control and human input are refused.</summary>
internal sealed class UnavailableTerminalAudit : ITerminalAudit
{
    public ValueTask RecordAsync(string kind, TerminalCaller caller, string terminalId, CancellationToken ct) =>
        throw new InvalidOperationException("No audit sink is registered for terminals.");
}
