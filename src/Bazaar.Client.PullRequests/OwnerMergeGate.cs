using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Client.Mobile;
using Bazaar.Contracts.GitHub;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// The phone's merge approval (doc 21, Merge request path; doc 29 Approvals). Each approval composes one fresh biometric result
/// from <see cref="IBiometricPrompt"/> with one device signature over the shared owner payload (<c>bazaar.owner.v1</c>, action
/// <c>pr.merge</c>, args hash from <see cref="PrMergeArgs"/>). Rules:
/// <list type="bullet">
/// <item>Only a <see cref="OwnerActionSource.Button"/> tap can ask. Dictation and anything else are refused before the prompt.</item>
/// <item>Nothing is cached. Every approval prompts again; a biometric success signs within the grace period, and the signature expires with it.</item>
/// <item>The signed payload carries the head sha, so a signature is void for any other head. Nothing is reused across heads.</item>
/// <item>A signed approval is single use: <see cref="TrySpend"/> returns false the second time.</item>
/// <item>Merging is off when <c>android.merge_from_folded</c> is off; that is checked before the prompt.</item>
/// </list>
/// </summary>
public sealed class OwnerMergeGate : IOwnerActionGate
{
    /// <summary>The action name for a squash merge.</summary>
    public const string MergeAction = "pr.merge";

    private readonly IBiometricPrompt _biometric;
    private readonly IDeviceApprovalSigner _signer;
    private readonly Guid _humanId;
    private readonly AndroidSettings _settings;
    private readonly ApprovalGateOptions _options;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly HashSet<Guid> _spent = [];

    /// <summary>Creates the gate. The signer's device id must have the allowed shape.</summary>
    /// <param name="humanId">The owner's human id, as the server knows it.</param>
    /// <param name="options">Biometric grace; clamped to 1 to 30 seconds. Null uses the 30 second ceiling.</param>
    public OwnerMergeGate(
        IBiometricPrompt biometric,
        IDeviceApprovalSigner signer,
        Guid humanId,
        AndroidSettings settings,
        ApprovalGateOptions? options = null,
        TimeProvider? clock = null)
    {
        _biometric = biometric ?? throw new ArgumentNullException(nameof(biometric));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _options = options ?? new ApprovalGateOptions();
        _clock = clock ?? TimeProvider.System;
        _humanId = humanId;
        if (!ApprovalSigningPayload.IsValidDeviceId(_signer.DeviceId))
        {
            throw new ArgumentException("The signer's device id has a disallowed shape.", nameof(signer));
        }
    }

    /// <inheritdoc />
    public async ValueTask<OwnerActionResult> AuthorizeMergeAsync(OwnerMergeIntent intent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (intent.Source != OwnerActionSource.Button)
        {
            return Refused(OwnerActionRefusal.NotFromButton);
        }

        if (!_settings.MergeFromFolded)
        {
            return Refused(OwnerActionRefusal.MergeDisabled);
        }

        if (!IsWellFormed(intent))
        {
            return Refused(OwnerActionRefusal.InvalidRequest);
        }

        var argsHash = PrMergeArgs.Hash(intent.Pr, intent.HeadSha, intent.CommitTitle, intent.CommitMessage);
        var approvalId = Guid.NewGuid();
        var subtitle = $"{intent.Pr.Owner}/{intent.Pr.Repo}#{intent.Pr.Number} at {intent.HeadSha[..7]}";

        BiometricOutcome outcome;
        try
        {
            outcome = await _biometric.AuthenticateAsync(
                new BiometricRequest(approvalId, Convert.ToHexStringLower(argsHash), "Confirm merge", subtitle),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            outcome = BiometricOutcome.Cancelled;
        }
#pragma warning disable CA1031 // Any prompt failure means no biometric success; the reason is not trusted detail.
        catch (Exception)
#pragma warning restore CA1031
        {
            outcome = BiometricOutcome.Failed;
        }

        if (outcome != BiometricOutcome.Succeeded)
        {
            return Refused(outcome switch
            {
                BiometricOutcome.Cancelled => OwnerActionRefusal.BiometricCancelled,
                BiometricOutcome.Unavailable => OwnerActionRefusal.BiometricUnavailable,
                _ => OwnerActionRefusal.BiometricFailed,
            });
        }

        var grace = _options.EffectiveBiometricGrace;
        var succeededAt = _clock.GetUtcNow();
        var issuedAt = succeededAt;
        var expiresAt = issuedAt + grace;
        var nonce = RandomNumberGenerator.GetBytes(OwnerSignedPayload.NonceLength);
        var deviceId = _signer.DeviceId;

        byte[] signature;
        try
        {
            var bytes = OwnerSignedPayload.Build(new OwnerPayload(
                MergeAction, approvalId, argsHash, intent.HeadSha, _humanId, deviceId, issuedAt, expiresAt, nonce));
            signature = await _signer.SignAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (signature.Length != ApprovalSigningPayload.SignatureLength)
            {
                return Refused(OwnerActionRefusal.SigningFailed);
            }
        }
        catch (OperationCanceledException)
        {
            return Refused(OwnerActionRefusal.SigningFailed);
        }
#pragma warning disable CA1031 // A signer failure means nothing was signed; the owner taps again for a fresh attempt.
        catch (Exception)
#pragma warning restore CA1031
        {
            return Refused(OwnerActionRefusal.SigningFailed);
        }

        if (_clock.GetUtcNow() >= succeededAt + grace)
        {
            return Refused(OwnerActionRefusal.BiometricExpired);
        }

        var payload = new PrOwnerPayload(MergeAction, approvalId, argsHash, intent.HeadSha, _humanId, deviceId, issuedAt, expiresAt, nonce);
        return new OwnerActionResult(null, new OwnerSignedAction(approvalId, intent.HeadSha, payload, signature));
    }

    /// <inheritdoc />
    public bool TrySpend(OwnerSignedAction approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        lock (_gate)
        {
            return _spent.Add(approval.ApprovalId);
        }
    }

    private static OwnerActionResult Refused(OwnerActionRefusal refusal) => new(refusal, null);

    private static bool IsWellFormed(OwnerMergeIntent intent) =>
        intent.Pr is { } pr
        && pr.InstallationId > 0
        && !string.IsNullOrWhiteSpace(pr.Owner)
        && !string.IsNullOrWhiteSpace(pr.Repo)
        && pr.Number > 0
        && _headShaRegex.IsMatch(intent.HeadSha ?? string.Empty)
        && !string.IsNullOrWhiteSpace(intent.CommitTitle);

    private static readonly System.Text.RegularExpressions.Regex _headShaRegex =
        new("^[0-9a-f]{40}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
