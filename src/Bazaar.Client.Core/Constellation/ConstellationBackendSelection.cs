namespace Bazaar.Client.Core.Constellation;

/// <summary>Which drawing path the Constellation pane uses.</summary>
public enum ConstellationBackend
{
    /// <summary>The Skia drawing path. Always available, and the fallback when Vulkan cannot draw here.</summary>
    Skia,

    /// <summary>The Vulkan backend (doc constellation-scale-and-lod, section 4). Needs a usable device and a native window.</summary>
    Vulkan,
}

/// <summary>The outcome of choosing a backend, with the reason shown in the pane's diagnostics.</summary>
public readonly record struct ConstellationBackendChoice(ConstellationBackend Backend, string Reason);

/// <summary>
/// Chooses the Constellation drawing path from what this host can actually provide. Pure, so the choice can be
/// tested without a GPU or a window. The Skia path is always the fallback: Vulkan is chosen only when a device
/// is present, the host has a native window handle to draw into, and the user has not asked for Skia.
/// </summary>
public static class ConstellationBackendSelection
{
    public static ConstellationBackendChoice Choose(
        bool vulkanDeviceAvailable,
        bool hostHasNativeWindowHandle,
        bool userPrefersSkia)
    {
        if (userPrefersSkia)
        {
            return new ConstellationBackendChoice(ConstellationBackend.Skia, "The user chose the Skia path.");
        }

        if (!vulkanDeviceAvailable)
        {
            return new ConstellationBackendChoice(ConstellationBackend.Skia, "No Vulkan device was found, so the Skia path is used.");
        }

        if (!hostHasNativeWindowHandle)
        {
            return new ConstellationBackendChoice(ConstellationBackend.Skia, "This view has no native window to draw into, so the Skia path is used.");
        }

        return new ConstellationBackendChoice(ConstellationBackend.Vulkan, "A Vulkan device and a native window are available.");
    }
}
