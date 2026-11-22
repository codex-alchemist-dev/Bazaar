using Bazaar.Updates;

namespace Bazaar.Client.Updates;

/// <summary>
/// The <see cref="IClientUpdater"/>: Velopack (behind <see cref="IVelopackAdapter"/>) moves the bytes, and Bazaar.Updates
/// decides and verifies. A release is offered only when the signed manifest agrees with the feed and
/// <see cref="UpdateDecision"/> accepts it. It is applied only after <see cref="UpdateVerifier"/> passes on the downloaded package.
/// </summary>
public sealed class ClientUpdater : IClientUpdater, IDisposable
{
    private readonly ClientUpdateOptions _options;
    private readonly IVelopackAdapter _adapter;
    private readonly IClientUpdateManifestSource _manifests;
    private readonly IUpdateSignatureVerifier _signatures;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManifest? _manifest;

    public ClientUpdater(
        ClientUpdateOptions options,
        IVelopackAdapter adapter,
        IClientUpdateManifestSource manifests,
        IUpdateSignatureVerifier signatures)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(manifests);
        ArgumentNullException.ThrowIfNull(signatures);

        _options = options;
        _adapter = adapter;
        _manifests = manifests;
        _signatures = signatures;
        State = new ClientUpdateState(ClientUpdatePhase.Idle, options.Channel);
    }

    public UpdateChannel Channel => _options.Channel;

    public ClientUpdateState State { get; private set; }

    public void SetChannel(UpdateChannel channel)
    {
        _options.Channel = channel;
        _manifest = null;
        State = new ClientUpdateState(ClientUpdatePhase.Idle, channel);
    }

    public async Task<ClientUpdateState> CheckAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _manifest = null;
            var channel = Channel;
            var installedText = _adapter.InstalledVersion;
            if (installedText is null)
            {
                return Set(ClientUpdatePhase.Unsupported, channel, detail: "This build is not a Velopack install.");
            }

            if (!UpdateVersion.TryParse(installedText, out var installed))
            {
                return Fail(channel, installedText, ClientUpdateFailure.Error, $"Installed version '{installedText}' is not a Bazaar version.");
            }

            var feed = _options.ResolveFeed(channel);
            if (feed is null)
            {
                return Fail(channel, installedText, ClientUpdateFailure.Error, "No update feed is configured.");
            }

            if (!ClientUpdateOptions.IsSecureFeed(feed))
            {
                return Set(ClientUpdatePhase.Refused, channel, installedText, detail: "The update feed is not HTTPS.");
            }

            Set(ClientUpdatePhase.Checking, channel, installedText);
            var offeredText = await _adapter.CheckAsync(feed, _options.VelopackChannel(channel), ct).ConfigureAwait(false);
            if (offeredText is null)
            {
                return Set(ClientUpdatePhase.UpToDate, channel, installedText);
            }

            // Fail closed: a feed offer with no signed manifest is never installed.
            var manifest = await _manifests.GetManifestAsync(channel, ct).ConfigureAwait(false);
            if (manifest is null)
            {
                return Fail(channel, installedText, ClientUpdateFailure.ManifestMissing, "The company server has no signed manifest for the offered release.", offeredText);
            }

            if (!UpdateVersion.TryParse(offeredText, out var offered)
                || !UpdateVersion.TryParse(manifest.Version, out var signed)
                || offered.CompareTo(signed) != 0)
            {
                return Fail(channel, installedText, ClientUpdateFailure.VersionMismatch, "The feed and the signed manifest name different versions.", offeredText);
            }

            var decision = UpdateDecision.Decide(installed, manifest);
            if (!decision.Apply)
            {
                return decision.Refusal == UpdateRefusal.NotNewer
                    ? Set(ClientUpdatePhase.UpToDate, channel, installedText)
                    : Set(ClientUpdatePhase.Refused, channel, installedText, offeredText, detail: $"Offer refused: {decision.Refusal}.");
            }

            _manifest = manifest;
            return Set(ClientUpdatePhase.Available, channel, installedText, offeredText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(Channel, _adapter.InstalledVersion, ClientUpdateFailure.Error, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClientUpdateState> DownloadAsync(IProgress<int>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = State;
            if (current.Phase != ClientUpdatePhase.Available || _manifest is null)
            {
                return current;
            }

            Set(ClientUpdatePhase.Downloading, current.Channel, current.InstalledVersion, current.OfferedVersion);
            var path = await _adapter.DownloadAsync(progress, ct).ConfigureAwait(false);

            var length = new FileInfo(path).Length;
            if (length > UpdateFeedClient.MaxPackageBytes)
            {
                return Fail(current.Channel, current.InstalledVersion, ClientUpdateFailure.PackageTooLarge, "The downloaded package is over the size limit.", current.OfferedVersion);
            }

            var package = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (!UpdateVerifier.Verify(_manifest, package, _signatures))
            {
                _manifest = null;
                return Fail(current.Channel, current.InstalledVersion, ClientUpdateFailure.VerificationFailed, "The downloaded package failed hash or signature verification.", current.OfferedVersion);
            }

            return Set(ClientUpdatePhase.Ready, current.Channel, current.InstalledVersion, current.OfferedVersion, percent: 100);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(State.Channel, State.InstalledVersion, ClientUpdateFailure.Error, ex.Message, State.OfferedVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ClientUpdateState> ApplyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Only a verified download may be applied.
            if (State.Phase != ClientUpdatePhase.Ready)
            {
                return State;
            }

            _adapter.ApplyAndRestart();
            return State;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(State.Channel, State.InstalledVersion, ClientUpdateFailure.Error, ex.Message, State.OfferedVersion);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private ClientUpdateState Set(
        ClientUpdatePhase phase,
        UpdateChannel channel,
        string? installed = null,
        string? offered = null,
        int percent = 0,
        string? detail = null)
    {
        State = new ClientUpdateState(phase, channel, installed, offered, percent, ClientUpdateFailure.None, detail);
        return State;
    }

    private ClientUpdateState Fail(UpdateChannel channel, string? installed, ClientUpdateFailure failure, string detail, string? offered = null)
    {
        State = new ClientUpdateState(ClientUpdatePhase.Failed, channel, installed, offered, 0, failure, detail);
        return State;
    }
}
