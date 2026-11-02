namespace Bazaar.Contracts.Engines;

/// <summary>JSON-RPC method names for the adapter lifecycle (docs/design/20-engine-integration-api.md, Adapter contract).</summary>
public static class EngineMethods
{
    public const string Open = "open";
    public const string Command = "command";
    public const string Cancel = "cancel";
    public const string Close = "close";
    public const string Health = "health";

    /// <summary>Notification name for streamed command output. Adapters send it without an id.</summary>
    public const string Output = "output";
}

/// <summary>
/// Typed error codes for adapter responses. These are Bazaar-defined and sit in the JSON-RPC server-error range
/// (-32000 to -32099), so they do not collide with the JSON-RPC reserved codes.
/// </summary>
public static class EngineErrorCodes
{
    /// <summary>The adapter process is not running or cannot be reached.</summary>
    public const int AdapterUnavailable = -32010;

    /// <summary>The command exceeded <c>engine.command.timeout_minutes</c>.</summary>
    public const int Timeout = -32011;

    /// <summary>The command was cancelled by a <c>cancel</c> request.</summary>
    public const int Cancelled = -32012;

    /// <summary>The caller holds no grant for this tool or risk class.</summary>
    public const int CapabilityNotGranted = -32013;

    /// <summary>The adapter process exited or hung and was killed by the server.</summary>
    public const int AdapterCrashed = -32014;
}
