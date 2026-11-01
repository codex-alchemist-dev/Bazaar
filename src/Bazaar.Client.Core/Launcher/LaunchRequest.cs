using System.Text.Json.Serialization;

namespace Bazaar.Client.Core.Launcher;

// TODO(component): Bazaar Component "launcher". Move to its own project and register via AddBazaarLauncher() from host composition only.

/// <summary>What a second launch asks the running instance to do.</summary>
public sealed record LaunchRequest(IReadOnlyList<string> OpenPaths, bool OpenTerminal)
{
    public static LaunchRequest Empty { get; } = new([], false);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LaunchRequest))]
internal sealed partial class LaunchJsonContext : JsonSerializerContext;
