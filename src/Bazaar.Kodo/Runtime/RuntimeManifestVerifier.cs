namespace Bazaar.Kodo.Runtime;

/// <summary>
/// Verifies a signed slot manifest and the slot's files (doc 46 section 6.1). Pure: the caller reads the bytes and passes them in.
/// A missing, malformed, unsigned, or wrongly signed manifest never verifies, whatever the files say. Every file that
/// disagrees is listed, not just the first.
/// </summary>
public sealed class RuntimeManifestVerifier
{
    private readonly RuntimeSignatureChecker _signatures;

    /// <summary>Creates a verifier that checks signatures with <paramref name="signatures"/>.</summary>
    public RuntimeManifestVerifier(RuntimeSignatureChecker signatures)
    {
        _signatures = signatures ?? throw new ArgumentNullException(nameof(signatures));
    }

    /// <summary>
    /// Verifies <paramref name="envelope"/> (null when the file is missing) against the slot files. <paramref name="slotFiles"/> maps
    /// each slot-relative path (ordinal, '/' separators) to its bytes and must include every file in the slot, so extra files are seen.
    /// </summary>
    public RuntimeVerificationResult Verify(
        byte[]? envelope,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> slotFiles,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(slotFiles);

        if (envelope is null)
        {
            return new RuntimeVerificationResult(RuntimeManifestStatus.Missing, []);
        }

        SignedRuntimeManifest? signed = RuntimeManifestCodec.ParseEnvelope(envelope);
        if (signed?.Manifest is not { } manifest || !RuntimeManifestRules.IsWellFormed(manifest))
        {
            return new RuntimeVerificationResult(RuntimeManifestStatus.Malformed, []);
        }

        RuntimeSignatureStatus signature = _signatures.Check(
            RuntimeManifestCodec.CanonicalizeManifest(manifest),
            signed.KeyId,
            signed.Signature,
            now);
        List<RuntimeFileProblem> problems = FindFileProblems(manifest, slotFiles);

        if (signature != RuntimeSignatureStatus.Valid)
        {
            return new RuntimeVerificationResult(ToManifestStatus(signature), problems);
        }

        return new RuntimeVerificationResult(
            problems.Count == 0 ? RuntimeManifestStatus.Verified : RuntimeManifestStatus.FileMismatch,
            problems);
    }

    /// <summary>Maps a failed signature check to the manifest status the caller sees.</summary>
    internal static RuntimeManifestStatus ToManifestStatus(RuntimeSignatureStatus signature) => signature switch
    {
        RuntimeSignatureStatus.Valid => RuntimeManifestStatus.Verified,
        RuntimeSignatureStatus.NoSignature => RuntimeManifestStatus.NoSignature,
        RuntimeSignatureStatus.UnknownKey => RuntimeManifestStatus.UnknownKey,
        RuntimeSignatureStatus.KeyOutsideValidity => RuntimeManifestStatus.KeyOutsideValidity,
        RuntimeSignatureStatus.UnsupportedAlgorithm => RuntimeManifestStatus.UnsupportedAlgorithm,
        _ => RuntimeManifestStatus.BadSignature,
    };

    private static List<RuntimeFileProblem> FindFileProblems(
        RuntimeManifest manifest,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> slotFiles)
    {
        List<RuntimeFileProblem> problems = [];
        HashSet<string> listed = new(StringComparer.Ordinal);

        foreach (RuntimeFileEntry entry in manifest.Files)
        {
            listed.Add(entry.Path);

            if (!slotFiles.TryGetValue(entry.Path, out ReadOnlyMemory<byte> bytes))
            {
                problems.Add(new RuntimeFileProblem(entry.Path, RuntimeFileProblemKind.Missing, entry.Sha256, null));
                continue;
            }

            if (bytes.Length != entry.Size)
            {
                problems.Add(new RuntimeFileProblem(entry.Path, RuntimeFileProblemKind.SizeMismatch, entry.Sha256, null));
                continue;
            }

            string actual = RuntimeManifestCodec.Sha256Hex(bytes.Span);
            if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            {
                problems.Add(new RuntimeFileProblem(entry.Path, RuntimeFileProblemKind.HashMismatch, entry.Sha256, actual));
            }
        }

        foreach (string path in slotFiles.Keys.Order(StringComparer.Ordinal))
        {
            if (!listed.Contains(path))
            {
                problems.Add(new RuntimeFileProblem(path, RuntimeFileProblemKind.Unlisted, null, null));
            }
        }

        return problems;
    }
}
