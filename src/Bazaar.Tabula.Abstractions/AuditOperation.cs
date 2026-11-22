namespace Bazaar.Tabula;

/// <summary>Whether an operation is written today or announced for a later version.</summary>
public enum OperationStatus
{
    Active,
    Planned,
}

/// <summary>
/// A registered audit operation (doc 81 section 8.1). <see cref="Kind"/> is <c>module.noun.verb</c>, matching
/// <c>^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,3}$</c>.
/// </summary>
public sealed record AuditOperation(
    string Kind,
    string ModuleId,
    AuditClass Class,
    IReadOnlyList<string> RequiredPayloadFields,
    bool Privileged,
    bool RetentionExempt,
    OperationStatus Status,
    string? PlannedUntilVersion,
    string DocRef);

/// <summary>A module's contribution to the vocabulary.</summary>
public interface IAuditOperationSource
{
    string ModuleId { get; }

    IEnumerable<AuditOperation> Operations { get; }
}

/// <summary>The frozen set of operations in force.</summary>
public interface IAuditOperationRegistry
{
    IReadOnlyList<AuditOperation> All { get; }

    bool TryGet(string kind, out AuditOperation operation);

    /// <summary>SHA-256 (lowercase hex) over the sorted operation descriptors.</summary>
    string RegistryHash { get; }
}
