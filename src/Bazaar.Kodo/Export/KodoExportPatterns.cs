using System.Text.RegularExpressions;

namespace Bazaar.Kodo.Export;

/// <summary>
/// The identifier shapes the export accepts (doc 46 sections 15.3 and 15.4). An id that does not match is refused, never escaped
/// into a label, so a stray instance id cannot widen a metric's cardinality.
/// </summary>
internal static partial class KodoExportPatterns
{
    /// <summary>A probe or category id such as <c>host.disk.free</c>: lowercase segments only.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)*$", RegexOptions.CultureInvariant)]
    internal static partial Regex ComponentId();

    /// <summary>A run of 16 or more hex digits. Instance ids (agents, group chats, sessions) look like this, so a component id must not contain one.</summary>
    [GeneratedRegex("[0-9a-f]{16,}", RegexOptions.CultureInvariant)]
    internal static partial Regex InstanceIdLike();

    /// <summary>A module id such as <c>kodo</c>.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex ModuleId();

    /// <summary>A webhook event id or delivery id.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    internal static partial Regex EventId();

    /// <summary>A webhook identifier name, such as <c>probe_id</c>.</summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    internal static partial Regex IdentifierName();

    /// <summary>A webhook identifier value. Identifiers only, never free text.</summary>
    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,128}$", RegexOptions.CultureInvariant)]
    internal static partial Regex IdentifierValue();

    /// <summary>A webhook signing key id. It travels in a header, so it is kept to URL-safe characters.</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex KeyId();

    /// <summary>An allow-list entry: a DNS name or a <c>*.</c> wildcard over one. No scheme, port, path or address.</summary>
    [GeneratedRegex("^(\\*\\.)?[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$", RegexOptions.CultureInvariant)]
    internal static partial Regex HostPattern();
}
