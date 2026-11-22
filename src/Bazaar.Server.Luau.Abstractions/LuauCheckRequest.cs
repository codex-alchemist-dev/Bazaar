namespace Bazaar.Server.Luau.Abstractions;

/// <summary>
/// One check run. <paramref name="ProjectRoot"/> is the working directory for every tool (so a project's
/// <c>selene.toml</c> and <c>.stylua.toml</c> apply). <paramref name="Files"/> are paths relative to the root.
/// An empty <paramref name="Tools"/> list runs every tool. A null <paramref name="Timeout"/> uses
/// <see cref="LuauToolOptions.DefaultTimeout"/> for each tool.
/// </summary>
public sealed record LuauCheckRequest(
    string ProjectRoot,
    IReadOnlyList<string> Files,
    IReadOnlyList<LuauToolKind> Tools,
    TimeSpan? Timeout = null);
