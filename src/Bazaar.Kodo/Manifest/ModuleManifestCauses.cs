namespace Bazaar.Kodo.Manifest;

/// <summary>Stable cause codes for a rejected manifest (doc 46 section 11.2). They are matched by tests, journals and the install report.</summary>
public static class ModuleManifestCauses
{
    public const string TooLarge = "manifest_too_large";
    public const string TooDeep = "manifest_too_deep";
    public const string DuplicateField = "manifest_duplicate_field";

    /// <summary>Not JSON, not an object, a wrong value type, or an unknown field.</summary>
    public const string Malformed = "manifest_malformed";

    public const string IdInvalid = "id_invalid";
    public const string NameInvalid = "name_invalid";
    public const string KindInvalid = "kind_invalid";
    public const string VersionInvalid = "version_invalid";
    public const string ContractUnsupported = "contract_unsupported";
    public const string DependencyInvalid = "dependency_invalid";
    public const string DependencyDuplicate = "dependency_duplicate";
    public const string DependencyConflict = "dependency_conflict";
    public const string HealthMissing = "health_missing";
    public const string HealthInvalid = "health_invalid";
    public const string ProbeInvalid = "probe_invalid";
    public const string ProbeDuplicate = "probe_duplicate";
    public const string ProbeUnknown = "probe_unknown";
    public const string RunbookPathInvalid = "runbook_path_invalid";
    public const string RunbookDuplicate = "runbook_duplicate";
    public const string PermissionInvalid = "permission_invalid";
    public const string PermissionDuplicate = "permission_duplicate";
    public const string TooManyEntries = "too_many_entries";
    public const string SignatureMissing = "signature_missing";
    public const string SignatureUnknownKey = "signature_unknown_key";
    public const string SignatureKeyOutsideValidity = "signature_key_outside_validity";
    public const string SignatureAlgMismatch = "signature_alg_mismatch";
    public const string SignatureUnsupportedAlgorithm = "signature_unsupported_algorithm";
    public const string SignatureInvalid = "signature_invalid";
}
