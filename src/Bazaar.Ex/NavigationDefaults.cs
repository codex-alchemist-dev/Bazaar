namespace Bazaar.Ex;

/// <summary>
/// First-party navigation targets. Modules add their own through the module contract; the company
/// may lock or re-code any of these. Codes are part of the default vocabulary, not a fixed API.
/// </summary>
public static class NavigationDefaults
{
    public const string GcFirstCommandId = "nav.gc.first";
    public const string GcTopCommandId = "nav.gc.top";
    public const string RyuseiRoadmapCommandId = "nav.ryusei.roadmap";
    public const string RyuseiBoardCommandId = "nav.ryusei.board";
    public const string UnreadCommandId = "nav.unread";
    public const string SettingsCommandId = "nav.settings";
    public const string ConstellationCommandId = "nav.constellation";

    public static IReadOnlyList<MnemonicAssignment> Assignments { get; } = new[]
    {
        new MnemonicAssignment(GcFirstCommandId, "GC list, first chat", "ngc1", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(GcTopCommandId, "Most recent activity GC", "topgc", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(RyuseiRoadmapCommandId, "RyuseiFuda Roadmap", "nrfdrm", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(RyuseiBoardCommandId, "RyuseiFuda board", "ryuseifuda", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(RyuseiBoardCommandId, "RyuseiFuda board", "kanban", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource, Alias: true),
        new MnemonicAssignment(UnreadCommandId, "Unread", "unread", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(SettingsCommandId, "Settings", "settings", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
        new MnemonicAssignment(ConstellationCommandId, "Constellation", "constellation", MnemonicTier.BuiltIn, MnemonicAssignment.BuiltInSource),
    };
}
