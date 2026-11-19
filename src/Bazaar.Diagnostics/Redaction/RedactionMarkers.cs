using System.Globalization;

namespace Bazaar.Diagnostics.Redaction;

/// <summary>Replacement text. Markers never match a rule, so a redacted string passes the residual scan.</summary>
public static class RedactionMarkers
{
    /// <summary>A built-in pattern matched.</summary>
    public const string Pattern = "<redacted:pattern>";

    /// <summary>A runtime exact value matched (machine name, session token and similar).</summary>
    public const string Exact = "<redacted:exact>";

    /// <summary>The field is never sent (<see cref="RedactionClass.Never"/>).</summary>
    public const string Structural = "<redacted:structural>";

    /// <summary>A redaction step threw. The report is held rather than sent.</summary>
    public const string Failed = "<redacted:failed>";

    /// <summary>The residual scan still found a match after redaction. The report is held.</summary>
    public const string Residual = "<redacted:residual>";

    /// <summary>Replaces a home directory name, for example <c>C:\Users\name\</c>.</summary>
    public const string Home = "<home>";

    /// <summary>Marks the end of a string cut at the length limit, with the number of characters dropped.</summary>
    public static string Truncated(int dropped) => "<truncated:" + dropped.ToString(CultureInfo.InvariantCulture) + ">";
}
