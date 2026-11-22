namespace Bazaar.Kodo.Manifest;

/// <summary>The outcome of reading a module manifest (doc 46 section 11).</summary>
public enum ModuleLoadOutcome
{
    /// <summary>Valid, signed, and every declared permission is already approved by the owner.</summary>
    Loadable,

    /// <summary>Valid and signed, but some declared permissions wait for the owner. Nothing is granted until then.</summary>
    NeedsOwnerApproval,

    /// <summary>Not loadable. <see cref="ModuleLoadDecision.Problems"/> says why.</summary>
    Rejected,
}

/// <summary>
/// The result of <see cref="ModuleManifestLoader.Evaluate"/>. Only data: the caller decides what to do, and nothing here grants a permission.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="ModuleId">The module id when the manifest decoded and it is well formed, otherwise null.</param>
/// <param name="Problems">Every finding, in a fixed order. Empty unless rejected.</param>
/// <param name="PermissionsNeedingApproval">Declared permissions the owner has not approved, sorted. Empty when rejected.</param>
/// <param name="ManifestDigest">SHA-256 (lowercase hex) of the canonical bytes, so an approval can be bound to this exact manifest. Null when decoding failed.</param>
public sealed record ModuleLoadDecision(
    ModuleLoadOutcome Outcome,
    string? ModuleId,
    IReadOnlyList<ModuleManifestProblem> Problems,
    IReadOnlyList<string> PermissionsNeedingApproval,
    string? ManifestDigest)
{
    /// <summary>The distinct cause codes of <see cref="Problems"/>, in order of first appearance.</summary>
    public IReadOnlyList<string> Causes => [.. Problems.Select(problem => problem.Cause).Distinct(StringComparer.Ordinal)];
}
