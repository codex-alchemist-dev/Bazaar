using System.Text.RegularExpressions;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>Builds the argument vector of docs/design/91 section 5. Pure; nothing here touches the disk or the environment.</summary>
internal static partial class ClaudeCodeArguments
{
    /// <summary>The MCP server name the bridge registers under; the model sees tools as <c>mcp__bazaar__name</c>.</summary>
    public const string BridgeServerName = "bazaar";

    public const string ToolPrefix = "mcp__" + BridgeServerName + "__";

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();

    public static bool IsValidModelId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 128 && ModelIdPattern().IsMatch(id);

    public static IReadOnlyList<string> Build(ClaudeCodeAuthMode mode, string modelId, string systemPromptFile, string mcpConfigFile)
    {
        if (!IsValidModelId(modelId))
        {
            throw new ArgumentException("The model id is not allowed.", nameof(modelId));
        }

        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--no-session-persistence",
            "--model=" + modelId,
            "--system-prompt-file", systemPromptFile,
            "--tools", "",
            "--strict-mcp-config",
            "--mcp-config", mcpConfigFile,
            "--max-turns", "1",
            "--permission-mode", "dontAsk",
            "--permission-prompts", "none",
        };
        if (mode == ClaudeCodeAuthMode.ApiKey)
        {
            args.Add("--bare");
        }
        else
        {
            args.Add("--safe-mode");
        }

        return args;
    }
}
