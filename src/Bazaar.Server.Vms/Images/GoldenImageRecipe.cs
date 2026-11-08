using System.Text.Json.Serialization;

namespace Bazaar.Server.Vms.Images;

/// <summary>Guest operating system of a golden image.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GoldenImageOs>))]
public enum GoldenImageOs
{
    Windows,
    Linux,
}

/// <summary>A tool pinned to an exact version inside a golden image.</summary>
public sealed record PinnedTool
{
    public PinnedTool(string name, string version)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A pinned tool needs a name.", nameof(name));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException($"Pinned tool '{name}' needs a version.", nameof(version));

        Name = name;
        Version = version;
    }

    public string Name { get; }

    public string Version { get; }
}

/// <summary>
/// The build recipe for a golden (parent) image: the OS, the version N of the image, the guest agent version and every
/// pinned tool with its version (docs/design/16 "Golden image and rebase"). Loaded from JSON; invalid recipes throw.
/// </summary>
public sealed record GoldenImageRecipe
{
    public GoldenImageRecipe(GoldenImageOs os, int version, string guestAgentVersion, IReadOnlyList<PinnedTool> pinnedTools)
    {
        if (!Enum.IsDefined(os))
            throw new ArgumentException($"Unknown guest OS '{os}'.", nameof(os));
        if (version <= 0)
            throw new ArgumentOutOfRangeException(nameof(version), version, "The image version must be greater than zero.");
        if (string.IsNullOrWhiteSpace(guestAgentVersion))
            throw new ArgumentException("A guest agent version is required.", nameof(guestAgentVersion));
        ArgumentNullException.ThrowIfNull(pinnedTools);
        if (pinnedTools.Count == 0)
            throw new ArgumentException("A recipe must pin at least one tool.", nameof(pinnedTools));

        var copy = pinnedTools.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in copy)
        {
            ArgumentNullException.ThrowIfNull(tool, nameof(pinnedTools));
            if (!seen.Add(tool.Name))
                throw new ArgumentException($"Pinned tool '{tool.Name}' is listed more than once.", nameof(pinnedTools));
        }

        Os = os;
        Version = version;
        GuestAgentVersion = guestAgentVersion;
        PinnedTools = copy.AsReadOnly();
    }

    public GoldenImageOs Os { get; }

    public int Version { get; }

    public string GuestAgentVersion { get; }

    public IReadOnlyList<PinnedTool> PinnedTools { get; }
}
