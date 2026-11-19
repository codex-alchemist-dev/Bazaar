using System.Globalization;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.HyperV;

/// <summary>Maps the allowlisted W3C key names to Windows virtual-key codes (what <c>Msvm_Keyboard</c> takes).</summary>
internal static class HyperVKeyTable
{
    public const int VkReturn = 0x0D;
    public const int VkTab = 0x09;

    private static readonly Dictionary<string, int> _keys = Build();

    /// <summary>Whether <paramref name="key"/> has a virtual-key code, and which.</summary>
    public static bool TryGetKey(string key, out int code) => _keys.TryGetValue(key, out code);

    /// <summary>The codes of the modifier keys to press, in press order.</summary>
    public static IEnumerable<int> ModifierCodes(KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Shift))
            yield return 0x10; // VK_SHIFT
        if (modifiers.HasFlag(KeyModifiers.Control))
            yield return 0x11; // VK_CONTROL
        if (modifiers.HasFlag(KeyModifiers.Alt))
            yield return 0x12; // VK_MENU
        if (modifiers.HasFlag(KeyModifiers.Meta))
            yield return 0x5B; // VK_LWIN
    }

    private static Dictionary<string, int> Build()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Enter"] = VkReturn,
            ["Tab"] = VkTab,
            ["Escape"] = 0x1B,
            ["Backspace"] = 0x08,
            ["Delete"] = 0x2E,
            ["Space"] = 0x20,
            ["ArrowLeft"] = 0x25,
            ["ArrowUp"] = 0x26,
            ["ArrowRight"] = 0x27,
            ["ArrowDown"] = 0x28,
            ["PageUp"] = 0x21,
            ["PageDown"] = 0x22,
            ["End"] = 0x23,
            ["Home"] = 0x24,
        };
        for (char c = 'A'; c <= 'Z'; c++)
            map["Key" + c] = c; // VK_A..VK_Z equal the ASCII capitals
        for (int d = 0; d <= 9; d++)
            map["Digit" + d.ToString(CultureInfo.InvariantCulture)] = 0x30 + d;
        for (int f = 1; f <= 12; f++)
            map["F" + f.ToString(CultureInfo.InvariantCulture)] = 0x70 + f - 1;
        return map;
    }
}
