namespace Bazaar.Contracts.Engines;

/// <summary>
/// The Studio tool allow-list and risk classes (docs/design/20-engine-integration-api.md, Roblox adapter; classes are [PROPOSED]).
/// Shared by the server dispatch and the Studio bridge, so both use the identical list; a tool not listed is denied.
/// </summary>
public static class StudioToolCatalog
{
    /// <summary>Prefix of the asset-generator tools, all <see cref="RiskClass.Write"/> (doc 20 names the group, not each tool).</summary>
    public const string AssetGeneratorPrefix = "generate_";

    private static readonly Dictionary<string, RiskClass> _classes = new(StringComparer.Ordinal)
    {
        ["script_read"] = RiskClass.Read,
        ["script_search"] = RiskClass.Read,
        ["script_grep"] = RiskClass.Read,
        ["inspect_instance"] = RiskClass.Read,
        ["search_game_tree"] = RiskClass.Read,
        ["get_console_output"] = RiskClass.Read,
        ["get_studio_state"] = RiskClass.Read,
        ["list_roblox_studios"] = RiskClass.Read,
        ["multi_edit"] = RiskClass.Write,
        ["insert_asset"] = RiskClass.Write,
        ["upload_image"] = RiskClass.Write,
        ["store_image"] = RiskClass.Write,
        ["execute_luau"] = RiskClass.Exec,
        ["start_stop_play"] = RiskClass.Exec,
        ["http_get"] = RiskClass.Exec,
        ["screen_capture"] = RiskClass.Exec,
        ["user_keyboard_input"] = RiskClass.Exec,
        ["user_mouse_input"] = RiskClass.Exec,
        ["character_navigation"] = RiskClass.Exec,
    };

    /// <summary>The explicitly listed tool names (the generator prefix rule is separate).</summary>
    public static IReadOnlyCollection<string> KnownTools => _classes.Keys;

    /// <summary>Looks up a tool's risk class; false (deny) when the tool is unknown.</summary>
    public static bool TryGetRiskClass(string? tool, out RiskClass riskClass)
    {
        riskClass = RiskClass.Exec;
        if (string.IsNullOrEmpty(tool))
        {
            return false;
        }

        if (_classes.TryGetValue(tool, out riskClass))
        {
            return true;
        }

        if (tool.Length > AssetGeneratorPrefix.Length && tool.StartsWith(AssetGeneratorPrefix, StringComparison.Ordinal))
        {
            riskClass = RiskClass.Write;
            return true;
        }

        riskClass = RiskClass.Exec;
        return false;
    }
}
