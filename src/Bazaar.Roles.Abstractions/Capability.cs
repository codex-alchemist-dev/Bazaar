using System.Text.RegularExpressions;

namespace Bazaar.Roles.Abstractions;

/// <summary>
/// A named thing a principal may do (doc 48 section 4): one of the tool dispatcher's capability classes (doc 15) or an
/// administrative capability under <c>admin.</c>. Lowercase, dot-separated, hyphens inside a segment.
/// </summary>
public readonly partial record struct Capability
{
    public Capability(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ValidName().IsMatch(value))
            throw new ArgumentException($"'{value}' is not a valid capability name.", nameof(value));
        Value = value;
    }

    public string Value { get; }

    /// <summary>True for <c>admin.*</c>. Agents never hold these (doc 48 section 4.1).</summary>
    public bool IsAdmin => Value.StartsWith("admin.", StringComparison.Ordinal);

    /// <summary>True for the human approval capability. Agents never hold it, and nobody grants it to themselves.</summary>
    public bool IsApproval => Value == Capabilities.Approve.Value;

    public override string ToString() => Value;

    [GeneratedRegex("^[a-z][a-z0-9-]*(\\.[a-z][a-z0-9-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidName();
}

/// <summary>The capabilities the built-in templates use. The classes are doc 15's; the admin names are doc 48 section 4's.</summary>
public static class Capabilities
{
    public static Capability Read { get; } = new("read");

    public static Capability Post { get; } = new("post");

    public static Capability Send { get; } = new("send");

    public static Capability Spend { get; } = new("spend");

    public static Capability Push { get; } = new("push");

    public static Capability Merge { get; } = new("merge");

    public static Capability CreateAccount { get; } = new("create-account");

    public static Capability GrantScope { get; } = new("grant-scope");

    public static Capability InstallPackage { get; } = new("install-package");

    public static Capability Deploy { get; } = new("deploy");

    /// <summary>Give a human approval (doc 15 tier 2, doc 32 section 6). Never held by an agent.</summary>
    public static Capability Approve { get; } = new("approve");

    public static Capability AdminMembers { get; } = new("admin.members");

    public static Capability AdminDevices { get; } = new("admin.devices");

    public static Capability AdminPrivacy { get; } = new("admin.privacy");

    public static Capability AdminBackups { get; } = new("admin.backups");

    public static Capability AdminModules { get; } = new("admin.modules");

    public static Capability AdminRoles { get; } = new("admin.roles");
}
