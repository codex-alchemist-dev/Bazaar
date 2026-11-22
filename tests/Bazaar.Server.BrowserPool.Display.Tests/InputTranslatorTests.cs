using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.BrowserPool.Display.Tests;

public sealed class InputTranslatorTests
{
    private static readonly BrowserPageDriverOptions _options = new();

    private static List<InputCommand> Translate(params InputAction[] actions) => InputTranslator.Translate(actions, new PointerState(), _options);

    private static JsonElement P(InputCommand c) => JsonElement.Parse(c.ParamsJson);

    private sealed record Unknown : InputAction;

    [Fact]
    public void A_pointer_move_is_one_mouse_moved_event()
    {
        var c = Assert.Single(Translate(new PointerMove(12, 34)));

        Assert.Equal(CdpMethod.InputDispatchMouseEvent, c.Method);
        Assert.Equal("{\"type\":\"mouseMoved\",\"x\":12,\"y\":34,\"button\":\"none\",\"buttons\":0,\"clickCount\":0,\"modifiers\":0,\"pointerType\":\"mouse\"}", System.Text.Encoding.UTF8.GetString(c.ParamsJson));
    }

    [Theory]
    [InlineData(PointerButtonKind.Left, "left", 1)]
    [InlineData(PointerButtonKind.Right, "right", 2)]
    [InlineData(PointerButtonKind.Middle, "middle", 4)]
    public void A_pointer_button_presses_and_releases_at_the_current_position(PointerButtonKind kind, string name, int bit)
    {
        var state = new PointerState { X = 7, Y = 9 };

        var cs = InputTranslator.Translate([new PointerButton(kind, true), new PointerButton(kind, false)], state, _options);

        Assert.Equal(2, cs.Count);
        Assert.Equal("mousePressed", P(cs[0]).GetProperty("type").GetString());
        Assert.Equal(name, P(cs[0]).GetProperty("button").GetString());
        Assert.Equal(bit, P(cs[0]).GetProperty("buttons").GetInt32());
        Assert.Equal(7, P(cs[0]).GetProperty("x").GetInt32());
        Assert.Equal(9, P(cs[0]).GetProperty("y").GetInt32());
        Assert.Equal(1, P(cs[0]).GetProperty("clickCount").GetInt32());
        Assert.Equal("mouseReleased", P(cs[1]).GetProperty("type").GetString());
        Assert.Equal(0, P(cs[1]).GetProperty("buttons").GetInt32());
        Assert.Equal(0, state.Buttons);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_click_moves_then_presses_and_releases_with_a_rising_click_count(int count)
    {
        var cs = Translate(new PointerClick(100, 200, PointerButtonKind.Left, count));

        Assert.Equal(1 + (2 * count), cs.Count);
        Assert.Equal("mouseMoved", P(cs[0]).GetProperty("type").GetString());
        for (var i = 1; i <= count; i++)
        {
            var down = P(cs[(2 * i) - 1]);
            var up = P(cs[2 * i]);
            Assert.Equal("mousePressed", down.GetProperty("type").GetString());
            Assert.Equal("mouseReleased", up.GetProperty("type").GetString());
            Assert.Equal(i, down.GetProperty("clickCount").GetInt32());
            Assert.Equal(i, up.GetProperty("clickCount").GetInt32());
            Assert.Equal(100, down.GetProperty("x").GetInt32());
            Assert.Equal(200, up.GetProperty("y").GetInt32());
        }
    }

    [Fact]
    public void A_scroll_moves_then_sends_a_wheel_event_with_the_deltas()
    {
        var cs = Translate(new PointerScroll(30, 40, -5, 120));

        Assert.Equal(2, cs.Count);
        Assert.Equal("mouseWheel", P(cs[1]).GetProperty("type").GetString());
        Assert.Equal(-5, P(cs[1]).GetProperty("deltaX").GetInt32());
        Assert.Equal(120, P(cs[1]).GetProperty("deltaY").GetInt32());
        Assert.Equal(30, P(cs[1]).GetProperty("x").GetInt32());
    }

    [Fact]
    public void A_plain_key_is_a_key_down_with_text_and_a_key_up()
    {
        var cs = Translate(new KeyStroke("KeyA"));

        Assert.Equal(2, cs.Count);
        Assert.All(cs, c => Assert.Equal(CdpMethod.InputDispatchKeyEvent, c.Method));
        var down = P(cs[0]);
        Assert.Equal("keyDown", down.GetProperty("type").GetString());
        Assert.Equal("a", down.GetProperty("key").GetString());
        Assert.Equal("KeyA", down.GetProperty("code").GetString());
        Assert.Equal(65, down.GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.Equal("a", down.GetProperty("text").GetString());
        Assert.Equal("keyUp", P(cs[1]).GetProperty("type").GetString());
        Assert.False(P(cs[1]).TryGetProperty("text", out _));
    }

    [Fact]
    public void Shift_holds_the_modifier_first_uppercases_a_letter_and_releases_in_reverse()
    {
        var cs = Translate(new KeyStroke("KeyA", KeyModifiers.Shift));

        Assert.Equal(4, cs.Count);
        Assert.Equal("Shift", P(cs[0]).GetProperty("key").GetString());
        Assert.Equal("rawKeyDown", P(cs[0]).GetProperty("type").GetString());
        Assert.Equal(8, P(cs[0]).GetProperty("modifiers").GetInt32());
        Assert.Equal("A", P(cs[1]).GetProperty("key").GetString());
        Assert.Equal("A", P(cs[1]).GetProperty("text").GetString());
        Assert.Equal(8, P(cs[1]).GetProperty("modifiers").GetInt32());
        Assert.Equal("keyUp", P(cs[2]).GetProperty("type").GetString());
        Assert.Equal("Shift", P(cs[3]).GetProperty("key").GetString());
        Assert.Equal("keyUp", P(cs[3]).GetProperty("type").GetString());
        Assert.Equal(0, P(cs[3]).GetProperty("modifiers").GetInt32());
    }

    [Fact]
    public void A_command_modifier_sends_a_raw_key_with_no_text_and_all_modifier_bits()
    {
        var cs = Translate(new KeyStroke("KeyC", KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta));

        var main = P(cs[3]);
        Assert.Equal("rawKeyDown", main.GetProperty("type").GetString());
        Assert.False(main.TryGetProperty("text", out _));
        Assert.Equal(2 | 1 | 4, main.GetProperty("modifiers").GetInt32());
        Assert.Equal(8, cs.Count);
    }

    [Theory]
    [InlineData("Enter", 13, "\r")]
    [InlineData("Space", 32, " ")]
    [InlineData("Tab", 9, null)]
    [InlineData("Escape", 27, null)]
    [InlineData("Backspace", 8, null)]
    [InlineData("Delete", 46, null)]
    [InlineData("ArrowLeft", 37, null)]
    [InlineData("ArrowUp", 38, null)]
    [InlineData("ArrowRight", 39, null)]
    [InlineData("ArrowDown", 40, null)]
    [InlineData("Home", 36, null)]
    [InlineData("End", 35, null)]
    [InlineData("PageUp", 33, null)]
    [InlineData("PageDown", 34, null)]
    [InlineData("F1", 112, null)]
    [InlineData("F12", 123, null)]
    [InlineData("Digit7", 55, "7")]
    [InlineData("ShiftLeft", 16, null)]
    [InlineData("ControlRight", 17, null)]
    [InlineData("AltLeft", 18, null)]
    [InlineData("MetaLeft", 91, null)]
    public void The_key_table_maps_names_to_codes_and_text(string key, int virtualKey, string? text)
    {
        var down = P(Translate(new KeyStroke(key))[0]);

        Assert.Equal(key, down.GetProperty("code").GetString());
        Assert.Equal(virtualKey, down.GetProperty("windowsVirtualKeyCode").GetInt32());
        Assert.Equal(virtualKey, down.GetProperty("nativeVirtualKeyCode").GetInt32());
        if (text is null)
        {
            Assert.False(down.TryGetProperty("text", out _));
            Assert.Equal("rawKeyDown", down.GetProperty("type").GetString());
        }
        else
        {
            Assert.Equal(text, down.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void Every_key_the_display_module_allows_is_in_the_table()
    {
        foreach (var key in DisplayKeys.All)
        {
            Assert.True(InputTranslator.IsKnownKey(key), key);
        }
    }

    [Theory]
    [InlineData("NoSuchKey")]
    [InlineData("keya")]
    [InlineData("")]
    [InlineData("Key A")]
    public void An_unknown_key_is_refused_with_a_clear_exception(string key)
    {
        var e = Assert.Throws<ArgumentException>(() => Translate(new KeyStroke(key)));

        Assert.Contains("not supported", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_very_long_key_name_is_not_echoed_in_the_message()
    {
        var name = new string('Z', 500);

        var e = Assert.Throws<ArgumentException>(() => Translate(new KeyStroke(name)));

        Assert.DoesNotContain(name, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_modifier_bit_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Translate(new KeyStroke("KeyA", (KeyModifiers)64)));
    }

    [Fact]
    public void Plain_text_is_one_insert_text_command()
    {
        var c = Assert.Single(Translate(new TypeText("hello wörld \U0001F600")));

        Assert.Equal(CdpMethod.InputInsertText, c.Method);
        Assert.Equal("hello wörld \U0001F600", P(c).GetProperty("text").GetString());
    }

    [Fact]
    public void A_newline_in_text_is_pressed_as_enter_between_two_inserts()
    {
        var cs = Translate(new TypeText("ab\ncd\n"));

        Assert.Equal(
            [CdpMethod.InputInsertText, CdpMethod.InputDispatchKeyEvent, CdpMethod.InputDispatchKeyEvent, CdpMethod.InputInsertText, CdpMethod.InputDispatchKeyEvent, CdpMethod.InputDispatchKeyEvent],
            cs.Select(c => c.Method));
        Assert.Equal("ab", P(cs[0]).GetProperty("text").GetString());
        Assert.Equal("Enter", P(cs[1]).GetProperty("key").GetString());
        Assert.Equal("cd", P(cs[3]).GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(16385, 0)]
    [InlineData(0, 16385)]
    [InlineData(int.MinValue, 5)]
    [InlineData(5, int.MaxValue)]
    public void A_negative_or_out_of_bounds_coordinate_is_refused_for_every_pointer_action(int x, int y)
    {
        Assert.Throws<ArgumentException>(() => Translate(new PointerMove(x, y)));
        Assert.Throws<ArgumentException>(() => Translate(new PointerClick(x, y)));
        Assert.Throws<ArgumentException>(() => Translate(new PointerScroll(x, y, 1, 1)));
    }

    [Fact]
    public void The_largest_allowed_coordinate_is_accepted()
    {
        Translate(new PointerMove(16384, 16384));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(-1)]
    public void A_click_count_outside_one_to_three_is_refused(int count)
    {
        Assert.Throws<ArgumentException>(() => Translate(new PointerClick(1, 1, PointerButtonKind.Left, count)));
    }

    [Fact]
    public void A_scroll_delta_above_the_limit_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Translate(new PointerScroll(1, 1, 10_001, 0)));
        Assert.Throws<ArgumentException>(() => Translate(new PointerScroll(1, 1, 0, int.MinValue)));
        Translate(new PointerScroll(1, 1, 10_000, -10_000));
    }

    [Fact]
    public void An_unknown_button_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Translate(new PointerButton((PointerButtonKind)9, true)));
        Assert.Throws<ArgumentException>(() => Translate(new PointerClick(1, 1, (PointerButtonKind)9)));
    }

    [Fact]
    public void An_action_kind_that_cannot_be_expressed_throws_not_supported_naming_it()
    {
        var e = Assert.Throws<NotSupportedException>(() => Translate(new Unknown()));

        Assert.Contains(nameof(Unknown), e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_text_is_refused_and_the_exception_never_carries_the_text()
    {
        const string secret = "hunter2-SECRET";
        var cases = new[] { secret + "\u0001", secret + "\r", secret + "\0", secret + "\ud800", "\udc00" + secret, new string('x', 2001) + secret, string.Empty };

        foreach (var text in cases)
        {
            var e = Assert.Throws<ArgumentException>(() => Translate(new TypeText(text)));
            Assert.DoesNotContain("hunter2", e.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET", e.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Text_is_refused_at_the_option_limit_and_accepted_just_under_it()
    {
        var options = new BrowserPageDriverOptions { MaxTextLength = 10 };

        InputTranslator.Translate([new TypeText(new string('a', 10))], new PointerState(), options);
        Assert.Throws<ArgumentException>(() => InputTranslator.Translate([new TypeText(new string('a', 11))], new PointerState(), options));
    }

    [Fact]
    public void A_batch_that_fails_leaves_the_remembered_pointer_alone()
    {
        var state = new PointerState { X = 1, Y = 2 };

        Assert.Throws<ArgumentException>(() => InputTranslator.Translate([new PointerMove(50, 60), new KeyStroke("Bogus")], state, _options));

        Assert.Equal(1, state.X);
        Assert.Equal(2, state.Y);
    }

    [Fact]
    public void An_empty_or_oversized_batch_is_refused()
    {
        Assert.Throws<ArgumentException>(() => InputTranslator.Translate([], new PointerState(), _options));
        Assert.Throws<ArgumentException>(() => InputTranslator.Translate([.. Enumerable.Repeat<InputAction>(new PointerMove(1, 1), 201)], new PointerState(), _options));
    }

    [Fact]
    public void Every_input_action_kind_in_the_abstractions_is_handled()
    {
        var kinds = typeof(InputAction).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(InputAction))).Select(t => t.Name).Order().ToArray();
        Assert.Equal(["KeyStroke", "PointerButton", "PointerClick", "PointerMove", "PointerScroll", "TypeText"], kinds);

        Translate(new PointerMove(1, 1), new PointerButton(PointerButtonKind.Left, true), new PointerClick(1, 1), new PointerScroll(1, 1, 1, 1), new KeyStroke("Enter"), new TypeText("x"));
    }
}
