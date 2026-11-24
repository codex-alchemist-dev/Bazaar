using System.Text.RegularExpressions;

namespace Bazaar.Kodo.Chaos;

/// <summary>What kind of thing a target identifier names, decided by its shape alone (doc 46 section 17.7).</summary>
public enum ChaosTargetClass
{
    /// <summary>A sandbox copy: <c>sandbox:</c> followed by a lower-case token.</summary>
    Sandbox,

    /// <summary>Names production: a production prefix or a known production id.</summary>
    Production,

    /// <summary>Anything else, including an empty or malformed identifier. Refused like production.</summary>
    Unknown,
}

/// <summary>Classifies a target identifier. Only <see cref="ChaosTargetClass.Sandbox"/> may be attacked; the rest is refused.</summary>
public static partial class ChaosTargetClassifier
{
    /// <summary>The prefix every sandbox identifier carries.</summary>
    public const string SandboxPrefix = "sandbox:";

    /// <summary>Classifies an identifier. The check is on the identifier class, never on a lookup that could fail open.</summary>
    public static ChaosTargetClass Classify(string? identifier, IReadOnlySet<string>? knownProductionIds)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return ChaosTargetClass.Unknown;
        }

        if (knownProductionIds is not null && knownProductionIds.Contains(identifier))
        {
            return ChaosTargetClass.Production;
        }

        if (ProductionShape().IsMatch(identifier))
        {
            return ChaosTargetClass.Production;
        }

        return SandboxShape().IsMatch(identifier) ? ChaosTargetClass.Sandbox : ChaosTargetClass.Unknown;
    }

    // prod, production or live, as a whole first word: prod, prod:x, production-eu, live.main.
    [GeneratedRegex("^(prod|production|live)([:._-]|$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProductionShape();

    // sandbox: then 1 to 64 of a-z, 0-9, dash or dot, starting with a letter or digit. Nothing else.
    [GeneratedRegex("^sandbox:[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SandboxShape();
}
