using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Tabula;

/// <summary>
/// Frozen operation vocabulary built from module sources (doc 81 section 8.1). A malformed kind, a duplicate kind or a
/// planned operation without a version fails construction, naming the module ids involved.
/// </summary>
public sealed partial class AuditOperationRegistry : IAuditOperationRegistry
{
    private readonly Dictionary<string, AuditOperation> _byKind = new(StringComparer.Ordinal);

    public AuditOperationRegistry(IEnumerable<IAuditOperationSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        foreach (IAuditOperationSource source in sources)
        {
            foreach (AuditOperation operation in source.Operations)
            {
                Add(source.ModuleId, operation);
            }
        }

        All = [.. _byKind.Values.OrderBy(o => o.Kind, StringComparer.Ordinal)];
        RegistryHash = ComputeHash(All);
    }

    public IReadOnlyList<AuditOperation> All { get; }

    public string RegistryHash { get; }

    public bool TryGet(string kind, out AuditOperation operation)
    {
        ArgumentNullException.ThrowIfNull(kind);
        return _byKind.TryGetValue(kind, out operation!);
    }

    private void Add(string sourceModuleId, AuditOperation operation)
    {
        if (!KindPattern().IsMatch(operation.Kind))
        {
            throw new ArgumentException($"Module '{sourceModuleId}': kind '{operation.Kind}' does not match module.noun.verb.");
        }

        if (!string.Equals(operation.ModuleId, sourceModuleId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Module '{sourceModuleId}' registered '{operation.Kind}' as module '{operation.ModuleId}'.");
        }

        if (operation.Status == OperationStatus.Planned && string.IsNullOrWhiteSpace(operation.PlannedUntilVersion))
        {
            throw new ArgumentException($"Module '{sourceModuleId}': planned operation '{operation.Kind}' needs PlannedUntilVersion.");
        }

        if (!_byKind.TryAdd(operation.Kind, operation))
        {
            throw new InvalidOperationException(
                $"Audit kind '{operation.Kind}' is registered by both '{_byKind[operation.Kind].ModuleId}' and '{sourceModuleId}'.");
        }
    }

    // Descriptors are joined with unit and record separators so field text cannot forge a boundary; operations are sorted by kind.
    private static string ComputeHash(IReadOnlyList<AuditOperation> sorted)
    {
        StringBuilder text = new();
        foreach (AuditOperation o in sorted)
        {
            text.Append(o.Kind).Append('\u001f')
                .Append(o.ModuleId).Append('\u001f')
                .Append(o.Class).Append('\u001f')
                .Append(string.Join(',', o.RequiredPayloadFields.Order(StringComparer.Ordinal))).Append('\u001f')
                .Append(o.Privileged ? '1' : '0').Append('\u001f')
                .Append(o.RetentionExempt ? '1' : '0').Append('\u001f')
                .Append(o.Status).Append('\u001f')
                .Append(o.PlannedUntilVersion).Append('\u001f')
                .Append(o.DocRef).Append('\u001e');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*){1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex KindPattern();
}
