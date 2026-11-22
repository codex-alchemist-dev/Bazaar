using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// The settings registry as setup sees it: the install-scope keys a plan or template may set, and the checks a value must pass.
/// Setup binds to this narrow view so it never references the server project; the host or the admin tool adapts the real
/// registry to it. Lookups and checks are pure and never touch storage.
/// </summary>
public interface ISetupSettingCatalog
{
    /// <summary>Every registered key, in no promised order.</summary>
    IReadOnlyCollection<ISetupSettingDefinition> Definitions { get; }

    /// <summary>Finds a key by its exact dotted name.</summary>
    bool TryGet(string name, out ISetupSettingDefinition? definition);
}

/// <summary>One registered setting as setup sees it.</summary>
public interface ISetupSettingDefinition
{
    /// <summary>Dotted, area-prefixed key, for example <c>host.status.bind</c>.</summary>
    string Name { get; }

    /// <summary>True when the key has install scope. A setup plan sets install values only.</summary>
    bool IsInstallScope { get; }

    /// <summary>The CLR type of the value (bool, int, string, an enum, or a read-only list).</summary>
    Type ValueType { get; }

    /// <summary>Type and validator check for a value. Returns null when it is valid, otherwise the reason.</summary>
    string? CheckValue(JsonElement value);

    /// <summary>
    /// Runs the write rule for a proposed value as an operator actor with the id <paramref name="actorId"/>: accepted, held for
    /// approval, or rejected. Pure; nothing is stored.
    /// </summary>
    SetupSettingEvaluation Evaluate(string actorId, JsonElement proposed);
}

/// <summary>What the registry decided about one proposed write.</summary>
/// <param name="Accepted">True when the write would be applied at once; false when it is held for approval or rejected.</param>
/// <param name="Reason">Why it was held or rejected; null when accepted.</param>
/// <param name="ValueJson">Canonical JSON of the proposed value, or the raw text when it did not parse.</param>
public sealed record SetupSettingEvaluation(bool Accepted, string? Reason, string ValueJson);

/// <summary>Receives one audit entry per install setting a setup plan changed. The host adapts it to the settings audit sink.</summary>
public interface ISetupSettingAudit
{
    /// <summary>Records an accepted install-scope change of <paramref name="key"/> made by <paramref name="actorId"/>.</summary>
    Task AppendInstallChangeAsync(string key, string actorId, string? oldValueJson, string newValueJson, CancellationToken cancellationToken = default);
}

/// <summary>A caller proved by a bearer token, as the provisioning API sees it.</summary>
/// <param name="Id">Principal id.</param>
/// <param name="Kind">Principal kind, such as <c>human</c> or <c>agent</c>.</param>
public sealed record SetupPrincipal(Guid Id, string Kind);

/// <summary>Validates the bearer token of a provisioning call. The host adapts its connection authenticator to it.</summary>
public interface ISetupCallerAuthenticator
{
    /// <summary>Returns the principal for a valid, unexpired token, or null when the token is not valid.</summary>
    ValueTask<SetupPrincipal?> AuthenticateAsync(string bearerToken, CancellationToken cancellationToken);
}

/// <summary>The transport rule for provisioning calls that arrive from off the machine.</summary>
public interface ISetupTransportPolicy
{
    /// <summary>True when the operator allowed plaintext HTTP from an overlay network.</summary>
    bool AllowPlaintextOverlay { get; }
}
