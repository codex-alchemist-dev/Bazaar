namespace Bazaar.Server.ReadAudit.Npgsql;

/// <summary>Settings of <see cref="AuditedDbDataSource"/>. The defaults record everything, so no read goes unrecorded.</summary>
public sealed class AuditedDataSourceOptions
{
    /// <summary>
    /// When true (the default), a command that did not declare what it reads is recorded under <see cref="UntaggedObjectType"/>.
    /// When false it is skipped, so an operator can run a partly migrated module without noise. Declare with
    /// <see cref="AuditedCommandExtensions.DeclareRead"/>.
    /// </summary>
    public bool RecordUntaggedReads { get; set; } = true;

    /// <summary>Object type label for a read that declared none. Must be a valid label (lowercase letters, digits and _ . : / -).</summary>
    public string UntaggedObjectType { get; set; } = "unclassified";
}
