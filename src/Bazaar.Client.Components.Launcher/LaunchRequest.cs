using System.Text.Json.Serialization;

namespace Bazaar.Client.Components.Launcher;

// Bazaar Component "launcher" (client). Host composition (the Windows head) is the only caller of its public API.

/// <summary>What a second launch asks the running instance to do.</summary>
public sealed record LaunchRequest(IReadOnlyList<string> OpenPaths, bool OpenTerminal)
{
    public static LaunchRequest Empty { get; } = new([], false);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LaunchRequest))]
internal sealed partial class LaunchJsonContext : JsonSerializerContext;
