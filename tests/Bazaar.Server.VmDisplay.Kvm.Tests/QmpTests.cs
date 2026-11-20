using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm.Tests;

public class QmpTests
{
    [Fact]
    public void Screendump_json_is_exact_with_and_without_format()
    {
        Assert.Equal(
            "{\"execute\":\"screendump\",\"arguments\":{\"filename\":\"/run/bazaar/display/a.png\",\"format\":\"png\"}}",
            QmpCommands.Screendump("/run/bazaar/display/a.png", "png"));
        Assert.Equal(
            "{\"execute\":\"screendump\",\"arguments\":{\"filename\":\"/run/bazaar/display/a.ppm\"}}",
            QmpCommands.Screendump("/run/bazaar/display/a.ppm", null));
    }

    [Fact]
    public void Input_send_event_json_is_exact_for_each_event_kind()
    {
        string json = QmpCommands.InputSendEvent(
        [
            QmpEvent.Key("ret", true),
            QmpEvent.Btn("left", false),
            QmpEvent.Abs("x", 16383),
        ]);
        Assert.Equal(
            "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":["
            + "{\"type\":\"key\",\"data\":{\"down\":true,\"key\":{\"type\":\"qcode\",\"data\":\"ret\"}}},"
            + "{\"type\":\"btn\",\"data\":{\"down\":false,\"button\":\"left\"}},"
            + "{\"type\":\"abs\",\"data\":{\"axis\":\"x\",\"value\":16383}}]}}",
            json);
    }

    [Fact]
    public void Results_are_parsed_and_bad_responses_fail()
    {
        Assert.True(QmpResult.Parse("{\"return\":{},\"id\":\"libvirt-1\"}").Ok);
        QmpResult err = QmpResult.Parse("{\"error\":{\"class\":\"GenericError\",\"desc\":\"Parameter 'format' is unexpected\"}}");
        Assert.False(err.Ok);
        Assert.Contains("format", err.ErrorDescription);
        Assert.Equal(DisplayFailure.SourceFailed, Assert.Throws<DisplayException>(() => QmpResult.Parse("not json")).Failure);
        Assert.Equal(DisplayFailure.SourceFailed, Assert.Throws<DisplayException>(() => QmpResult.Parse("{\"x\":1}")).Failure);
    }

    [Fact]
    public void Image_headers_report_size()
    {
        Assert.True(ImageHeader.TryReadPng(TestImages.Png(720, 400), out int w, out int h));
        Assert.Equal((720, 400), (w, h));
        Assert.True(ImageHeader.TryReadPpm(TestImages.Ppm(800, 600), out w, out h));
        Assert.Equal((800, 600), (w, h));
        Assert.False(ImageHeader.TryReadPng([1, 2, 3], out _, out _));
        Assert.False(ImageHeader.TryReadPpm("P5\n1 1\n255\n"u8, out _, out _));
    }

    [Fact]
    public void Every_allowlisted_key_maps_to_a_qcode()
    {
        foreach (string key in DisplayKeys.All)
            Assert.True(QmpKeyTables.TryGetKey(key, out string q) && q.Length > 0, key);
        QmpKeyTables.TryGetKey("KeyA", out string a);
        QmpKeyTables.TryGetKey("Digit1", out string d);
        QmpKeyTables.TryGetKey("Enter", out string e);
        QmpKeyTables.TryGetKey("PageDown", out string pd);
        QmpKeyTables.TryGetKey("F12", out string f);
        Assert.Equal(["a", "1", "ret", "pgdn", "f12"], new[] { a, d, e, pd, f });
        Assert.False(QmpKeyTables.TryGetKey("Insert", out _));
    }

    [Fact]
    public void Every_printable_ascii_char_maps_and_shift_state_is_right()
    {
        for (char c = ' '; c <= '~'; c++)
            Assert.True(QmpKeyTables.TryGetChar(c, out string q, out _) && q.Length > 0, c.ToString());
        Assert.True(QmpKeyTables.TryGetChar('\n', out string ret, out _));
        Assert.Equal("ret", ret);
        Assert.True(QmpKeyTables.TryGetChar('\t', out string tab, out _));
        Assert.Equal("tab", tab);

        // Shifted and unshifted forms share a key and differ only in the shift flag.
        (char Plain, char Shifted)[] pairs = [('a', 'A'), ('1', '!'), ('2', '@'), ('0', ')'), ('-', '_'), ('=', '+'), ('[', '{'), (']', '}'), ('\\', '|'), (';', ':'), ('\'', '"'), (',', '<'), ('.', '>'), ('/', '?'), ('`', '~')];
        foreach ((char plain, char shifted) in pairs)
        {
            QmpKeyTables.TryGetChar(plain, out string pq, out bool ps);
            QmpKeyTables.TryGetChar(shifted, out string sq, out bool ss);
            Assert.Equal(pq, sq);
            Assert.False(ps);
            Assert.True(ss);
        }

        Assert.False(QmpKeyTables.TryGetChar('é', out _, out _));
        Assert.False(QmpKeyTables.TryGetChar('\r', out _, out _));
    }

    [Fact]
    public void Modifier_qcodes_follow_press_order()
    {
        Assert.Equal(["shift", "ctrl", "alt", "meta_l"], QmpKeyTables.ModifierQCodes(KeyModifiers.Shift | KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta));
        Assert.Empty(QmpKeyTables.ModifierQCodes(KeyModifiers.None));
    }

    [Theory]
    [InlineData("vm1", true)]
    [InlineData("a.b_c-d", true)]
    [InlineData("A", true)]
    [InlineData("", false)]
    [InlineData("-lead", false)]
    [InlineData("a b", false)]
    [InlineData("a'b", false)]
    [InlineData("a\"b", false)]
    [InlineData("a;b", false)]
    [InlineData("a$b", false)]
    [InlineData("a`b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("vm1\n", false)]
    [InlineData("..", false)]
    public void Domain_names_are_validated(string name, bool valid) => Assert.Equal(valid, DomainName.IsValid(name));

    [Fact]
    public void Domain_names_of_63_chars_pass_and_64_fail()
    {
        Assert.True(DomainName.IsValid(new string('a', 63)));
        Assert.False(DomainName.IsValid(new string('a', 64)));
        Assert.False(DomainName.IsValid(null));
    }

    [Theory]
    [InlineData(0, 1920, 0)]
    [InlineData(1919, 1920, 32767)]
    [InlineData(5000, 1920, 32767)]
    [InlineData(-5, 1920, 0)]
    [InlineData(960, 1920, 16392)]
    [InlineData(10, 1, 0)]
    public void Axis_scaling_hits_the_edges_and_clamps(int pixel, int extent, int expected) =>
        Assert.Equal(expected, KvmConsole.ScaleAxis(pixel, extent));
}
