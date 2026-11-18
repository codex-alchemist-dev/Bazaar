using Bazaar.Contracts.Engines;

namespace Bazaar.Server.EngineDispatch;

// TEMPORARY copy of the Studio tool catalog. The catalog is moving to Bazaar.Contracts.Engines.StudioToolCatalog;
// delete this file at merge and call that type from EngineToolDispatcher.LookupTool.
internal static class TemporaryStudioToolCatalog
{
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
        ["user_keyboard_input"] = RiskClass.Exec,
        ["user_mouse_input"] = RiskClass.Exec,
        ["character_navigation"] = RiskClass.Exec,
    };

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

        if (tool.Length > "generate_".Length && tool.StartsWith("generate_", StringComparison.Ordinal))
        {
            riskClass = RiskClass.Write;
            return true;
        }

        riskClass = RiskClass.Exec;
        return false;
    }
}
