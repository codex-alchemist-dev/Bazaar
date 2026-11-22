using System.Security.Cryptography;
using Bazaar.Approvals;
using Bazaar.Server.Biometric;

namespace Bazaar.Server.DeviceEnrolment.Tests;

internal sealed class MemoryStore : IEnrolmentStore
{
    private readonly Dictionary<string, (Guid Human, Guid Device, DateTimeOffset Expires, bool Spent)> _challenges = [];
    private readonly Dictionary<string, (Guid Human, DateTimeOffset Expires, bool Spent)> _codes = [];
    public HashSet<(Guid Device, Guid Human, DeviceClass Class)> Devices { get; } = [];
    public List<EnrolmentCommit> Commits { get; } = [];
    public int Writes => Commits.Count;

    public Task SaveChallengeAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        _challenges[Convert.ToHexString(nonce)] = (humanId, deviceId, expiresAt, false);
        return Task.CompletedTask;
    }

    public Task SaveCodeAsync(byte[] codeHash, Guid humanId, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        _codes[Convert.ToHexString(codeHash)] = (humanId, expiresAt, false);
        return Task.CompletedTask;
    }

    public Task<bool> IsChallengeLiveAsync(byte[] nonce, Guid humanId, Guid deviceId, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(_challenges.TryGetValue(Convert.ToHexString(nonce), out var c) && !c.Spent && c.Human == humanId && c.Device == deviceId && c.Expires > now);

    public Task<bool> IsCodeLiveAsync(byte[] codeHash, Guid humanId, DateTimeOffset now, CancellationToken ct = default) =>
        Task.FromResult(_codes.TryGetValue(Convert.ToHexString(codeHash), out var c) && !c.Spent && c.Human == humanId && c.Expires > now);

    public Task<bool> DeviceMatchesAsync(Guid deviceId, Guid humanId, DeviceClass deviceClass, CancellationToken ct = default) =>
        Task.FromResult(Devices.Contains((deviceId, humanId, deviceClass)));

    public Task<EnrolmentCommitResult> CommitAsync(EnrolmentCommit commit, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(commit.Nonce);
        if (!_challenges.TryGetValue(key, out var ch) || ch.Spent)
        {
            return Task.FromResult(EnrolmentCommitResult.ChallengeSpent);
        }

        if (commit.ConsoleCodeHash is not null)
        {
            var ck = Convert.ToHexString(commit.ConsoleCodeHash);
            if (!_codes.TryGetValue(ck, out var code) || code.Spent)
            {
                return Task.FromResult(EnrolmentCommitResult.CodeSpent);
            }
        }

        if (Commits.Any(c => c.DeviceId == commit.DeviceId))
        {
            return Task.FromResult(EnrolmentCommitResult.AlreadyEnrolled);
        }

        _challenges[key] = ch with { Spent = true };
        if (commit.ConsoleCodeHash is not null)
        {
            var ck = Convert.ToHexString(commit.ConsoleCodeHash);
            _codes[ck] = _codes[ck] with { Spent = true };
        }

        Commits.Add(commit);
        return Task.FromResult(EnrolmentCommitResult.Committed);
    }
}

internal sealed class MemoryAudit : IEnrolmentAudit
{
    public List<EnrolmentVerdict> Refusals { get; } = [];

    public Task RefusedAsync(Guid humanId, Guid deviceId, EnrolmentVerdict verdict, DateTimeOffset at, CancellationToken ct = default)
    {
        Refusals.Add(verdict);
        return Task.CompletedTask;
    }
}

internal sealed class FakeKeyAttestor(ApprovalKeyAttestationResult result) : IApprovalKeyAttestor
{
    public Task<ApprovalKeyAttestationResult> VerifyAsync(DeviceClass deviceClass, byte[] spki, IReadOnlyList<byte[]> attestation, CancellationToken ct = default) =>
        Task.FromResult(result);
}

internal sealed class FakeCredentialAttestor(CredentialRegistration result) : ICredentialAttestor
{
    public byte[]? LastChallenge { get; private set; }

    public Task<CredentialRegistration> VerifyAsync(byte[] credentialId, byte[] attestation, byte[] clientDataJson, byte[] expectedChallenge, CancellationToken ct = default)
    {
        LastChallenge = expectedChallenge;
        return Task.FromResult(result);
    }
}

internal sealed class FakeOwnerSignature(OwnerSignatureVerdict verdict) : IOwnerSignatureVerifier
{
    public int Calls { get; private set; }

    public Task<OwnerSignatureVerdict> VerifyAsync(OwnerPayload payload, byte[] signature, DateTimeOffset now, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(verdict);
    }
}

internal sealed class FakeBiometric(BiometricVerdict verdict) : IBiometricAssertionVerifier
{
    public bool? LastHighRisk { get; private set; }

    public Task<BiometricVerdict> VerifyAsync(BiometricAssertion assertion, OwnerPayload payload, bool highRisk, DateTimeOffset now, CancellationToken ct = default)
    {
        LastHighRisk = highRisk;
        return Task.FromResult(verdict);
    }
}

internal static class Keys
{
    public static byte[] P256() => ECDsa.Create(ECCurve.NamedCurves.nistP256).ExportSubjectPublicKeyInfo();

    public static byte[] P384() => ECDsa.Create(ECCurve.NamedCurves.nistP384).ExportSubjectPublicKeyInfo();
}
