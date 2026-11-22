using System.Text.Json;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.HyperV.Tests;

internal sealed class FakeRunner : IHyperVScriptRunner
{
    public List<(string Script, IReadOnlyDictionary<string, string> Parameters)> Calls { get; } = [];

    public Func<string, string> Output { get; set; } = _ => string.Empty;

    public Task<string> RunAsync(string script, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        Calls.Add((script, parameters));
        return Task.FromResult(Output(script));
    }
}

public class HyperVConsoleTests
{
    private static readonly DisplayTarget _target = new("agent-1", Guid.NewGuid(), DisplayGuestOs.Windows, "agent-vm.1");

    private static (HyperVConsole Console, FakeRunner Runner) Make(HyperVOptions? options = null, bool windows = true)
    {
        var runner = new FakeRunner();
        return (new HyperVConsole(options ?? new HyperVOptions(), runner, () => windows), runner);
    }

    private static DisplayTarget Named(string name) => _target with { BackendRef = name };

    private static string ThumbnailOutput(int w, int h, int bytes) => $"{w} {h}\n{Convert.ToBase64String(new byte[bytes])}\n";

    private static JsonElement[] Steps(FakeRunner runner, int run)
    {
        using JsonDocument doc = JsonDocument.Parse(runner.Calls[run].Parameters["Steps"]);
        return [.. doc.RootElement.EnumerateArray().Select(e => e.Clone())];
    }

    [Fact]
    public void Supports_needs_enabled_a_valid_name_and_windows()
    {
        Assert.True(Make().Console.Supports(_target));
        Assert.False(Make(windows: false).Console.Supports(_target));
        Assert.False(Make(new HyperVOptions { Enabled = false }).Console.Supports(_target));
        Assert.False(Make().Console.Supports(Named("bad name")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("a\"b")]
    [InlineData("a;b")]
    [InlineData("a$b")]
    [InlineData("a`b")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("-x")]
    [InlineData("x\n")]
    public async Task Hostile_names_are_rejected_and_nothing_runs(string name)
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Named(name), 100, 100, CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        e = await Assert.ThrowsAsync<DisplayException>(() => console.SendInputAsync(Named(name), [new KeyStroke("Enter")], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task A_name_over_63_characters_is_rejected()
    {
        (HyperVConsole console, _) = Make();
        await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(Named(new string('a', 64)), 10, 10, CancellationToken.None));
    }

    [Fact]
    public async Task Capture_passes_the_name_as_data_and_the_script_is_the_constant()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        runner.Output = _ => ThumbnailOutput(1280, 800, 1280 * 800 * 2);

        RawFrame frame = await console.ReadFramebufferAsync(_target, 1920, 1200, CancellationToken.None);

        Assert.Equal(RawPixelFormat.Rgb565, frame.Format);
        Assert.Equal((1280, 800), (frame.Width, frame.Height));
        Assert.Equal(1280 * 800 * 2, frame.Data.Length);
        (string script, IReadOnlyDictionary<string, string> p) = Assert.Single(runner.Calls);
        Assert.Same(HyperVScripts.Thumbnail, script);
        Assert.Equal("agent-vm.1", p["Name"]);
        Assert.Equal("1280", p["Width"]);
        Assert.Equal("800", p["Height"]);
        Assert.DoesNotContain("agent-vm.1", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requested_size_is_the_smaller_of_the_call_and_the_options()
    {
        (HyperVConsole console, FakeRunner runner) = Make(new HyperVOptions { ThumbnailWidth = 1000, ThumbnailHeight = 900 });
        runner.Output = _ => ThumbnailOutput(640, 480, 640 * 480 * 2);
        await console.ReadFramebufferAsync(_target, 640, 480, CancellationToken.None);
        Assert.Equal("640", runner.Calls[0].Parameters["Width"]);
        Assert.Equal("480", runner.Calls[0].Parameters["Height"]);

        runner.Output = _ => ThumbnailOutput(1000, 900, 1000 * 900 * 2);
        await console.ReadFramebufferAsync(_target, 5000, 5000, CancellationToken.None);
        Assert.Equal("1000", runner.Calls[1].Parameters["Width"]);
        Assert.Equal("900", runner.Calls[1].Parameters["Height"]);
    }

    [Fact]
    public async Task A_length_mismatch_is_a_malformed_frame()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        runner.Output = _ => ThumbnailOutput(1280, 800, 1280 * 800 * 2 - 2);
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(_target, 1920, 1200, CancellationToken.None));
        Assert.Equal(DisplayFailure.FrameMalformed, e.Failure);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1280 800\n")]
    [InlineData("big small\nAAAA\n")]
    [InlineData("640 480\nAAAA\n")]
    [InlineData("1280 800\n!!!not base64!!!\n")]
    public async Task Garbage_output_is_a_malformed_frame(string output)
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        runner.Output = _ => output;
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.ReadFramebufferAsync(_target, 1920, 1200, CancellationToken.None));
        Assert.Equal(DisplayFailure.FrameMalformed, e.Failure);
    }

    [Fact]
    public async Task Typed_text_is_carried_in_the_steps_json_not_the_script()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        const string text = "Name'; Remove-Item $env:TEMP `x \"q\"";
        await console.SendInputAsync(_target, [new TypeText(text)], CancellationToken.None);

        (string script, IReadOnlyDictionary<string, string> p) = Assert.Single(runner.Calls);
        Assert.Same(HyperVScripts.Keys, script);
        Assert.DoesNotContain("Remove-Item", script, StringComparison.Ordinal);
        JsonElement step = Assert.Single(Steps(runner, 0));
        Assert.Equal("type", step.GetProperty("op").GetString());
        Assert.Equal(text, step.GetProperty("text").GetString());
        Assert.Equal("agent-vm.1", p["Name"]);
    }

    [Fact]
    public async Task Text_is_chunked_at_512_and_newline_is_a_separate_enter_step()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        await console.SendInputAsync(_target, [new TypeText(new string('a', 1100) + "\nb")], CancellationToken.None);

        JsonElement[] steps = Steps(runner, 0);
        Assert.Equal(["type", "type", "type", "key", "type"], steps.Select(s => s.GetProperty("op").GetString()));
        Assert.Equal(512, steps[0].GetProperty("text").GetString()!.Length);
        Assert.Equal(512, steps[1].GetProperty("text").GetString()!.Length);
        Assert.Equal(76, steps[2].GetProperty("text").GetString()!.Length);
        Assert.Equal(0x0D, steps[3].GetProperty("code").GetInt32());
        Assert.Equal("b", steps[4].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("café")]
    [InlineData("a\rb")]
    [InlineData("a\u0007b")]
    public async Task Non_ascii_and_control_text_is_invalid_input(string text)
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() => console.SendInputAsync(_target, [new TypeText(text)], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Every_allowlisted_key_has_a_virtual_key_code()
    {
        foreach (string key in DisplayKeys.All)
            Assert.True(HyperVKeyTable.TryGetKey(key, out int code) && code > 0, key);
        HyperVKeyTable.TryGetKey("KeyA", out int a);
        HyperVKeyTable.TryGetKey("Digit0", out int d0);
        HyperVKeyTable.TryGetKey("F1", out int f1);
        HyperVKeyTable.TryGetKey("F12", out int f12);
        HyperVKeyTable.TryGetKey("Enter", out int enter);
        HyperVKeyTable.TryGetKey("ArrowLeft", out int left);
        Assert.Equal([0x41, 0x30, 0x70, 0x7B, 0x0D, 0x25], new[] { a, d0, f1, f12, enter, left });
        Assert.False(HyperVKeyTable.TryGetKey("Insert", out _));
    }

    [Fact]
    public async Task A_key_stroke_presses_modifiers_first_and_releases_them_last()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        await console.SendInputAsync(_target, [new KeyStroke("KeyA", KeyModifiers.Control | KeyModifiers.Meta)], CancellationToken.None);

        JsonElement[] steps = Steps(runner, 0);
        Assert.Equal(["press", "press", "key", "release", "release"], steps.Select(s => s.GetProperty("op").GetString()));
        Assert.Equal([0x11, 0x5B, 0x41, 0x5B, 0x11], steps.Select(s => s.GetProperty("code").GetInt32()));
    }

    [Fact]
    public async Task Pointer_actions_become_move_click_and_scroll_steps_in_one_mouse_run()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        await console.SendInputAsync(
            _target,
            [new PointerMove(5, 6), new PointerClick(10, 20, PointerButtonKind.Right, 2), new PointerScroll(1, 2, 0, -3)],
            CancellationToken.None);

        (string script, _) = Assert.Single(runner.Calls);
        Assert.Same(HyperVScripts.Mouse, script);
        JsonElement[] steps = Steps(runner, 0);
        Assert.Equal(["move", "move", "click", "click", "move", "scroll"], steps.Select(s => s.GetProperty("op").GetString()));
        Assert.Equal(5, steps[0].GetProperty("x").GetInt32());
        Assert.Equal(20, steps[1].GetProperty("y").GetInt32());
        Assert.Equal(2, steps[2].GetProperty("button").GetInt32());
        Assert.Equal(-3, steps[5].GetProperty("delta").GetInt32());
    }

    [Theory]
    [InlineData(PointerButtonKind.Left, 1)]
    [InlineData(PointerButtonKind.Right, 2)]
    [InlineData(PointerButtonKind.Middle, 3)]
    public async Task Button_indexes_follow_the_wmi_numbering(PointerButtonKind kind, int index)
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        await console.SendInputAsync(_target, [new PointerClick(1, 1, kind)], CancellationToken.None);
        Assert.Equal(index, Steps(runner, 0)[1].GetProperty("button").GetInt32());
    }

    [Fact]
    public async Task Mixed_batches_split_into_runs_per_device_in_order()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        await console.SendInputAsync(
            _target,
            [new KeyStroke("Enter"), new TypeText("x"), new PointerMove(1, 1), new KeyStroke("Tab")],
            CancellationToken.None);
        Assert.Equal([HyperVScripts.Keys, HyperVScripts.Mouse, HyperVScripts.Keys], runner.Calls.Select(c => c.Script));
    }

    [Fact]
    public async Task A_lone_pointer_button_and_oversized_click_counts_are_invalid_and_run_nothing()
    {
        (HyperVConsole console, FakeRunner runner) = Make();
        DisplayException e = await Assert.ThrowsAsync<DisplayException>(() =>
            console.SendInputAsync(_target, [new KeyStroke("Enter"), new PointerButton(PointerButtonKind.Left, true)], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        e = await Assert.ThrowsAsync<DisplayException>(() =>
            console.SendInputAsync(_target, [new PointerClick(1, 1, PointerButtonKind.Left, 99)], CancellationToken.None));
        Assert.Equal(DisplayFailure.InvalidInput, e.Failure);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public void Scripts_are_constants_that_read_only_the_parameter_object()
    {
        foreach (string script in new[] { HyperVScripts.Thumbnail, HyperVScripts.Keys, HyperVScripts.Mouse })
        {
            Assert.Contains("$env:BAZAAR_DISPLAY_PARAMS", script, StringComparison.Ordinal);
            Assert.Contains("root\\virtualization\\v2", script, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", script, StringComparison.Ordinal);
        }

        Assert.Contains("GetVirtualSystemThumbnailImage", HyperVScripts.Thumbnail, StringComparison.Ordinal);
        Assert.Contains("TypeText", HyperVScripts.Keys, StringComparison.Ordinal);
        Assert.Contains("SetAbsolutePosition", HyperVScripts.Mouse, StringComparison.Ordinal);
    }
}
