namespace Bazaar.Server.Portability;

/// <summary>
/// Export options the operator sets (doc 44 section 12, install scope). The defaults are the shipped values. The host reads them
/// from the settings registry and writes every change to the audit log.
/// </summary>
public sealed record PortabilityExportOptions
{
    /// <summary>
    /// <c>portability.export.include_derived</c>: write the derived views (the Constellation graph). Off by default. Derived files
    /// are always marked derived and never claimed as authoritative (doc 44 section 12).
    /// </summary>
    public bool IncludeDerived { get; init; }
}
