using Bazaar.Server.Components.PrivacyAudit;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Components.Remote;

/// <summary>Pairing settings (doc 32 section 3 and Settings). Defaults are the shipped values.</summary>
public sealed class PairingOptions
{
    /// <summary>How long an offer's user code stays usable, in minutes (<c>pairing.offer_ttl_minutes</c>).</summary>
    public int OfferTtlMinutes { get; set; } = 10;

    /// <summary>The poll interval the device is told to keep, in seconds (RFC 8628 <c>interval</c>).</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is not usable.</summary>
    public void Validate()
    {
        if (OfferTtlMinutes is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(OfferTtlMinutes), OfferTtlMinutes, "The offer lifetime must be 1 to 60 minutes.");
        if (PollIntervalSeconds is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(PollIntervalSeconds), PollIntervalSeconds, "The poll interval must be 1 to 60 seconds.");
    }
}

/// <summary>The offer a signed-in human created. <see cref="UserCode"/> is shown once; the server keeps its hash.</summary>
public sealed record PairingOfferCreated(Guid OfferId, string UserCode, DateTimeOffset ExpiresAt);

/// <summary>What the device gets back from the start: the poll secret and the interval to keep.</summary>
public sealed record DeviceAuthorization(Guid OfferId, string DeviceCode, int IntervalSeconds);

/// <summary>Result of one poll that did not end pairing.</summary>
public enum PairingPollStatus
{
    /// <summary>The approver has not answered yet.</summary>
    Pending,

    /// <summary>The device polled faster than its interval. It should wait longer (RFC 8628 <c>slow_down</c>).</summary>
    SlowDown,

    /// <summary>The approver approved. The device is paired and holds these tokens.</summary>
    Granted,
}

/// <summary>A poll answer. <see cref="Tokens"/> and <see cref="DeviceId"/> are set only when granted.</summary>
public sealed record PairingPoll(PairingPollStatus Status, TokenPair? Tokens = null, Guid? DeviceId = null);

/// <summary>An offer action was refused. <see cref="Code"/> is a short reason for the server log, never for the device.</summary>
public sealed class PairingRefusedException(string code) : Exception("The pairing request was refused.")
{
    public string Code { get; } = code;
}

/// <summary>A poll ended pairing with an RFC 8628 error: <c>expired_token</c>, <c>access_denied</c> or <c>invalid_grant</c>.</summary>
public sealed class PairingPollException(string error) : Exception("The device authorization ended.")
{
    /// <summary>The RFC 8628 error code the route returns to the device.</summary>
    public string Error { get; } = error;
}

/// <summary>Pairing a new device (doc 32 section 3): offers, the device's authorization and poll, and the approver's answer.</summary>
public interface IPairingService
{
    Task<PairingOfferCreated> CreateOfferAsync(Guid createdBy, string role, CancellationToken ct = default);

    Task<DeviceAuthorization> StartDeviceAuthorizationAsync(string userCode, string label, string deviceClass, CancellationToken ct = default);

    Task ApproveAsync(Guid offerId, Guid approverHumanId, CancellationToken ct = default);

    Task DenyAsync(Guid offerId, Guid approverHumanId, CancellationToken ct = default);

    Task<PairingPoll> PollAsync(string deviceCode, CancellationToken ct = default);
}

/// <summary>
/// The pairing state machine. An offer is single use and bound to the human who created it: only that human can approve it,
/// and each state change is one compare-and-set on <see cref="IPairingOfferStore"/>. The first grant creates the device and its
/// first refresh credential through <see cref="IRefreshCredentialStore"/>, then issues an access token.
/// </summary>
public sealed class PairingService(
    IPairingOfferStore offers,
    IRefreshCredentialStore credentials,
    IAccessTokenIssuer accessTokens,
    PrivacyAuditRecorder audit,
    IOptions<RemoteTokenOptions> tokenOptions,
    IOptions<PairingOptions> pairingOptions,
    TimeProvider time) : IPairingService
{
    private static readonly string[] _roles = ["owner", "coworker"];
    private static readonly string[] _classes = ["windows", "linux", "android", "terminal", "other"];

    private readonly RemoteTokenOptions _tokens = ValidatedTokens(tokenOptions);
    private readonly PairingOptions _pairing = ValidatedPairing(pairingOptions);

    public async Task<PairingOfferCreated> CreateOfferAsync(Guid createdBy, string role, CancellationToken ct = default)
    {
        if (createdBy == Guid.Empty) throw new ArgumentException("A creator is required.", nameof(createdBy));
        if (Array.IndexOf(_roles, role) < 0) throw new PairingRefusedException("unknown_role");

        var now = time.GetUtcNow();
        var userCode = PairingCodes.NewUserCode();
        var offer = new PairingOfferRecord(
            Id: Guid.CreateVersion7(now),
            CreatedBy: createdBy,
            Role: role,
            UserCodeHash: PairingCodes.HashUserCode(userCode),
            DeviceCodeHash: null,
            ExpiresAt: now.AddMinutes(_pairing.OfferTtlMinutes),
            State: "pending",
            DeviceLabel: null,
            DeviceClass: null,
            LastPolledAt: null);

        await offers.InsertAsync(offer, ct);
        await audit.OfferCreatedAsync(createdBy.ToString("D"), offer.Id.ToString("D"), role, _pairing.OfferTtlMinutes, ct);
        return new PairingOfferCreated(offer.Id, userCode, offer.ExpiresAt);
    }

    public async Task<DeviceAuthorization> StartDeviceAuthorizationAsync(string userCode, string label, string deviceClass, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userCode);
        ArgumentNullException.ThrowIfNull(label);
        if (label.Length is < 1 or > 200) throw new PairingRefusedException("bad_label");
        if (Array.IndexOf(_classes, deviceClass) < 0) throw new PairingRefusedException("bad_class");

        var normalized = PairingCodes.Normalize(userCode);
        if (!PairingCodes.IsWellFormed(normalized)) throw new PairingRefusedException("unknown_code");

        var offer = await offers.FindByUserCodeHashAsync(PairingCodes.HashUserCode(normalized), ct)
            ?? throw new PairingRefusedException("unknown_code");
        var now = time.GetUtcNow();
        if (offer.State != "pending" || offer.DeviceCodeHash is not null) throw new PairingRefusedException("already_used");
        if (now >= offer.ExpiresAt)
        {
            await offers.TryTransitionAsync(offer.Id, "pending", "expired", ct);
            throw new PairingRefusedException("expired");
        }

        var deviceCode = RefreshCredentialSecret.Generate();
        var attached = await offers.TryAttachDeviceAsync(offer.Id, RefreshCredentialSecret.Hash(deviceCode), label, deviceClass, ct);
        if (!attached) throw new PairingRefusedException("already_used");
        return new DeviceAuthorization(offer.Id, deviceCode, _pairing.PollIntervalSeconds);
    }

    public async Task ApproveAsync(Guid offerId, Guid approverHumanId, CancellationToken ct = default)
    {
        var offer = await RequireOwnPendingOfferAsync(offerId, approverHumanId, ct);
        if (!await offers.TryTransitionAsync(offer.Id, "pending", "approved", ct))
            throw new PairingRefusedException("already_used");
    }

    public async Task DenyAsync(Guid offerId, Guid approverHumanId, CancellationToken ct = default)
    {
        var offer = await RequireOwnPendingOfferAsync(offerId, approverHumanId, ct);
        if (!await offers.TryTransitionAsync(offer.Id, "pending", "denied", ct))
            throw new PairingRefusedException("already_used");
    }

    public async Task<PairingPoll> PollAsync(string deviceCode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceCode);
        var offer = await offers.FindByDeviceCodeHashAsync(RefreshCredentialSecret.Hash(deviceCode), ct)
            ?? throw new PairingPollException("invalid_grant");
        var now = time.GetUtcNow();

        switch (offer.State)
        {
            case "redeemed":
                throw new PairingPollException("invalid_grant");
            case "denied":
                throw new PairingPollException("access_denied");
            case "expired":
                throw new PairingPollException("expired_token");
            case "pending" when now >= offer.ExpiresAt:
                await offers.TryTransitionAsync(offer.Id, "pending", "expired", ct);
                throw new PairingPollException("expired_token");
            case "approved" when now >= offer.ExpiresAt:
                // An approval still counts only inside the offer's lifetime.
                await offers.TryTransitionAsync(offer.Id, "approved", "expired", ct);
                throw new PairingPollException("expired_token");
            case "pending":
                var tooSoon = offer.LastPolledAt is { } last && now - last < TimeSpan.FromSeconds(_pairing.PollIntervalSeconds);
                await offers.TouchPollAsync(offer.Id, now, ct);
                return new PairingPoll(tooSoon ? PairingPollStatus.SlowDown : PairingPollStatus.Pending);
            case "approved":
                return await RedeemAsync(offer, now, ct);
            default:
                throw new PairingPollException("invalid_grant");
        }
    }

    private async Task<PairingOfferRecord> RequireOwnPendingOfferAsync(Guid offerId, Guid approverHumanId, CancellationToken ct)
    {
        var offer = await offers.FindAsync(offerId, ct) ?? throw new PairingRefusedException("unknown_offer");
        if (offer.CreatedBy != approverHumanId) throw new PairingRefusedException("not_creator");
        if (offer.State != "pending" || offer.DeviceCodeHash is null) throw new PairingRefusedException("not_pending");
        if (time.GetUtcNow() >= offer.ExpiresAt) throw new PairingRefusedException("expired");
        return offer;
    }

    private async Task<PairingPoll> RedeemAsync(PairingOfferRecord offer, DateTimeOffset now, CancellationToken ct)
    {
        // Redeem first: a second poll of the same approval finds "redeemed" and gets nothing.
        if (!await offers.TryTransitionAsync(offer.Id, "approved", "redeemed", ct))
            throw new PairingPollException("invalid_grant");

        var deviceId = Guid.CreateVersion7(now);
        var credentialId = Guid.CreateVersion7(now);
        var secret = RefreshCredentialSecret.Generate();
        var refreshExpiresAt = now.AddDays(_tokens.RefreshSlidingDays);
        var accessExpiresAt = now.AddMinutes(_tokens.AccessTokenMinutes);
        var device = new DeviceRecord(deviceId, offer.CreatedBy, offer.DeviceLabel ?? string.Empty, offer.DeviceClass ?? "other", 1, null);

        await credentials.CreateDeviceAsync(device, credentialId, RefreshCredentialSecret.Hash(secret), now, refreshExpiresAt, ct);
        var access = accessTokens.Issue(deviceId, offer.CreatedBy, accessExpiresAt);
        await audit.PairingIssuedAsync(offer.CreatedBy.ToString("D"), offer.Id.ToString("D"), deviceId.ToString("D"), device.Class, ct);

        return new PairingPoll(
            PairingPollStatus.Granted,
            new TokenPair(access, accessExpiresAt, secret, refreshExpiresAt),
            deviceId);
    }

    private static RemoteTokenOptions ValidatedTokens(IOptions<RemoteTokenOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();
        return options.Value;
    }

    private static PairingOptions ValidatedPairing(IOptions<PairingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Value.Validate();
        return options.Value;
    }
}
