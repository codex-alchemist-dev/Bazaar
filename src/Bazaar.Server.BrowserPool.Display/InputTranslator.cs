using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>One DevTools input command: the allow-listed method and its rendered <c>params</c> object.</summary>
internal sealed record InputCommand(CdpMethod Method, byte[] ParamsJson);

/// <summary>The pointer state the driver remembers between batches, because a <see cref="PointerButton"/> acts at the current position.</summary>
internal sealed class PointerState
{
    /// <summary>The last x position, in CSS pixels.</summary>
    public int X { get; set; }

    /// <summary>The last y position, in CSS pixels.</summary>
    public int Y { get; set; }

    /// <summary>The DevTools <c>buttons</c> bit mask currently held (left 1, right 2, middle 4).</summary>
    public int Buttons { get; set; }
}

/// <summary>
/// Turns display input actions into DevTools input commands. Everything is validated and rendered before the first command is
/// sent, so a bad batch has no partial effect. Coordinates are CSS pixels of the viewport. No message, exception or log line here
/// carries typed text.
/// </summary>
internal static class InputTranslator
{
    private const int _altBit = 1;
    private const int _ctrlBit = 2;
    private const int _metaBit = 4;
    private const int _shiftBit = 8;

    private static readonly Dictionary<string, KeyDef> _keys = BuildKeys();

    /// <summary>Whether the key name is in the table.</summary>
    public static bool IsKnownKey(string? key) => key is not null && _keys.ContainsKey(key);

    /// <summary>Every key name the table knows.</summary>
    public static IReadOnlyCollection<string> KnownKeys => _keys.Keys;

    /// <summary>Translates a whole batch. Updates <paramref name="pointer"/> as the batch moves and presses.</summary>
    /// <exception cref="ArgumentException">An action is invalid (negative or too large coordinate, unknown key, bad text, too many actions).</exception>
    /// <exception cref="NotSupportedException">An action kind cannot be expressed over DevTools.</exception>
    public static List<InputCommand> Translate(IReadOnlyList<InputAction> actions, PointerState pointer, BrowserPageDriverOptions options)
    {
        ArgumentNullException.ThrowIfNull(actions);
        if (actions.Count == 0)
        {
            throw new ArgumentException("The input batch is empty.", nameof(actions));
        }

        if (actions.Count > options.MaxActionsPerBatch)
        {
            throw new ArgumentException("The input batch has more actions than the limit.", nameof(actions));
        }

        // Work on a copy so a batch that fails halfway through translation leaves the remembered state alone.
        var state = new PointerState { X = pointer.X, Y = pointer.Y, Buttons = pointer.Buttons };
        var commands = new List<InputCommand>(actions.Count * 2);
        foreach (var action in actions)
        {
            switch (action)
            {
                case PointerMove m:
                    Point(m.X, m.Y, options);
                    state.X = m.X;
                    state.Y = m.Y;
                    commands.Add(Mouse("mouseMoved", state, "none", 0, 0, 0));
                    break;
                case PointerButton b:
                    {
                        var name = ButtonName(b.Button);
                        var bit = ButtonBit(b.Button);
                        state.Buttons = b.Down ? state.Buttons | bit : state.Buttons & ~bit;
                        commands.Add(Mouse(b.Down ? "mousePressed" : "mouseReleased", state, name, 1, 0, 0));
                        break;
                    }

                case PointerClick c:
                    {
                        Point(c.X, c.Y, options);
                        if (c.Count is < 1 or > 3)
                        {
                            throw new ArgumentException("A click count must be 1 to 3.", nameof(actions));
                        }

                        var name = ButtonName(c.Button);
                        var bit = ButtonBit(c.Button);
                        state.X = c.X;
                        state.Y = c.Y;
                        commands.Add(Mouse("mouseMoved", state, "none", 0, 0, 0));
                        for (var i = 1; i <= c.Count; i++)
                        {
                            state.Buttons |= bit;
                            commands.Add(Mouse("mousePressed", state, name, i, 0, 0));
                            state.Buttons &= ~bit;
                            commands.Add(Mouse("mouseReleased", state, name, i, 0, 0));
                        }

                        break;
                    }

                case PointerScroll s:
                    Point(s.X, s.Y, options);
                    if (Math.Abs((long)s.DeltaX) > options.MaxScrollDelta || Math.Abs((long)s.DeltaY) > options.MaxScrollDelta)
                    {
                        throw new ArgumentException("A scroll delta is above the limit.", nameof(actions));
                    }

                    state.X = s.X;
                    state.Y = s.Y;
                    commands.Add(Mouse("mouseMoved", state, "none", 0, 0, 0));
                    commands.Add(Mouse("mouseWheel", state, "none", 0, s.DeltaX, s.DeltaY));
                    break;
                case KeyStroke k:
                    Key(k, commands);
                    break;
                case TypeText t:
                    Text(t, commands, options);
                    break;
                default:
                    throw new NotSupportedException($"The input action {action?.GetType().Name ?? "null"} cannot be expressed over the DevTools protocol.");
            }
        }

        pointer.X = state.X;
        pointer.Y = state.Y;
        pointer.Buttons = state.Buttons;
        return commands;
    }

    private static void Point(int x, int y, BrowserPageDriverOptions options)
    {
        if (x < 0 || y < 0)
        {
            throw new ArgumentException("A pointer coordinate may not be negative.");
        }

        if (x > options.MaxCoordinate || y > options.MaxCoordinate)
        {
            throw new ArgumentException("A pointer coordinate is above the limit.");
        }
    }

    private static string ButtonName(PointerButtonKind button) => button switch
    {
        PointerButtonKind.Left => "left",
        PointerButtonKind.Middle => "middle",
        PointerButtonKind.Right => "right",
        _ => throw new ArgumentException("Unknown pointer button."),
    };

    private static int ButtonBit(PointerButtonKind button) => button switch
    {
        PointerButtonKind.Left => 1,
        PointerButtonKind.Right => 2,
        PointerButtonKind.Middle => 4,
        _ => throw new ArgumentException("Unknown pointer button."),
    };

    private static InputCommand Mouse(string type, PointerState state, string button, int clickCount, int deltaX, int deltaY)
    {
        return Render(CdpMethod.InputDispatchMouseEvent, w =>
        {
            w.WriteString("type"u8, type);
            w.WriteNumber("x"u8, state.X);
            w.WriteNumber("y"u8, state.Y);
            w.WriteString("button"u8, button);
            w.WriteNumber("buttons"u8, state.Buttons);
            w.WriteNumber("clickCount"u8, clickCount);
            w.WriteNumber("modifiers"u8, 0);
            w.WriteString("pointerType"u8, "mouse");
            if (type == "mouseWheel")
            {
                w.WriteNumber("deltaX"u8, deltaX);
                w.WriteNumber("deltaY"u8, deltaY);
            }
        });
    }

    private static void Key(KeyStroke stroke, List<InputCommand> commands)
    {
        if (stroke.Key is null || !_keys.TryGetValue(stroke.Key, out var key))
        {
            throw new ArgumentException("The key is not supported" + Describe(stroke.Key) + ".");
        }

        const KeyModifiers all = KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta;
        if ((stroke.Modifiers & ~all) != 0)
        {
            throw new ArgumentException("Unknown key modifier.");
        }

        // Hold each modifier first, in a fixed order, then the key, then release in reverse.
        var held = new List<(KeyDef Def, int Bit)>();
        var mask = 0;
        foreach (var (flag, name, bit) in ModifierOrder)
        {
            if ((stroke.Modifiers & flag) == 0)
            {
                continue;
            }

            mask |= bit;
            var def = _keys[name];
            held.Add((def, bit));
            commands.Add(KeyEvent("rawKeyDown", def, mask, null));
        }

        // Printable text is only sent when no command modifier is held; Shift changes the character instead.
        var shift = (mask & _shiftBit) != 0;
        var typesText = (mask & (_ctrlBit | _altBit | _metaBit)) == 0;
        var text = typesText ? key.TextFor(shift) : null;
        commands.Add(KeyEvent(text is null ? "rawKeyDown" : "keyDown", key, mask, text, shift));
        commands.Add(KeyEvent("keyUp", key, mask, null, shift));
        for (var i = held.Count - 1; i >= 0; i--)
        {
            mask &= ~held[i].Bit;
            commands.Add(KeyEvent("keyUp", held[i].Def, mask, null));
        }
    }

    private static string Describe(string? key)
    {
        // A key name is not typed text, but a crafted one could be long, so only a short plain name is echoed.
        if (key is { Length: > 0 and <= 24 } && key.All(char.IsAsciiLetterOrDigit))
        {
            return " (" + key + ")";
        }

        return string.Empty;
    }

    private static void Text(TypeText typed, List<InputCommand> commands, BrowserPageDriverOptions options)
    {
        var text = typed.Text;
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Text to type is empty.");
        }

        if (text.Length > options.MaxTextLength)
        {
            throw new ArgumentException("Text to type is longer than the limit.");
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c) && c != '\n' && c != '\t')
            {
                throw new ArgumentException("Text to type contains a control character.");
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]))
                {
                    throw new ArgumentException("Text to type is not valid Unicode.");
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new ArgumentException("Text to type is not valid Unicode.");
            }
        }

        // Text goes in with Input.insertText. A newline is pressed as Enter, because insertText does not submit or break lines in
        // every field; a tab is inserted as a tab character.
        var enter = _keys["Enter"];
        var start = 0;
        while (start <= text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl;
            if (end > start)
            {
                var segment = text[start..end];
                commands.Add(Render(CdpMethod.InputInsertText, w => w.WriteString("text"u8, segment)));
            }

            if (nl < 0)
            {
                break;
            }

            commands.Add(KeyEvent("keyDown", enter, 0, "\r"));
            commands.Add(KeyEvent("keyUp", enter, 0, null));
            start = nl + 1;
        }
    }

    private static InputCommand KeyEvent(string type, KeyDef key, int modifiers, string? text, bool shift = false)
    {
        var keyValue = key.KeyFor(shift);
        return Render(CdpMethod.InputDispatchKeyEvent, w =>
        {
            w.WriteString("type"u8, type);
            w.WriteNumber("modifiers"u8, modifiers);
            w.WriteString("key"u8, keyValue);
            w.WriteString("code"u8, key.Code);
            w.WriteNumber("windowsVirtualKeyCode"u8, key.VirtualKey);
            w.WriteNumber("nativeVirtualKeyCode"u8, key.VirtualKey);
            if (text is not null)
            {
                w.WriteString("text"u8, text);
                w.WriteString("unmodifiedText"u8, key.TextFor(false) ?? text);
            }
        });
    }

    private static InputCommand Render(CdpMethod method, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            write(w);
            w.WriteEndObject();
        }

        return new InputCommand(method, buffer.WrittenSpan.ToArray());
    }

    private static (KeyModifiers Flag, string Name, int Bit)[] ModifierOrder { get; } =
    [
        (KeyModifiers.Control, "ControlLeft", _ctrlBit),
        (KeyModifiers.Alt, "AltLeft", _altBit),
        (KeyModifiers.Shift, "ShiftLeft", _shiftBit),
        (KeyModifiers.Meta, "MetaLeft", _metaBit),
    ];

    private static Dictionary<string, KeyDef> BuildKeys()
    {
        var keys = new Dictionary<string, KeyDef>(StringComparer.Ordinal)
        {
            ["Enter"] = new("Enter", "Enter", 13, "\r"),
            ["Tab"] = new("Tab", "Tab", 9, null),
            ["Escape"] = new("Escape", "Escape", 27, null),
            ["Backspace"] = new("Backspace", "Backspace", 8, null),
            ["Delete"] = new("Delete", "Delete", 46, null),
            ["Space"] = new(" ", "Space", 32, " "),
            ["ArrowLeft"] = new("ArrowLeft", "ArrowLeft", 37, null),
            ["ArrowUp"] = new("ArrowUp", "ArrowUp", 38, null),
            ["ArrowRight"] = new("ArrowRight", "ArrowRight", 39, null),
            ["ArrowDown"] = new("ArrowDown", "ArrowDown", 40, null),
            ["Home"] = new("Home", "Home", 36, null),
            ["End"] = new("End", "End", 35, null),
            ["PageUp"] = new("PageUp", "PageUp", 33, null),
            ["PageDown"] = new("PageDown", "PageDown", 34, null),
            ["ShiftLeft"] = new("Shift", "ShiftLeft", 16, null),
            ["ShiftRight"] = new("Shift", "ShiftRight", 16, null),
            ["ControlLeft"] = new("Control", "ControlLeft", 17, null),
            ["ControlRight"] = new("Control", "ControlRight", 17, null),
            ["AltLeft"] = new("Alt", "AltLeft", 18, null),
            ["AltRight"] = new("Alt", "AltRight", 18, null),
            ["MetaLeft"] = new("Meta", "MetaLeft", 91, null),
            ["MetaRight"] = new("Meta", "MetaRight", 92, null),
        };
        for (var f = 1; f <= 12; f++)
        {
            var name = "F" + f.ToString(CultureInfo.InvariantCulture);
            keys[name] = new KeyDef(name, name, 111 + f, null);
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            var name = "Key" + c;
            keys[name] = new KeyDef(char.ToLowerInvariant(c).ToString(), name, c, char.ToLowerInvariant(c).ToString(), c.ToString());
        }

        for (var d = 0; d <= 9; d++)
        {
            var digit = d.ToString(CultureInfo.InvariantCulture);
            keys["Digit" + digit] = new KeyDef(digit, "Digit" + digit, 48 + d, digit, ShiftedText: null, NoTextWithShift: true);
        }

        return keys;
    }

    /// <summary>One entry of the key table.</summary>
    /// <param name="Key">The DevTools <c>key</c> value.</param>
    /// <param name="Code">The physical <c>code</c> (the display module's key name).</param>
    /// <param name="VirtualKey">The Windows virtual key code, which DevTools wants on every platform.</param>
    /// <param name="Text">The text the key types without Shift, or null for a key that types nothing.</param>
    /// <param name="ShiftedText">The text with Shift held, for letters.</param>
    /// <param name="NoTextWithShift">True for digits: with Shift the character depends on the layout, so no text is sent.</param>
    private sealed record KeyDef(string Key, string Code, int VirtualKey, string? Text, string? ShiftedText = null, bool NoTextWithShift = false)
    {
        public string KeyFor(bool shift) => shift && ShiftedText is not null ? ShiftedText : Key;

        public string? TextFor(bool shift) => shift ? (NoTextWithShift ? null : ShiftedText ?? Text) : Text;
    }
}
