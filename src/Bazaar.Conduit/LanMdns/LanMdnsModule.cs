using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bazaar.Conduit.LanMdns;

/// <summary>
/// LAN discovery over mDNS (doc 39, section 2, catalogue row <c>lan-mdns</c>). Advertises <c>_bazaar._tcp.local</c> and
/// answers its queries, and publishes one <c>wss</c> offer per LAN IPv4 address. mDNS proves nothing about reachability,
/// so offers are never marked verified. Start failures are reported through <see cref="Status"/>, not thrown.
/// </summary>
public sealed class LanMdnsModule : IConduitModule, IAsyncDisposable
{
    /// <summary>Stable module id.</summary>
    public const string ModuleId = "lan";

    private const string _instanceSetting = "conduit.lan.instance";
    private const string _versionSetting = "server.version";
    private const string _defaultInstance = "bazaar";
    private const string _defaultVersion = "0.0.0";
    private const string _keyHashPrefix = "sha256:";
    private const int _keyHashTxtLength = 16;
    private const int _maxInstanceBytes = 63;
    private const int _maxVersionLength = 64;

    private readonly ILanAddressSource _addresses;
    private readonly Func<IReadOnlyList<IPAddress>, IMulticastResponder> _responderFactory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _gate = new();
    private ModuleStatus _status = new(ModuleId, ConduitModuleState.Disabled, null, []);
    private IMulticastResponder? _socket;
    private MdnsResponder? _responder;
    private ILogger _logger = NullLogger.Instance;

    /// <summary>Creates the module over the real network interfaces and the real mDNS socket.</summary>
    public LanMdnsModule()
        : this(new SystemLanAddressSource(), addresses => new UdpMulticastResponder(addresses))
    {
    }

    /// <summary>Creates the module over the given address source and socket factory. Tests pass fakes.</summary>
    public LanMdnsModule(ILanAddressSource addresses, Func<IReadOnlyList<IPAddress>, IMulticastResponder> responderFactory)
    {
        _addresses = addresses ?? throw new ArgumentNullException(nameof(addresses));
        _responderFactory = responderFactory ?? throw new ArgumentNullException(nameof(responderFactory));
    }

    /// <inheritdoc />
    public string Id => ModuleId;

    /// <inheritdoc />
    public string DisplayName => "LAN discovery (mDNS)";

    /// <inheritdoc />
    public ConduitScope Scope => ConduitScope.Lan;

    /// <inheritdoc />
    public int Priority => 10;

    /// <inheritdoc />
    public bool DefaultEnabled => true;

    /// <inheritdoc />
    public ModuleStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ModuleStatus>? StatusChanged;

    /// <summary>Stops the module and releases its lifecycle gate.</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(ConduitContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(context.LocalPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(context.LocalPort, ushort.MaxValue);

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is not null)
            {
                return;
            }

            SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Starting, null, []));

            _logger = context.Logger;
            var addresses = LookUpAddresses(context);
            if (addresses.Count == 0)
            {
                LanMdnsLog.NoLanAddress(context.Logger);
                SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Failed, "no-lan-address", []));
                return;
            }

            var instance = InstanceLabel(context.Settings.Read(_instanceSetting, _defaultInstance));
            var version = Sanitize(context.Settings.Read(_versionSetting, _defaultVersion));
            var txt = new List<string>
            {
                $"keyhash={KeyHashTxt(context.ServerKeyHash)}",
                $"ver={version}",
            };
            var responder = new MdnsResponder(instance, (ushort)context.LocalPort, txt, addresses);

            var socket = _responderFactory(addresses);
            try
            {
                await socket.StartAsync(
                    (datagram, ct) => AnswerAsync(socket, responder, datagram, ct),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await socket.DisposeAsync().ConfigureAwait(false);
                if (ex is OperationCanceledException)
                {
                    SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Disabled, null, []));
                    throw;
                }

                LanMdnsLog.BindFailed(context.Logger, ex);
                SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Failed, "bind-failed", []));
                return;
            }

            _socket = socket;
            _responder = responder;

            try
            {
                await socket.SendAsync(MdnsCodec.Encode(responder.Advertise(goodbye: false)), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LanMdnsLog.SendFailed(context.Logger, ex);
            }

            var offers = addresses
                .Select(address => new ConduitOffer(
                    ModuleId,
                    Scope,
                    new Uri($"wss://{address}:{context.LocalPort}/ws"),
                    context.ServerKeyHash,
                    ExpiresAt: null,
                    RequiresRelay: false,
                    Verified: false))
                .ToList();
            SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Running, null, offers));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var socket = _socket;
            var responder = _responder;
            _socket = null;
            _responder = null;

            if (socket is not null && responder is not null)
            {
                try
                {
                    await socket.SendAsync(MdnsCodec.Encode(responder.Advertise(goodbye: true)), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The goodbye is best effort; the socket is released either way.
                }
                catch (Exception ex)
                {
                    LanMdnsLog.SendFailed(_logger, ex);
                }

                await socket.DisposeAsync().ConfigureAwait(false);
            }

            SetStatus(new ModuleStatus(ModuleId, ConduitModuleState.Disabled, null, []));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static async ValueTask AnswerAsync(IMulticastResponder socket, MdnsResponder responder, MulticastDatagram datagram, CancellationToken cancellationToken)
    {
        MdnsMessage query;
        try
        {
            query = MdnsCodec.Decode(datagram.Payload);
        }
        catch (FormatException)
        {
            return;
        }

        var response = responder.Respond(query);
        if (response is not null)
        {
            await socket.SendAsync(MdnsCodec.Encode(response), cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<IPAddress> LookUpAddresses(ConduitContext context)
    {
        try
        {
            return _addresses.GetLanAddresses();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LanMdnsLog.AddressLookupFailed(context.Logger, ex);
            return [];
        }
    }

    private void SetStatus(ModuleStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private static string KeyHashTxt(string serverKeyHash)
    {
        var hex = serverKeyHash.StartsWith(_keyHashPrefix, StringComparison.Ordinal)
            ? serverKeyHash[_keyHashPrefix.Length..]
            : serverKeyHash;
        return hex.Length <= _keyHashTxtLength ? hex : hex[.._keyHashTxtLength];
    }

    private static string InstanceLabel(string configured)
    {
        var label = configured.Trim().Replace('.', '-');
        if (label.Length == 0)
        {
            label = _defaultInstance;
        }

        while (Encoding.UTF8.GetByteCount(label) > _maxInstanceBytes)
        {
            label = label[..^1];
        }

        return label;
    }

    private static string Sanitize(string value)
    {
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0)
        {
            return _defaultVersion;
        }

        return clean.Length <= _maxVersionLength ? clean : clean[.._maxVersionLength];
    }
}
