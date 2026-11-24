using System.Text.RegularExpressions;

namespace Bazaar.Server.DevSandbox;

/// <summary>
/// Refuses any sandbox run that could touch a production company, schema or role, and any spec that would widen the sandbox
/// (doc 48 section 2.1 rules 4 and 5). Every check fails closed: a spec with any problem gets a refusal reason, never a
/// best-effort fix. Returns null when the spec is acceptable.
/// </summary>
public static class ProductionIsolationGuard
{
    private const int _maxRunIdLength = 64;

    // Postgres identifiers (schema and role names) are truncated at 63 bytes. A longer name would silently alias another one.
    private const int _maxIdentifierLength = 63;

    private const string _sandboxCompanyPrefix = "sandbox-";
    private const string _sandboxSchemaPrefix = "sandbox_";
    private const int _minTimeoutSeconds = 1;
    private const int _maxTimeoutSeconds = 3600;
    private const int _minMemoryLimitMb = 16;
    private const int _maxMemoryLimitMb = 8192;

    private static readonly Regex _runIdCharacters = new("^[a-z0-9-]+\\z", RegexOptions.CultureInvariant);

    private static readonly Regex _moduleIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant);

    private static readonly Regex _sandboxTargetName = new("^sandbox[-_][a-z0-9_-]+\\z", RegexOptions.CultureInvariant);

    // The _semVer 2.0.0 reference expression (https://semver.org), anchored to the whole string.
    private static readonly Regex _semVer = new(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)"
        + "(?:-((?:0|[1-9]\\d*|\\d*[a-zA-Z-][0-9a-zA-Z-]*)(?:\\.(?:0|[1-9]\\d*|\\d*[a-zA-Z-][0-9a-zA-Z-]*))*))?"
        + "(?:\\+([0-9a-zA-Z-]+(?:\\.[0-9a-zA-Z-]+)*))?\\z",
        RegexOptions.CultureInvariant);

    /// <summary>Validates a run spec before any process starts.</summary>
    /// <param name="spec">The spec to check.</param>
    /// <returns>A refusal reason, or null when the spec is acceptable.</returns>
    public static string? ValidateSpec(SandboxRunSpec spec)
    {
        if (spec is null)
        {
            return "run spec is missing";
        }

        string? runIdReason = ValidateRunId(spec.RunId);
        if (runIdReason is not null)
        {
            return runIdReason;
        }

        if (string.IsNullOrWhiteSpace(spec.ModuleId))
        {
            return "module id is empty";
        }

        // The id is used to look the module up in a store; it must never be able to name a path.
        if (!_moduleIdPattern.IsMatch(spec.ModuleId) || spec.ModuleId.Contains("..", StringComparison.Ordinal))
        {
            return "module id may contain only letters, digits, dot, underscore and hyphen (at most 128, no '..')";
        }

        if (spec.ModuleVersion is null || !_semVer.IsMatch(spec.ModuleVersion))
        {
            return "module version is not _semVer 2.0";
        }

        if (!Enum.IsDefined(spec.Engine))
        {
            return "engine is not a known kind";
        }

        if (spec.Grants is null)
        {
            return "grants are missing";
        }

        foreach (SandboxPermissionGrant? grant in spec.Grants)
        {
            if (grant is null || string.IsNullOrWhiteSpace(grant.Class))
            {
                return "a grant has an empty permission class";
            }

            if (grant.Target is null)
            {
                return "a grant has no target";
            }
        }

        if (spec.TimeoutSeconds is < _minTimeoutSeconds or > _maxTimeoutSeconds)
        {
            return $"timeout must be {_minTimeoutSeconds} to {_maxTimeoutSeconds} seconds";
        }

        if (spec.MemoryLimitMb is < _minMemoryLimitMb or > _maxMemoryLimitMb)
        {
            return $"memory limit must be {_minMemoryLimitMb} to {_maxMemoryLimitMb} MB";
        }

        if (spec.TargetCompanyId is not null && !spec.TargetCompanyId.StartsWith(_sandboxCompanyPrefix, StringComparison.Ordinal))
        {
            return "target company is not a sandbox company; production targets are refused";
        }

        if (spec.TargetCompanyId is not null && (spec.TargetCompanyId.Length > _maxIdentifierLength || ValidateTarget(spec.TargetCompanyId) is not null))
        {
            return "target company id is not a clean sandbox company id; production targets are refused";
        }

        if (spec.EgressAllowlist is null)
        {
            return "egress allowlist is missing";
        }

        foreach (string? host in spec.EgressAllowlist)
        {
            string? hostReason = ValidateEgressHost(host);
            if (hostReason is not null)
            {
                return hostReason;
            }
        }

        return null;
    }

    /// <summary>Checks a run id: lowercase letters, digits and hyphens, not starting or ending with a hyphen, and short enough for a schema name.</summary>
    /// <param name="runId">The run id to check.</param>
    /// <returns>A refusal reason, or null when the run id is acceptable.</returns>
    public static string? ValidateRunId(string? runId)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return "run id is empty";
        }

        if (runId.Length > _maxRunIdLength)
        {
            return $"run id is longer than {_maxRunIdLength} characters";
        }

        if (!_runIdCharacters.IsMatch(runId))
        {
            return "run id may contain only lowercase letters, digits and hyphens";
        }

        if (runId[0] == '-' || runId[^1] == '-')
        {
            return "run id must not start or end with a hyphen";
        }

        if (_sandboxSchemaPrefix.Length + runId.Length > _maxIdentifierLength)
        {
            return $"run id is too long for a schema name (at most {_maxIdentifierLength - _sandboxSchemaPrefix.Length} characters)";
        }

        return null;
    }

    /// <summary>
    /// Checks that a schema or company name is a sandbox name. A null name has no target and is accepted here; the caller decides
    /// whether a target is required.
    /// </summary>
    /// <param name="schemaOrCompany">A schema name (<c>sandbox_...</c>) or company id (<c>sandbox-...</c>), or null.</param>
    /// <returns>A refusal reason, or null when the name is acceptable.</returns>
    public static string? ValidateTarget(string? schemaOrCompany)
    {
        if (schemaOrCompany is null)
        {
            return null;
        }

        if (!_sandboxTargetName.IsMatch(schemaOrCompany))
        {
            return "target is not a sandbox company or schema; production targets are refused";
        }

        return null;
    }

    /// <summary>True only for names that start with <c>sandbox_</c>. Any other schema is production or not ours.</summary>
    /// <param name="name">The schema name.</param>
    /// <returns>True when the name is a sandbox schema.</returns>
    public static bool IsSandboxSchema(string? name)
    {
        return name is not null && name.StartsWith(_sandboxSchemaPrefix, StringComparison.Ordinal);
    }

    private static string? ValidateEgressHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "egress allowlist has an empty host";
        }

        if (host.Contains('/') || host.Contains(':') || host.Any(char.IsWhiteSpace))
        {
            return "egress allowlist entries must be host names only (no scheme, port, path or whitespace)";
        }

        if (host.Contains('*'))
        {
            return "egress allowlist must not contain wildcards";
        }

        if (!string.Equals(SandboxTargetRules.NormalizeHost(host), host, StringComparison.Ordinal))
        {
            return "egress allowlist entries must be plain lower-case DNS names (no control characters, trailing dot, non-ASCII or look-alike characters)";
        }

        if (string.Equals(host, "localhost", StringComparison.Ordinal))
        {
            return null;
        }

        if (host.Length > ".test".Length && host.EndsWith(".test", StringComparison.Ordinal))
        {
            return null;
        }

        return "egress allowlist accepts only test endpoints (names ending in .test or localhost); production host names are refused";
    }
}
