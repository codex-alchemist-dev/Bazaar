using System.Text.RegularExpressions;
using Bazaar.Roles.Abstractions;

namespace Bazaar.Server.AuditView;

/// <summary>
/// Operator settings for the audit viewer (doc 48 section 5.4). The shipped defaults are the safe ones. Settings may narrow
/// what a role sees; the owner floor and the agent refusal are not settings.
/// </summary>
public sealed partial record AuditViewOptions
{
    /// <summary>Category name to the event-kind prefixes it covers. A kind with no matching prefix is in <c>other</c>.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> CategoryMap { get; init; } = DefaultCategoryMap;

    /// <summary>The capability that lets a human see every category. The owner role holds it at instance scope.</summary>
    public string OwnerCapability { get; init; } = Capabilities.Approve.Value;

    /// <summary>The capability prefix for one category: a role that grants <c>audit.view.security</c> sees the security category.</summary>
    public string CategoryCapabilityPrefix { get; init; } = "audit.view.";

    public int DefaultPageSize { get; init; } = 100;

    public int MaxPageSize { get; init; } = 500;

    /// <summary>Most source rows read for one page, so a viewer with a narrow scope cannot make the server scan the whole log.</summary>
    public int MaxScanRows { get; init; } = 5000;

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> DefaultCategoryMap { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [AuditViewCategories.Security] =
            [
                "security.", "auth.", "approval.", "lease.", "module.permissions.", "audit.", "recovery.",
            ],
        };

    public void Validate()
    {
        if (DefaultPageSize < 1 || MaxPageSize < DefaultPageSize || MaxScanRows < MaxPageSize)
            throw new ArgumentException("Page sizes must satisfy 1 <= default <= max <= max scan.");
        if (!ValidCapability().IsMatch(OwnerCapability))
            throw new ArgumentException("OwnerCapability is not a valid capability name.");
        foreach (var (name, prefixes) in CategoryMap)
        {
            if (name == AuditViewCategories.Other || !CategoryName().IsMatch(name))
                throw new ArgumentException($"'{name}' is not a usable category name.");
            if (prefixes.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException($"Category '{name}' has an empty prefix.");
        }
    }

    /// <summary>The category of an event kind: the first map entry (in name order) with a matching prefix, else <c>other</c>.</summary>
    public string CategoryOf(string kind)
    {
        foreach (var (name, prefixes) in CategoryMap.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var prefix in prefixes)
            {
                if (kind.StartsWith(prefix, StringComparison.Ordinal))
                    return name;
            }
        }

        return AuditViewCategories.Other;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidCapability();

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryName();
}
