using Bazaar.Contracts.Gc;
using Bazaar.Contracts.TimeMachine;
using Bazaar.Contracts.Wire;
using Bazaar.Server.Gc.Read;
using Bazaar.Server.TimeMachine;
using Bazaar.Server.Wire.Connection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bazaar.Server.Host.Wire;

/// <summary>
/// Wire adapters for <c>replay.frame</c> and <c>replay.fork</c> (docs/design/02, replay and fork requests). The Time Machine
/// module may not reference Bazaar.Server, so the composition root adapts its <see cref="ReplayWireHandler"/> to the wire host. The
/// adapters hold no rules: they decode, map the principal, call the handler and map the reply. The per-connection token bucket
/// that already covers <c>gc.history</c> covers these requests too, because it runs before any handler.
/// </summary>
internal static class ReplayWireMapping
{
    /// <summary>The same mapping the GC history handler uses (wire principal kind to GC principal).</summary>
    public static GcPrincipal PrincipalOf(PrincipalRef principal) => new(
        principal.Id,
        principal.Kind switch
        {
            "owner" => PrincipalKind.Owner,
            "human" => PrincipalKind.Human,
            "agent" => PrincipalKind.Agent,
            _ => throw new InvalidOperationException($"Unknown principal kind '{principal.Kind}'."),
        },
        string.Empty);

    public static WireReply ToReply<T>(WireRequestContext context, ReplayWireReply<T> reply) =>
        reply.IsOk
            ? WireReply.Ok(context.Connection.Codec, reply.Value!)
            : WireReply.Fail(reply.ErrorCode!, reply.Message);
}

/// <summary><c>replay.frame</c>: a GC rendered at a seq.</summary>
public sealed class ReplayFrameWireAdapter(IServiceProvider services) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.ReplayFrame;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<ReplayFrameRequest>();
        var handler = services.GetRequiredService<ReplayWireHandler>();
        var reply = await handler.FrameAsync(ReplayWireMapping.PrincipalOf(context.Connection.Principal), request, cancellationToken)
            .ConfigureAwait(false);
        return ReplayWireMapping.ToReply(context, reply);
    }
}

/// <summary><c>replay.fork</c>: forks a project GC at a seq. Answers <c>unsupported</c> while no fork target provisioner is bound.</summary>
public sealed class ReplayForkWireAdapter(IServiceProvider services) : IWireRequestHandler
{
    /// <inheritdoc />
    public string Type => WireTypes.ReplayFork;

    /// <inheritdoc />
    public async ValueTask<WireReply> HandleAsync(WireRequestContext context, CancellationToken cancellationToken)
    {
        var request = context.Payload<ReplayForkRequest>();
        if (services.GetService<IForkTargetProvisioner>() is null)
            return WireReply.Fail(WireErrorCodes.Unsupported, "Forking is not available on this server yet.");

        var handler = services.GetRequiredService<ReplayWireHandler>();
        var reply = await handler.ForkAsync(ReplayWireMapping.PrincipalOf(context.Connection.Principal), request, cancellationToken)
            .ConfigureAwait(false);
        return ReplayWireMapping.ToReply(context, reply);
    }
}

/// <summary>Registers the replay wire adapters.</summary>
public static class ReplayWireServiceCollectionExtensions
{
    /// <summary>
    /// Registers <c>replay.frame</c> and <c>replay.fork</c>. Needs the Time Machine component (projector, fork service) and the
    /// GC read services. The adapters resolve the handler on the first request, so a host without a fork target provisioner still
    /// builds and serves frames; its forks answer <c>unsupported</c>.
    /// </summary>
    public static IServiceCollection AddBazaarReplayWire(this IServiceCollection services)
    {
        services.TryAddSingleton(sp => new ReplayWireHandler(
            sp.GetRequiredService<IReplayProjector>(),
            // The fork service needs a provisioner; without one the fork adapter answers before it would be used.
            sp.GetService<IForkTargetProvisioner>() is null ? new UnboundForkService() : sp.GetRequiredService<IForkService>(),
            sp.GetRequiredService<IGcReadAccess>(),
            sp.GetRequiredService<IGcLogReader>()));
        services.AddSingleton<IWireRequestHandler>(sp => new ReplayFrameWireAdapter(RequireBindings(sp)));
        services.AddSingleton<IWireRequestHandler>(sp => new ReplayForkWireAdapter(RequireBindings(sp)));
        return services;
    }

    /// <summary>
    /// Fail closed at startup: wire handlers are built when the connection manager is, so a host without the Time Machine
    /// component or the GC read services stops there instead of answering the first replay request with an internal error.
    /// The fork provisioner is deliberately not required; forks answer <c>unsupported</c> without one.
    /// </summary>
    private static IServiceProvider RequireBindings(IServiceProvider sp)
    {
        _ = sp.GetRequiredService<IReplayProjector>();
        _ = sp.GetRequiredService<IGcReadAccess>();
        _ = sp.GetRequiredService<IGcLogReader>();
        return sp;
    }

    private sealed class UnboundForkService : IForkService
    {
        public Task<ForkResult> ForkAsync(ForkRequest request, CancellationToken ct) =>
            throw new NotSupportedException("No fork target provisioner is bound.");
    }
}
