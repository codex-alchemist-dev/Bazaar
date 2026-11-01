using System.Security.Cryptography;

namespace Bazaar.Client.LocalInference.Models;

/// <summary>Signature and per-file integrity checks against a manifest (doc 31, Model packaging and delivery).</summary>
public static class ModelIntegrity
{
    private const int _bufferSize = 81920;

    /// <summary>
    /// A manifest with no signature passes only when the policy does not require one. A signature that is present must
    /// verify, whatever the policy says.
    /// </summary>
    public static void VerifySignature(ModelManifest manifest, ManifestPolicy policy, IManifestVerifier verifier)
    {
        if (string.IsNullOrWhiteSpace(manifest.Signature))
        {
            if (policy.RequireSignature)
            {
                throw new ModelIntegrityException(ModelIntegrityFailure.SignatureMissing, null,
                    $"Model {manifest.Model} {manifest.Version} is unsigned and the policy requires a signature.");
            }

            return;
        }

        if (!verifier.Verify(manifest, manifest.Signature))
        {
            throw new ModelIntegrityException(ModelIntegrityFailure.SignatureInvalid, null,
                $"Signature on model {manifest.Model} {manifest.Version} did not verify.");
        }
    }

    /// <summary>Checks that every file listed in the manifest is present in the directory with the listed length and SHA-256.</summary>
    public static async Task VerifyFilesAsync(ModelManifest manifest, string directory, CancellationToken ct)
    {
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            await VerifyFileAsync(file, directory, ct);
        }
    }

    /// <summary>
    /// Checks one file. The length is compared before hashing, and the hash is streamed so large models are not read
    /// into memory.
    /// </summary>
    public static async Task VerifyFileAsync(ManifestFile file, string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, file.Name);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ModelIntegrityException(ModelIntegrityFailure.Missing, file.Name, $"Model file '{file.Name}' is missing.");
        }

        if (info.Length != file.Bytes)
        {
            throw new ModelIntegrityException(ModelIntegrityFailure.LengthMismatch, file.Name,
                $"Model file '{file.Name}' is {info.Length} bytes; the manifest lists {file.Bytes}.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct);
        if (!string.Equals(Convert.ToHexStringLower(hash), file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModelIntegrityException(ModelIntegrityFailure.HashMismatch, file.Name,
                $"Model file '{file.Name}' does not match its SHA-256 in the manifest.");
        }
    }
}
