namespace Bazaar.Contracts.Engines;

/// <summary>
/// A capability an engine adapter declares in its manifest (docs/design/20-engine-integration-api.md, Capabilities).
/// The UI and agents query by capability, never by engine id. Serialized as snake_case strings.
/// </summary>
public enum EngineCapability
{
    Scenes,
    Assets,
    Animation,

    /// <summary>Unconfirmed: owner question 1 in docs/design/20-engine-integration-api.md.</summary>
    Timeline,
    Debugging,
    HotReload,
    ScriptCheck,
    Import,
    Export,
    Test,
    Playtest,
}

/// <summary>
/// The risk class of an adapter tool. Default deny applies to <see cref="Write"/> and <see cref="Exec"/>
/// (docs/design/20-engine-integration-api.md, Data and interfaces). Serialized as snake_case strings.
/// </summary>
public enum RiskClass
{
    Read,
    Write,
    Exec,
}
