using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.VmDisplay.Devices;

/// <summary>
/// Maps display input to device input for an Android guest (docs/design/62 section 5.1, docs/design/59 section 4). Pure. An action
/// Android cannot express, such as a modifier chord, a pointer move without a click or a right click, is refused rather than approximated.
/// </summary>
public static class DeviceInputMapper
{
    /// <summary>Android key codes for the display key names (KeyEvent constants).</summary>
    private static readonly Dictionary<string, int> _keyCodes = BuildKeyCodes();

    public static IReadOnlyList<DeviceInput> Map(IReadOnlyList<InputAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        var result = new List<DeviceInput>();
        foreach (InputAction action in actions)
        {
            switch (action)
            {
                case PointerClick { Button: PointerButtonKind.Left } click when click.Count is >= 1 and <= 3 && click.X >= 0 && click.Y >= 0:
                    for (int i = 0; i < click.Count; i++)
                        result.Add(new DeviceInput.Tap(click.X, click.Y));
                    break;
                case KeyStroke { Modifiers: KeyModifiers.None } key when _keyCodes.TryGetValue(key.Key, out int code):
                    result.Add(new DeviceInput.Key(code));
                    break;
                case TypeText text:
                    result.Add(new DeviceInput.Text(text.Text));
                    break;
                default:
                    throw new DisplayException(DisplayFailure.InvalidInput, $"An Android guest cannot take the input action {action.GetType().Name} in this form.");
            }
        }

        return result;
    }

    private static Dictionary<string, int> BuildKeyCodes()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Enter"] = 66,
            ["Tab"] = 61,
            ["Escape"] = 111,
            ["Backspace"] = 67,
            ["Delete"] = 112,
            ["Space"] = 62,
            ["ArrowUp"] = 19,
            ["ArrowDown"] = 20,
            ["ArrowLeft"] = 21,
            ["ArrowRight"] = 22,
            ["Home"] = 122,
            ["End"] = 123,
            ["PageUp"] = 92,
            ["PageDown"] = 93,
        };
        for (int c = 0; c < 26; c++)
            map["Key" + (char)('A' + c)] = 29 + c;
        for (int d = 0; d <= 9; d++)
            map["Digit" + d] = 7 + d;
        for (int f = 1; f <= 12; f++)
            map["F" + f] = 131 + f - 1;
        return map;
    }
}
