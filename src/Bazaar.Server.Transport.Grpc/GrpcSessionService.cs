using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Transport.Grpc.Generated;
using Bazaar.Wire.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Transport.Grpc;

/// <summary>Open gRPC-Web sessions by id. One instance per server.</summary>
internal sealed class GrpcWebSessionRegistry
{
    public ConcurrentDictionary<string, GrpcWebSession> Sessions { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// The service <c>bazaar.wire.v1.Session</c> (doc 90 section 9). Each accepted call checks the credential through
/// <see cref="IWireTransportAuthenticator"/>, picks the codec from the request, and hands one
/// <see cref="IWireConnection"/> to <see cref="IWireConnectionHandoff"/>.
/// </summary>
internal sealed partial class GrpcSessionService : Session.SessionBase
{
    private const int _minSessionIdLength = 16;
    private const int _maxSessionIdLength = 128;

    private readonly IWireTransportAuthenticator _authenticator;
    private readonly IWireConnectionHandoff _handoff;
    private readonly GrpcTransportOptions _options;
    private readonly WireTransportsEnabledOptions _enabled;
    private readonly GrpcWebSessionRegistry _registry;
    private readonly ILogger<GrpcSessionService> _log;

    public GrpcSessionService(
        IWireTransportAuthenticator authenticator,
        IWireConnectionHandoff handoff,
        IOptions<GrpcTransportOptions> options,
        IOptions<WireTransportsEnabledOptions> enabled,
        GrpcWebSessionRegistry registry,
        ILogger<GrpcSessionService> log)
    {
        _authenticator = authenticator;
        _handoff = handoff;
        _options = options.Value;
        _enabled = enabled.Value;
        _registry = registry;
        _log = log;
    }

    public override async Task Connect(IAsyncStreamReader<Frame> requestStream, IServerStreamWriter<Frame> responseStream, ServerCallContext context)
    {
        var (auth, profile) = await AdmitAsync(WireNames.Grpc, context).ConfigureAwait(false);
        await context.WriteResponseHeadersAsync(new Metadata { { GrpcMetadata.Codec, profile.Codec } }).ConfigureAwait(false);
        var connection = new GrpcServerConnection(profile, requestStream, responseStream, _options.MaxFrameBytes);
        try
        {
            await _handoff.HandleAsync(connection, auth, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (connection.CloseCode is { } code) context.ResponseTrailers.Add(GrpcMetadata.CloseCode, code.ToString(CultureInfo.InvariantCulture));
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async Task Subscribe(SubscribeRequest request, IServerStreamWriter<Frame> responseStream, ServerCallContext context)
    {
        var id = request.SessionId;
        if (!IsValidSessionId(id)) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid session id."));
        var (auth, profile) = await AdmitAsync(WireNames.GrpcWeb, context).ConfigureAwait(false);
        var session = new GrpcWebSession(Credential(context)!, profile, responseStream, _options.MaxFrameBytes, _options.WebInboundQueueDepth);
        if (!_registry.Sessions.TryAdd(id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new RpcException(new Status(StatusCode.AlreadyExists, "Session id in use."));
        }

        await context.WriteResponseHeadersAsync(new Metadata { { GrpcMetadata.Codec, profile.Codec } }).ConfigureAwait(false);
        using var gone = context.CancellationToken.Register(session.Abort);
        try
        {
            await _handoff.HandleAsync(session, auth, context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _registry.Sessions.TryRemove(id, out _);
            if (session.CloseCode is { } code) context.ResponseTrailers.Add(GrpcMetadata.CloseCode, code.ToString(CultureInfo.InvariantCulture));
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override async Task<Frame> Send(Frame request, ServerCallContext context)
    {
        EnsureEnabled(WireNames.GrpcWeb);
        var id = context.RequestHeaders.GetValue(GrpcMetadata.Session);
        if (!IsValidSessionId(id) || !_registry.Sessions.TryGetValue(id!, out var session))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "No such session."));
        }

        if (!SameCredential(Credential(context), session.Credential)) throw Unauthenticated();
        await session.EnqueueAsync(request.Data, context.CancellationToken).ConfigureAwait(false);
        return new Frame();
    }

    private async Task<(WireAuthResult Auth, WireProfile Profile)> AdmitAsync(string transport, ServerCallContext context)
    {
        EnsureEnabled(transport);
        var credential = Credential(context);
        if (credential is null) throw Unauthenticated();
        var auth = await _authenticator.AuthenticateAsync(credential, transport, context.RequestHeaders.GetValue(GrpcMetadata.Device), context.CancellationToken).ConfigureAwait(false);
        if (!auth.Ok)
        {
            LogRefused(transport, auth.Reason ?? "refused");
            throw Unauthenticated();
        }

        var offered = RequestedCodecs(context);
        var codec = offered.Count == 0
            ? (_options.EnabledCodecs.Contains(_options.FloorCodec, StringComparer.Ordinal) ? _options.FloorCodec : null)
            : WireNegotiator.PickCodec(offered, _options.EnabledCodecs);
        if (codec is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "No common codec."), new Metadata { { GrpcMetadata.CloseCode, "4400" } });
        }

        return (auth, new WireProfile(transport, codec));
    }

    private void EnsureEnabled(string transport)
    {
        if (!_enabled.IsEnabled(transport)) throw new RpcException(new Status(StatusCode.Unimplemented, "Transport not enabled."));
    }

    private static string? Credential(ServerCallContext context)
    {
        var value = context.RequestHeaders.GetValue(GrpcMetadata.Authorization);
        if (string.IsNullOrWhiteSpace(value)) return null;
        const string scheme = "Bearer ";
        var token = value.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? value[scheme.Length..].Trim() : value.Trim();
        return token.Length == 0 ? null : token;
    }

    /// <summary>Codec names from <c>bazaar-codec</c>, else from a <c>content-type</c> suffix (<c>application/grpc+cbor</c>); empty when neither is present.</summary>
    private static List<string> RequestedCodecs(ServerCallContext context)
    {
        var names = new List<string>();
        var header = context.RequestHeaders.GetValue(GrpcMetadata.Codec);
        if (!string.IsNullOrWhiteSpace(header))
        {
            names.AddRange(header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            return names;
        }

        var contentType = context.GetHttpContext().Request.ContentType;
        if (contentType is not null && contentType.StartsWith(GrpcMetadata.ContentTypePrefix + "+", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = contentType[(GrpcMetadata.ContentTypePrefix.Length + 1)..].Split(';')[0].Trim();
            if (suffix.Length > 0) names.Add(suffix);
        }

        return names;
    }

    private static bool IsValidSessionId(string? id) =>
        id is { Length: >= _minSessionIdLength and <= _maxSessionIdLength } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool SameCredential(string? presented, string expected)
    {
        if (presented is null) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private static RpcException Unauthenticated() =>
        new(new Status(StatusCode.Unauthenticated, "Authentication failed."), new Metadata { { GrpcMetadata.CloseCode, "4401" } });

    [LoggerMessage(Level = LogLevel.Information, Message = "gRPC {Transport} connection refused: {Reason}")]
    private partial void LogRefused(string transport, string reason);
}
