using System.Text.RegularExpressions;

namespace Bazaar.Kodo.Manifest;

/// <summary>The shapes a manifest field must have. A value that does not match is refused, never normalised.</summary>
internal static partial class ModuleManifestPatterns
{
    /// <summary>SemVer 2.0.0 with optional pre-release and build metadata, such as <c>0.1.0+a1b2c3d</c>.</summary>
    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?(\\+[0-9A-Za-z-]+(\\.[0-9A-Za-z-]+)*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex SemVer();

    /// <summary>One permission: a dotted name with an optional scope, such as <c>db.read:gc</c> or <c>kodo.observe</c>. No wildcards.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+(:[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex Permission();

    /// <summary>One path segment of a runbook path: starts with a letter or digit, no trailing dot.</summary>
    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9_-])?$", RegexOptions.CultureInvariant)]
    internal static partial Regex PathSegment();
}
