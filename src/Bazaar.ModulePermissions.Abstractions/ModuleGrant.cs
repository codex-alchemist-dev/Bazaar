using System.Diagnostics.CodeAnalysis;

namespace Bazaar.ModulePermissions;

/// <summary>
/// One module permission in its written form <c>class:scope</c> (doc 48 section 1). The class is one of
/// <see cref="ModulePermissionClasses.All"/>. Only <c>gc.post</c> may stand with no scope. The scope names one target, or ends in <c>/*</c> to cover everything below a
/// prefix by whole segments. A <c>network</c> scope is one host in its canonical spelling and never a prefix.
/// </summary>
public sealed record ModuleGrant
{
    private const string _prefixSuffix = "/*";

    private ModuleGrant(string @class, string? scope)
    {
        Class = @class;
        Scope = scope;
    }

    /// <summary>The permission class, one of <see cref="ModulePermissionClasses.All"/>.</summary>
    public string Class { get; }

    /// <summary>The scope, or null when the permission names the class alone.</summary>
    public string? Scope { get; }

    /// <summary>
    /// The client capability names of doc 41 section 8 and the permission each one stands for (doc 48 section 1, canonical
    /// vocabulary). A capability name is read as its permission, so one approved set serves both spellings.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CapabilityAliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["wire:gc.history"] = "data.read:gc",
        ["wire:gc.post"] = "gc.post",
        ["wire:kanban.read"] = "data.read:kanban",
        ["wire:kanban.write"] = "data.write:kanban",
        ["wire:vault.read"] = "data.read:vault",
        ["wire:constellation.read"] = "data.read:constellation",
        ["storage:local"] = "fs:local/*",
    };

    /// <summary>
    /// Reads a permission. False for anything that is not exactly <c>class</c> or <c>class:scope</c> with a known class and a
    /// valid scope; the caller must treat such a string as granting nothing.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ModuleGrant? permission)
    {
        permission = null;
        if (string.IsNullOrEmpty(text) || text.Length > ModuleTargetRules.MaxTargetLength)
        {
            return false;
        }

        if (CapabilityAliases.TryGetValue(text, out var canonical))
        {
            text = canonical;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            // Only a GC post reads sensibly with no scope. Every other class must name what it wants, so "fs" or "secret"
            // alone can never stand on an approval card looking like the whole class.
            if (text != ModulePermissionClasses.GcPost)
            {
                return false;
            }

            permission = new ModuleGrant(text, null);
            return true;
        }

        var @class = text[..colon];
        var scope = text[(colon + 1)..];
        if (!ModulePermissionClasses.IsClass(@class))
        {
            return false;
        }

        var valid = @class == ModulePermissionClasses.Network
            ? string.Equals(ModuleTargetRules.NormalizeHost(scope), scope, StringComparison.Ordinal)
            : ModuleTargetRules.IsValidScope(scope);
        if (!valid)
        {
            return false;
        }

        permission = new ModuleGrant(@class, scope);
        return true;
    }

    /// <summary>
    /// Whether this permission allows <paramref name="access"/>. The classes must be equal. A permission with no scope allows
    /// only an access with no target, and a scoped permission never allows an access with no target.
    /// </summary>
    public bool Covers(ModuleAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!string.Equals(Class, access.Class, StringComparison.Ordinal))
        {
            return false;
        }

        if (Scope is null || access.Target is null)
        {
            return Scope is null && access.Target is null;
        }

        return Class == ModulePermissionClasses.Network
            ? string.Equals(Scope, ModuleTargetRules.NormalizeHost(access.Target), StringComparison.Ordinal)
            : ModuleTargetRules.ScopeCovers(Scope, access.Target);
    }

    /// <summary>
    /// Whether every access this permission allows is also allowed by <paramref name="other"/>. Used to tell a narrowing
    /// update from a widening one (doc 48 section 1).
    /// </summary>
    public bool IsWithin(ModuleGrant other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (!string.Equals(Class, other.Class, StringComparison.Ordinal))
        {
            return false;
        }

        if (Scope is null || other.Scope is null)
        {
            return Scope is null && other.Scope is null;
        }

        if (string.Equals(Scope, other.Scope, StringComparison.Ordinal))
        {
            return true;
        }

        if (Class == ModulePermissionClasses.Network || !other.Scope.EndsWith(_prefixSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        // A prefix grant contains an exact scope below it, and a prefix grant below it.
        var mine = Scope.EndsWith(_prefixSuffix, StringComparison.Ordinal) ? Scope[..^_prefixSuffix.Length] : Scope;
        return ModuleTargetRules.ScopeCovers(other.Scope, mine);
    }

    /// <summary>The written form.</summary>
    public override string ToString() => Scope is null ? Class : $"{Class}:{Scope}";
}
