using Bazaar.Server.Settings;

namespace Bazaar.Client.Settings;

/// <summary>
/// What the settings pane needs from the server: the registry's keys with their stored values, who the signed-in person is for
/// settings, and a way to write one value. The server stays the authority: it re-checks role, validator and approval on every
/// write, so a client that skips its own checks gains nothing (doc 03 settings registry).
/// </summary>
public interface ISettingsClient
{
    /// <summary>Every registered key with its stored value, and the actor the server resolves for this session.</summary>
    Task<SettingsSnapshot> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One key with its stored value, or null when the server does not know the key.</summary>
    Task<SettingsEntry?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Asks the server to store <paramref name="valueJson"/> for <paramref name="name"/>. The server decides the outcome.</summary>
    Task<SettingsChangeResult> SetAsync(string name, string valueJson, CancellationToken cancellationToken = default);
}

/// <summary>The registry's keys as the client sees them, and the actor whose roles decide what is read-only.</summary>
/// <param name="Actor">Who the server treats this session as (id and roles).</param>
/// <param name="Entries">Every key with its stored value.</param>
public sealed record SettingsSnapshot(SettingActor Actor, IReadOnlyList<SettingsEntry> Entries);

/// <summary>One registered key and its stored value.</summary>
/// <param name="Definition">The registry definition: name, scope, role, approval tier, description, default and validation.</param>
/// <param name="ValueJson">The stored value as JSON text, or null when the key is unset and the default applies.</param>
public sealed record SettingsEntry(SettingDefinition Definition, string? ValueJson);

/// <summary>The server's answer to one write.</summary>
/// <param name="Outcome">Accepted, PendingApproval or Rejected.</param>
/// <param name="Reason">Why it was rejected or held; null when accepted. Shown to the person as it is.</param>
public sealed record SettingsChangeResult(SettingWriteOutcome Outcome, string? Reason);

/// <summary>The client has no connection to a server that serves settings, so nothing can be listed or written.</summary>
public sealed class SettingsNotConnectedException()
    : InvalidOperationException("Not connected: no server is serving settings to this client.");
