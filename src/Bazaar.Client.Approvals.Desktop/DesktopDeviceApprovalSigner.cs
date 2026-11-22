using System.Text.RegularExpressions;
using Bazaar.DeviceApproval;

namespace Bazaar.Client.Approvals;

/// <summary>
/// <see cref="IDeviceApprovalSigner"/> for a desktop head, over a platform <see cref="IApprovalSigner"/> (doc 32 section 5a.1:
/// on Windows a TPM key). It accepts an already enrolled key id (enrolment is doc 32 Question 4). A key that is not TPM-backed
/// is refused before anything is signed, and a platform refusal is rethrown as <see cref="InvalidOperationException"/> so the
/// merge approver maps it to a failed approval. The message names the refusal reason and never carries payload or signature bytes.
/// </summary>
public sealed partial class DesktopDeviceApprovalSigner : IDeviceApprovalSigner
{
    private const int _signatureLength = 64;

    private readonly IApprovalSigner _signer;

    /// <param name="deviceId">The enrolled key id, as the server knows it (<c>^[A-Za-z0-9._-]{1,128}$</c>).</param>
    /// <param name="signer">The platform signer holding the key.</param>
    public DesktopDeviceApprovalSigner(string deviceId, IApprovalSigner signer)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(signer);
        if (!DeviceIdPattern().IsMatch(deviceId))
        {
            throw new ArgumentException("The device id must match ^[A-Za-z0-9._-]{1,128}$.", nameof(deviceId));
        }

        DeviceId = deviceId;
        _signer = signer;
    }

    /// <inheritdoc />
    public string DeviceId { get; }

    /// <inheritdoc />
    public async ValueTask<byte[]> SignAsync(ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_signer.Custody != ApprovalKeyCustody.Tpm)
        {
            throw new InvalidOperationException("The approval key is not TPM-backed, so it cannot approve a merge.");
        }

        ApprovalSignature result;
        try
        {
            result = await _signer.SignAsync(canonicalPayload, cancellationToken).ConfigureAwait(false);
        }
        catch (ApprovalRefusedException ex)
        {
            throw new InvalidOperationException($"The approval key refused to sign ({ex.Reason}).");
        }

        if (result.Custody != ApprovalKeyCustody.Tpm || result.Signature is not { Length: _signatureLength })
        {
            throw new InvalidOperationException("The approval key returned an unusable signature.");
        }

        return result.Signature;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex DeviceIdPattern();
}
