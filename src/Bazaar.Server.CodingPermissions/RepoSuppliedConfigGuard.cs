namespace Bazaar.Server.CodingPermissions;

/// <summary>
/// Refuses repo-supplied MCP servers and repo instruction files (doc 17: never auto-loaded, threat model P1). Editing such a
/// file as ordinary code is fine; loading it as configuration or instructions is not.
/// </summary>
public static class RepoSuppliedConfigGuard
{
    private static readonly HashSet<string> _loaderTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "load_mcp_config",
        "register_mcp_server",
        "load_instruction_file",
        "load_repo_instructions",
    };

    private static readonly string[] _configFileNames =
    [
        ".mcp.json", "claude.md", "agents.md", "gemini.md", ".cursorrules", ".windsurfrules",
    ];

    private static readonly string[] _configPathPrefixes =
    [
        ".claude/", ".cursor/", ".github/copilot-instructions.md", ".vscode/mcp.json",
    ];

    /// <summary>The reason the call is refused, or null when the guard has no objection.</summary>
    public static string? Check(ToolCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_loaderTools.Contains(context.Tool))
        {
            return $"Tool '{context.Tool}' loads repo-supplied MCP servers or instruction files, which are never allowed.";
        }

        if (IsMcpTool(context.Tool) && IsRepoLabel(context.SourceTrust))
        {
            return $"MCP tool '{context.Tool}' comes from a repo-supplied server, which is never allowed.";
        }

        if (context.TargetPath is { } path
            && IsRepoConfigPath(path)
            && context.Tool.Contains("load", StringComparison.OrdinalIgnoreCase))
        {
            return $"Loading '{path}' as configuration or instructions is never allowed.";
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> names a repo MCP or instruction file.</summary>
    public static bool IsRepoConfigPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var lower = path.Replace('\\', '/').ToLowerInvariant();
        while (lower.StartsWith("./", StringComparison.Ordinal))
        {
            lower = lower[2..];
        }

        var name = lower[(lower.LastIndexOf('/') + 1)..];
        foreach (var file in _configFileNames)
        {
            if (name == file)
            {
                return true;
            }
        }

        foreach (var prefix in _configPathPrefixes)
        {
            if (lower.StartsWith(prefix, StringComparison.Ordinal) || lower.Contains("/" + prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMcpTool(string tool) =>
        tool.StartsWith("mcp:", StringComparison.OrdinalIgnoreCase) || tool.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase);

    private static bool IsRepoLabel(string label) =>
        label.StartsWith("repo", StringComparison.OrdinalIgnoreCase);
}
