using System.Security.Cryptography;
using Bazaar.Install;
using Bazaar.Install.Abstractions;
using Bazaar.Install.Trust;
using Bazaar.Server.Tests.Modules.Trust;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Tests.Modules.Install;

/// <summary>A stand-in owner device key: it signs approval forms and verifies them, like the real signer and verifier will.</summary>
internal sealed class OwnerApprovalTestKey : IOwnerApprovalSigner, IOwnerApprovalVerifier, IDisposable
{
    public const string KeyId = "owner-device-1";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Task<OwnerApprovalSignature?> SignAsync(ReadOnlyMemory<byte> approvalForm, string ownerActor, CancellationToken ct = default) =>
        Task.FromResult<OwnerApprovalSignature?>(new OwnerApprovalSignature(KeyId, _key.SignData(approvalForm.Span, HashAlgorithmName.SHA256)));

    public bool Verify(ReadOnlySpan<byte> approvalForm, OwnerApprovalSignature signature) =>
        signature.SignerKeyId == KeyId && _key.VerifyData(approvalForm, signature.Signature, HashAlgorithmName.SHA256);

    /// <summary>Records a real, owner-signed approval for <paramref name="package"/> in <paramref name="store"/> and returns the row.</summary>
    public async Task<ModulePermissionApprovalRecord> ApproveAsync(
        IModuleSignatureVerifier verifier, InstallAuditLog store, ModulePackage package, params string[] permissions)
    {
        var service = new ModuleApprovalService(verifier, store, this, new FakeTimeProvider(ModuleSigningTestHelper.Now));
        var result = await service.ApproveAsync(package, permissions, "owner", TestContext.Current.CancellationToken);
        return Assert.IsType<ModuleApprovalResult.Recorded>(result).Approval;
    }

    public void Dispose() => _key.Dispose();
}
