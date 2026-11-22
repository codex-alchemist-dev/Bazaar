using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Client.Approvals;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Merge;

/// <summary>
/// <see cref="IOwnerMergeApprover"/> over the device signer. It builds the shared <see cref="OwnerPayload"/> for a
/// <see cref="GitMergeArgsHash.Action"/> approval, asks for a fresh biometric check on that exact merge, signs
/// <see cref="OwnerSignedPayload.Build"/> with the device key and returns the wire body. The payload, hash and signing bytes are the
/// same types the server gate rebuilds and verifies, so the two cannot disagree. Nothing is logged or kept; the signature lives in
/// the returned DTO only.
/// </summary>
public sealed class DeviceOwnerMergeApprover : IOwnerMergeApprover
{
    /// <summary>Shipped lifetime of one approval. The server accepts up to 5 minutes by default and only ever tightens that.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private const int _maxTitleLength = 200;

    // The server's shipped ceiling (doc 32 section 5a); a longer lifetime is always refused, so it is refused here too.
    private static readonly TimeSpan _lifetimeCeiling = TimeSpan.FromMinutes(15);

    private readonly IBiometricPrompt _biometric;
    private readonly IDeviceApprovalSigner _signer;
    private readonly IOwnerMergeIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _lifetime;

    public DeviceOwnerMergeApprover(
        IBiometricPrompt biometric,
        IDeviceApprovalSigner signer,
        IOwnerMergeIdentity identity,
        TimeProvider? clock = null,
        TimeSpan? lifetime = null)
    {
        _biometric = biometric ?? throw new ArgumentNullException(nameof(biometric));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? TimeProvider.System;
        _lifetime = lifetime ?? DefaultLifetime;
        if (_lifetime <= TimeSpan.Zero || _lifetime > _lifetimeCeiling)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "The lifetime must be positive and within the server ceiling.");
        }
    }

    /// <summary>The squash commit title for a PR: its title and number on one line, capped.</summary>
    public static string CommitTitleFor(string prTitle, long number)
    {
        ArgumentNullException.ThrowIfNull(prTitle);
        var oneLine = string.Join(' ', prTitle.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (oneLine.Length > _maxTitleLength)
        {
            oneLine = oneLine[.._maxTitleLength];
        }

        return $"{oneLine} (#{number})";
    }

    public async Task<MergeApprovalResult> ApproveAsync(MergeApprovalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headSha = request.HeadSha.ToLowerInvariant();
        var signOff = _identity.SignOff;
        var argsHash = GitMergeArgsHash.Compute(request.Provider, request.Repo, request.Number, headSha, request.CommitTitle, signOff);
        var approvalId = Guid.NewGuid();
        var now = _clock.GetUtcNow();
        var payload = new OwnerPayload(
            GitMergeArgsHash.Action, approvalId, argsHash, headSha, _identity.HumanId, _signer.DeviceId,
            now, now + _lifetime, RandomNumberGenerator.GetBytes(OwnerSignedPayload.NonceLength));

        byte[] message;
        try
        {
            message = OwnerSignedPayload.Build(payload);
        }
        catch (ArgumentException)
        {
            return new MergeApprovalResult(MergeApprovalOutcome.Failed, null);
        }

        BiometricOutcome outcome;
        try
        {
            var prompt = new BiometricRequest(
                approvalId,
                Convert.ToHexStringLower(argsHash),
                "Approve merge",
                $"Squash merge {request.Repo} #{request.Number} at {headSha[..7]}");
            outcome = await _biometric.AuthenticateAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new MergeApprovalResult(MergeApprovalOutcome.Cancelled, null);
        }

        switch (outcome)
        {
            case BiometricOutcome.Succeeded:
                break;
            case BiometricOutcome.Unavailable:
                return new MergeApprovalResult(MergeApprovalOutcome.Unavailable, null);
            default:
                return new MergeApprovalResult(MergeApprovalOutcome.Cancelled, null);
        }

        byte[] signature;
        try
        {
            signature = await _signer.SignAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new MergeApprovalResult(MergeApprovalOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            return new MergeApprovalResult(MergeApprovalOutcome.Failed, null);
        }

        if (signature is null || signature.Length != OwnerSignatureCrypto.SignatureLength)
        {
            return new MergeApprovalResult(MergeApprovalOutcome.Failed, null);
        }

        var approval = new GitOwnerApprovalDto(
            payload.Action, payload.ApprovalId, Convert.ToBase64String(argsHash), headSha, payload.HumanId, payload.DeviceId,
            payload.IssuedAt, payload.ExpiresAt, Convert.ToBase64String(payload.Nonce), Convert.ToBase64String(signature));
        return new MergeApprovalResult(MergeApprovalOutcome.Approved, new GitMergeRequestDto(headSha, request.CommitTitle, signOff, approval));
    }
}
