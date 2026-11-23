using System.Text.Json.Serialization;

namespace Bazaar.Kodo.Manifest;

/// <summary>The module's own dependency declaration (doc 46 section 10.1). Missing lists mean none.</summary>
/// <param name="Hard">Module ids whose red readiness makes this module not ready.</param>
/// <param name="Soft">Module ids whose red readiness makes this module degraded.</param>
public sealed record ModuleManifestDependencies(IReadOnlyList<string>? Hard, IReadOnlyList<string>? Soft);

/// <summary>The self-test part of the health block. Budgets are in milliseconds.</summary>
/// <param name="QuickBudgetMs">Budget of a quick, read-only self-test.</param>
/// <param name="FullBudgetMs">Budget of a full, sandboxed self-test.</param>
/// <param name="Scheduled">How often the full self-test runs: off, hourly, daily or weekly.</param>
public sealed record ModuleManifestSelfTest(int? QuickBudgetMs, int? FullBudgetMs, string? Scheduled);

/// <summary>The mandatory health block (doc 46 sections 10 and 11). A manifest without it does not load.</summary>
/// <param name="Liveness">Who answers liveness: <c>builtin</c> or <c>module</c>.</param>
/// <param name="Readiness">Who answers readiness: <c>builtin</c> or <c>module</c>.</param>
/// <param name="SelfTest">The self-test budgets and schedule.</param>
public sealed record ModuleManifestHealth(string? Liveness, string? Readiness, ModuleManifestSelfTest? SelfTest);

/// <summary>The detached signature block. The algorithm is checked against the trusted key's, never chosen by the manifest.</summary>
/// <param name="KeyId">The trust root key id.</param>
/// <param name="Alg">The algorithm name, which must equal the trusted key's algorithm.</param>
/// <param name="Value">The base64 signature over the canonical bytes of the manifest without this block.</param>
public sealed record ModuleManifestSignature(string? KeyId, string? Alg, string? Value);

/// <summary>
/// A module manifest as written in the signed package (doc 46 section 11). Members are nullable so that a missing field is a
/// validation cause, not a parse failure. The canonical bytes leave out <see cref="Signature"/>, and that is the signed message.
/// </summary>
public sealed record ModuleManifest(
    string? Id,
    string? Name,
    string? Kind,
    string? Version,
    [property: JsonPropertyName("kodo.contract")] int? KodoContract,
    ModuleManifestDependencies? Dependencies,
    ModuleManifestHealth? Health,
    IReadOnlyList<string>? Probes,
    IReadOnlyList<string>? Runbooks,
    IReadOnlyList<string>? Permissions,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ModuleManifestSignature? Signature);
