using Bazaar.Kodo.Runtime;

namespace Bazaar.Kodo.Manifest;

/// <summary>
/// Turns manifest bytes into a load decision (doc 46 section 11): strict decode, the structural rules, then the signature against
/// the trust root through the existing runtime verifier. Pure: the caller passes the bytes and the time. Permissions are only
/// reported for the owner, never granted.
/// </summary>
public sealed class ModuleManifestLoader
{
    private readonly RuntimeTrustRoot _trust;
    private readonly RuntimeSignatureChecker _checker;
    private readonly ModuleProbeCatalogue _catalogue;

    /// <summary>Creates a loader that verifies RSA PKCS#1 SHA-256 signatures, like the runtime manifest.</summary>
    public ModuleManifestLoader(RuntimeTrustRoot trust, ModuleProbeCatalogue catalogue)
        : this(trust, catalogue, [new RsaPkcs1Sha256Verifier()])
    {
    }

    /// <exception cref="ArgumentException">Two verifiers claim the same algorithm name.</exception>
    public ModuleManifestLoader(RuntimeTrustRoot trust, ModuleProbeCatalogue catalogue, IEnumerable<IRuntimeSignatureVerifier> verifiers)
    {
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _checker = new RuntimeSignatureChecker(trust, verifiers);
    }

    /// <summary>
    /// Decides whether the module may load. <paramref name="approvedPermissions"/> are the permissions the owner already approved for this
    /// module (null means none), so a manifest that asks for nothing new is loadable and one that asks for more is not.
    /// </summary>
    public ModuleLoadDecision Evaluate(ReadOnlySpan<byte> manifestBytes, DateTimeOffset now, IReadOnlySet<string>? approvedPermissions = null)
    {
        ModuleManifestDecoding decoded = ModuleManifestCodec.Decode(manifestBytes);
        if (decoded.Manifest is null)
        {
            return Rejected(null, [new ModuleManifestProblem(decoded.Cause ?? ModuleManifestCauses.Malformed, "manifest")], null);
        }

        ModuleManifest manifest = decoded.Manifest;
        byte[] canonical = ModuleManifestCodec.CanonicalBytes(manifest);
        string digest = RuntimeManifestCodec.Sha256Hex(canonical);

        List<ModuleManifestProblem> problems = [.. ModuleManifestValidator.Validate(manifest, _catalogue)];
        ModuleManifestProblem? signatureProblem = CheckSignature(manifest.Signature, canonical, now);
        if (signatureProblem is not null)
        {
            problems.Add(signatureProblem);
        }

        if (problems.Count > 0)
        {
            string? knownId = problems.Any(problem => problem.Cause == ModuleManifestCauses.IdInvalid) ? null : manifest.Id;
            return Rejected(knownId, problems, digest);
        }

        string[] pending = [.. (manifest.Permissions ?? [])
            .Where(permission => approvedPermissions is null || !approvedPermissions.Contains(permission))
            .Order(StringComparer.Ordinal)];
        return new ModuleLoadDecision(
            pending.Length == 0 ? ModuleLoadOutcome.Loadable : ModuleLoadOutcome.NeedsOwnerApproval,
            manifest.Id,
            [],
            pending,
            digest);
    }

    private static ModuleLoadDecision Rejected(string? moduleId, IReadOnlyList<ModuleManifestProblem> problems, string? digest) =>
        new(ModuleLoadOutcome.Rejected, moduleId, problems, [], digest);

    private ModuleManifestProblem? CheckSignature(ModuleManifestSignature? signature, byte[] canonical, DateTimeOffset now)
    {
        const string field = "signature";
        if (signature is null || string.IsNullOrEmpty(signature.Value))
        {
            return new(ModuleManifestCauses.SignatureMissing, field);
        }

        // The manifest may not choose its own algorithm: the pinned key decides, and the block must agree.
        TrustedKey? key = _trust.Find(signature.KeyId);
        if (key is not null && !string.Equals(signature.Alg, key.Algorithm, StringComparison.Ordinal))
        {
            return new(ModuleManifestCauses.SignatureAlgMismatch, field);
        }

        string cause = _checker.Check(canonical, signature.KeyId, signature.Value, now) switch
        {
            RuntimeSignatureStatus.Valid => string.Empty,
            RuntimeSignatureStatus.NoSignature => ModuleManifestCauses.SignatureMissing,
            RuntimeSignatureStatus.UnknownKey => ModuleManifestCauses.SignatureUnknownKey,
            RuntimeSignatureStatus.KeyOutsideValidity => ModuleManifestCauses.SignatureKeyOutsideValidity,
            RuntimeSignatureStatus.UnsupportedAlgorithm => ModuleManifestCauses.SignatureUnsupportedAlgorithm,
            _ => ModuleManifestCauses.SignatureInvalid,
        };
        return cause.Length == 0 ? null : new(cause, field);
    }
}
