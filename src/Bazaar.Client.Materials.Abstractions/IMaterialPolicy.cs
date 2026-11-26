namespace Bazaar.Client.Materials;

/// <summary>Where a surface sits. Chrome surfaces may be translucent; content surfaces never are.</summary>
public enum MaterialSurface
{
    /// <summary>The window and the rail.</summary>
    Chrome,

    /// <summary>The top strip.</summary>
    Strip,

    /// <summary>A bottom or side sheet.</summary>
    Sheet,

    /// <summary>The command palette and the radial menu.</summary>
    Overlay,

    /// <summary>A detached tool window.</summary>
    ToolWindow,

    /// <summary>Never translucent: a message list.</summary>
    MessageList,

    /// <summary>Never translucent: the code editor.</summary>
    Editor,

    /// <summary>Never translucent: the terminal.</summary>
    Terminal,

    /// <summary>Never translucent: diffs.</summary>
    Diff,

    /// <summary>Never translucent: any other surface carrying long-form text.</summary>
    LongText,
}

/// <summary>Why a policy decision came out as it did. Stable for tests and diagnostics.</summary>
public enum MaterialReason
{
    Translucent,
    ContentSurface,
    SettingOpaque,
    ReduceTransparency,
    BatterySaver,
    BackdropFailed,
    FrameBudgetMissed,
    PlatformUnsupported,
}

/// <summary>A resolved material and the reason for it.</summary>
public readonly record struct MaterialDecision(MaterialKind Kind, MaterialReason Reason)
{
    /// <summary>True when the surface is drawn with its opaque fallback.</summary>
    public bool IsOpaque => Kind == MaterialKind.Opaque;
}

/// <summary>Decides the material of a surface from the platform, the person's setting and the current conditions.</summary>
public interface IMaterialPolicy
{
    /// <summary>The material for <paramref name="surface"/> right now. Cheap and side-effect free, so it may be called per layout.</summary>
    MaterialDecision Resolve(MaterialSurface surface);
}
