using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm.Tests;

public class KvmConsoleTests
{
    private static readonly KvmOptions _options = new() { ScreendumpDirectory = "/dump" };

    private static (KvmConsole Console, FakeTransport Transport, FakeReader Reader) Make(KvmOptions? options = null)
    {
        var transport = new FakeTransport();
        var reader = new FakeReader();
        return (new KvmConsole(options ?? _options, transport, reader), transport, reader);
    }

    private static int EventCount(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("arguments").GetProperty("events").GetArrayLength();
    }

    [Fact]
    public void Supports_follows_enabled_and_name_validity()
    {
        (KvmConsole console, _, _) = Make();
        Assert.True(console.Supports(Targets.For("agent-vm.1")));
        Assert.False(console.Supports(Targets.For("bad name")));
        Assert.False(Make(new KvmOptions { Enabled = false }).Console.Supports(Targets.For("vm1")));
    }

    [Fact]
    public async Task Capture_asks_for_png_reads_the_file_and_deletes_it()
    {
        (KvmConsole console, FakeTransport transport, FakeReader reader) = Make();
        RawFrame frame = await console.ReadFramebufferAsync(Targets.For("vm1"), 1920, 1200, CancellationToken.None);

        Assert.Equal(RawPixelFormat.Png, frame.Format);
        Assert.Equal(reader.Content, frame.Data);
        Assert.Equal((0, 0), (frame.Width, frame.Height));
        (string domain, string json) = Assert.Single(transport.Calls);
        Assert.Equal("vm1", domain);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement args = doc.RootElement.GetProperty("arguments");
        Assert.Equal("png", args.GetProperty("format").GetString());
        string file = args.GetProperty("filename").GetString()!;
        Assert.StartsWith("/dump/", file, StringComparison.Ordinal);
        Assert.EndsWith(".png", file, StringComparison.Ordinal);
        Assert.Equal([file], reader.Deleted);
        Assert.Equal([file], reader.Read);
    }

    [Fact]
    public async Task Screendump_file_is_deleted_when_qemu_fails()
    {
        (KvmConsole console, FakeTransport transport, FakeReader reader) = Make();
        transport.Respond = _ => "{\"error\":{\"class\":\"GenericError\",\"desc\":\"no console\"}}";
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
        Assert.Single(reader.Deleted);
        Assert.Empty(reader.Read);
    }

    [Fact]
    public async Task Screendump_file_is_deleted_when_the_read_fails()
    {
        (KvmConsole console, _, FakeReader reader) = Make();
        reader.FailRead = true;
        await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None));
        Assert.Single(reader.Deleted);
    }

    [Fact]
    public async Task Screendump_file_is_deleted_when_the_transport_throws()
    {
        (KvmConsole console, FakeTransport transport, FakeReader reader) = Make();
        transport.Respond = _ => throw new DisplayException(DisplayFailure.SourceFailed, "down");
        await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None));
        Assert.Single(reader.Deleted);
    }

    [Fact]
    public async Task Old_qemu_without_format_falls_back_to_ppm_once()
    {
        (KvmConsole console, FakeTransport transport, FakeReader reader) = Make();
        transport.Respond = json => json.Contains("\"format\"", StringComparison.Ordinal)
            ? "{\"error\":{\"class\":\"GenericError\",\"desc\":\"Parameter 'format' is unexpected\"}}"
            : "{\"return\":{}}";
        reader.Content = TestImages.Ppm(640, 480);

        RawFrame frame = await console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None);

        Assert.Equal(RawPixelFormat.Ppm, frame.Format);
        Assert.Equal(2, transport.Calls.Count);
        Assert.DoesNotContain("format", transport.Calls[1].Json, StringComparison.Ordinal);
        Assert.EndsWith(".ppm", reader.Deleted[1], StringComparison.Ordinal);
        Assert.Equal(2, reader.Deleted.Count);
    }

    [Fact]
    public async Task Other_screendump_errors_do_not_retry()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        transport.Respond = _ => "{\"error\":{\"class\":\"GenericError\",\"desc\":\"permission denied\"}}";
        await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None));
        Assert.Single(transport.Calls);
    }

    [Fact]
    public async Task A_file_that_is_not_an_image_is_malformed()
    {
        (KvmConsole console, _, FakeReader reader) = Make();
        reader.Content = [1, 2, 3, 4];
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None));
        Assert.Equal(DisplayFailure.FrameMalformed, e.Failure);
    }

    [Fact]
    public async Task Invalid_names_are_rejected_before_any_call()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Targets.For("x;rm -rf"), 10, 10, CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        e = await Assert.ThrowsAsync<DisplayException>(() => console.SendInputAsync(Targets.For("a b"), [new KeyStroke("Enter")], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task A_key_stroke_presses_modifiers_first_and_releases_them_last()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.SendInputAsync(Targets.For("vm1"), [new KeyStroke("KeyC", KeyModifiers.Control | KeyModifiers.Shift)], CancellationToken.None);

        string key(string q, bool down) => "{\"type\":\"key\",\"data\":{\"down\":" + (down ? "true" : "false") + ",\"key\":{\"type\":\"qcode\",\"data\":\"" + q + "\"}}}";
        Assert.Equal(
            "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":["
            + string.Join(',', key("shift", true), key("ctrl", true), key("c", true), key("c", false), key("ctrl", false), key("shift", false)) + "]}}",
            Assert.Single(transport.Calls).Json);
    }

    [Fact]
    public async Task An_unknown_key_is_invalid_input_and_nothing_is_sent()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            console.SendInputAsync(Targets.For("vm1"), [new KeyStroke("Enter"), new KeyStroke("Insert")], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Typed_text_uses_shift_for_capitals_and_names_the_limit_for_other_characters()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.SendInputAsync(Targets.For("vm1"), [new TypeText("A!\n")], CancellationToken.None);
        string json = Assert.Single(transport.Calls).Json;
        Assert.Equal(4 + 4 + 2, EventCount(json));
        Assert.Contains("\"data\":\"ret\"", json, StringComparison.Ordinal);

        transport.Calls.Clear();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            console.SendInputAsync(Targets.For("vm1"), [new TypeText("café")], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Contains("IME", e.Message, StringComparison.Ordinal);
        Assert.Empty(transport.Calls);
    }

    [Fact]
    public async Task Long_text_is_chunked_into_commands_of_at_most_64_events()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.SendInputAsync(Targets.For("vm1"), [new TypeText(new string('x', 100))], CancellationToken.None);
        Assert.Equal(4, transport.Calls.Count);
        Assert.All(transport.Calls, c => Assert.True(EventCount(c.Json) <= 64));
        Assert.Equal(200, transport.Calls.Sum(c => EventCount(c.Json)));
    }

    [Fact]
    public async Task Pointer_input_captures_once_to_learn_the_size_then_scales_to_the_axis_range()
    {
        (KvmConsole console, FakeTransport transport, FakeReader reader) = Make();
        reader.Content = TestImages.Png(1001, 501);
        await console.SendInputAsync(Targets.For("vm1"), [new PointerClick(1000, 250, PointerButtonKind.Right, 1)], CancellationToken.None);

        Assert.Equal(3, transport.Calls.Count); // capture, press, release
        Assert.Contains("screendump", transport.Calls[0].Json, StringComparison.Ordinal);
        Assert.Equal(
            "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":["
            + "{\"type\":\"abs\",\"data\":{\"axis\":\"x\",\"value\":32767}},"
            + "{\"type\":\"abs\",\"data\":{\"axis\":\"y\",\"value\":16383}},"
            + "{\"type\":\"btn\",\"data\":{\"down\":true,\"button\":\"right\"}}]}}",
            transport.Calls[1].Json);
        Assert.Equal(
            "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":[{\"type\":\"btn\",\"data\":{\"down\":false,\"button\":\"right\"}}]}}",
            transport.Calls[2].Json);

        // The size is remembered: no second capture.
        transport.Calls.Clear();
        await console.SendInputAsync(Targets.For("vm1"), [new PointerMove(0, 0)], CancellationToken.None);
        Assert.Single(transport.Calls);
        Assert.DoesNotContain("screendump", transport.Calls[0].Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_count_repeats_and_is_bounded()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None);
        transport.Calls.Clear();
        await console.SendInputAsync(Targets.For("vm1"), [new PointerClick(1, 1, PointerButtonKind.Left, 3)], CancellationToken.None);
        Assert.Equal(6, transport.Calls.Count);

        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            console.SendInputAsync(Targets.For("vm1"), [new PointerClick(1, 1, PointerButtonKind.Left, 11)], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
    }

    [Fact]
    public async Task Scroll_uses_wheel_buttons_and_caps_the_steps()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.ReadFramebufferAsync(Targets.For("vm1"), 10, 10, CancellationToken.None);
        transport.Calls.Clear();

        await console.SendInputAsync(Targets.For("vm1"), [new PointerScroll(5, 5, 0, -3), new PointerScroll(5, 5, 0, 500)], CancellationToken.None);

        string up = transport.Calls[0].Json;
        Assert.Equal(2 + 6, EventCount(up));
        Assert.Contains("wheel-up", up, StringComparison.Ordinal);
        string down = transport.Calls[1].Json;
        Assert.Equal(2 + 40, EventCount(down));
        Assert.Contains("wheel-down", down, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_lone_pointer_button_is_one_btn_event()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        await console.SendInputAsync(Targets.For("vm1"), [new PointerButton(PointerButtonKind.Middle, true)], CancellationToken.None);
        Assert.Equal(
            "{\"execute\":\"input-send-event\",\"arguments\":{\"events\":[{\"type\":\"btn\",\"data\":{\"down\":true,\"button\":\"middle\"}}]}}",
            Assert.Single(transport.Calls).Json);
    }

    [Fact]
    public async Task A_qmp_error_on_input_is_a_source_failure()
    {
        (KvmConsole console, FakeTransport transport, _) = Make();
        transport.Respond = _ => "{\"error\":{\"class\":\"GenericError\",\"desc\":\"nope\"}}";
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.SendInputAsync(Targets.For("vm1"), [new KeyStroke("Enter")], CancellationToken.None));
        Assert.Equal(DisplayFailure.SourceFailed, e.Failure);
    }
}
