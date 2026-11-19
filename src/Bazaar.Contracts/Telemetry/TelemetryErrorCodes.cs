using System.Text.RegularExpressions;

namespace Bazaar.Contracts.Telemetry;

/// <summary>One registered error code and what it means (doc 43, section 4.1).</summary>
/// <param name="Code">The code, <c>BZ-&lt;AREA&gt;-&lt;NNNN&gt;</c>.</param>
/// <param name="Description">A short, plain description shown in the crash report dialog.</param>
public sealed record TelemetryErrorCodeEntry(string Code, string Description);

/// <summary>
/// The registry of stable error codes that clients and the server put on telemetry and crash reports
/// (doc 43, section 4.1). A code is never reused for a different meaning. Add an entry when a module
/// starts reporting a new failure; never edit the meaning of an entry that has shipped.
/// </summary>
public static partial class TelemetryErrorCodes
{
    /// <summary>The form of a code: <c>BZ-</c>, an area of 2 to 8 capitals, a dash and four digits.</summary>
    public const string Shape = @"^BZ-(?<area>[A-Z]{2,8})-(?<number>\d{4})\z";

    private static readonly IReadOnlyList<TelemetryErrorCodeEntry> _registry =
    [
    ];

    /// <summary>Every registered code, in registry order.</summary>
    public static IReadOnlyList<TelemetryErrorCodeEntry> Registry => _registry;

    /// <summary>True when the text has the code form <c>BZ-&lt;AREA&gt;-&lt;NNNN&gt;</c>.</summary>
    public static bool IsWellFormed(string? code) => code is not null && ShapeRegex().IsMatch(code);

    /// <summary>Returns the description for a registered code, or <c>null</c> when the code is not registered.</summary>
    public static string? DescriptionOf(string? code)
    {
        if (code is null)
        {
            return null;
        }

        foreach (var entry in _registry)
        {
            if (string.Equals(entry.Code, code, StringComparison.Ordinal))
            {
                return entry.Description;
            }
        }

        return null;
    }

    /// <summary>
    /// Checks a set of entries and returns one message per problem: a malformed code, a code listed twice,
    /// or a missing description. An empty result means the set is valid. The contract test runs this over
    /// <see cref="Registry"/>.
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<TelemetryErrorCodeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!IsWellFormed(entry.Code))
            {
                problems.Add($"Malformed code '{entry.Code}'; expected BZ-<AREA>-<NNNN>.");
            }

            if (!seen.Add(entry.Code))
            {
                problems.Add($"Duplicate code '{entry.Code}'.");
            }

            if (string.IsNullOrWhiteSpace(entry.Description))
            {
                problems.Add($"Code '{entry.Code}' has no description.");
            }
        }

        return problems;
    }

    [GeneratedRegex(Shape, RegexOptions.CultureInvariant)]
    private static partial Regex ShapeRegex();
}
