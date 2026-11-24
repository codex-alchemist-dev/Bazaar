using System.Text.Json;
using Bazaar.Client.Athanor.Gtk.Helper;

namespace Bazaar.Client.Athanor.Gtk.Tests;

public sealed class HelperCoreTests
{
    private sealed class FakeSurface : IEditorSurface
    {
        public List<string> Posted { get; } = [];

        public List<WindowPlacement> Placements { get; } = [];

        public int QuitCount { get; private set; }

        public void Dispatch(Action action) => action();

        public void PostToPage(string json) => Posted.Add(json);

        public void ApplyWindow(WindowPlacement placement) => Placements.Add(placement);

        public void Quit() => QuitCount++;
    }

    private static (HelperCore Core, FakeSurface Surface, StringWriter Output) Create(long maxLine = 1024 * 1024)
    {
        var surface = new FakeSurface();
        var output = new StringWriter { NewLine = "\n" };
        return (new HelperCore(surface, output, maxLine), surface, output);
    }

    private static string[] Lines(StringWriter output) =>
        output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public void Hello_is_one_line_with_protocol_webkit_sandbox_and_pid()
    {
        (HelperCore core, _, StringWriter output) = Create();

        core.SendHello("2.52.6", sandbox: true, pid: 77);

        using JsonDocument d = JsonDocument.Parse(Assert.Single(Lines(output)));
        Assert.Equal("helper.hello", d.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, d.RootElement.GetProperty("protocol").GetInt32());
        Assert.Equal("2.52.6", d.RootElement.GetProperty("webkit").GetString());
        Assert.True(d.RootElement.GetProperty("sandbox").GetBoolean());
        Assert.Equal(77, d.RootElement.GetProperty("pid").GetInt32());
    }

    [Fact]
    public void A_ping_is_answered_with_a_pong_carrying_the_same_id()
    {
        (HelperCore core, FakeSurface surface, StringWriter output) = Create();

        core.HandleLine("""{"type":"helper.ping","id":12}""");

        using JsonDocument d = JsonDocument.Parse(Assert.Single(Lines(output)));
        Assert.Equal("helper.pong", d.RootElement.GetProperty("type").GetString());
        Assert.Equal(12, d.RootElement.GetProperty("id").GetInt64());
        Assert.Empty(surface.Posted);
    }

    [Fact]
    public void Bridge_messages_go_to_the_page_unchanged()
    {
        (HelperCore core, FakeSurface surface, _) = Create();
        const string open = """{"type":"open","seq":1,"documentId":"ws:a","content":"x\n</script>","revision":"r","languageId":"plaintext","readOnly":false}""";

        core.HandleLine(open);

        Assert.Equal(open, Assert.Single(surface.Posted));
    }

    [Fact]
    public void A_window_message_is_applied_and_clamped()
    {
        (HelperCore core, FakeSurface surface, _) = Create();

        core.HandleLine("""{"type":"helper.window","x":5,"y":6,"width":900,"height":10,"visible":false,"title":"T"}""");

        WindowPlacement p = Assert.Single(surface.Placements);
        Assert.Equal(5, p.X);
        Assert.Equal(6, p.Y);
        Assert.Equal(900, p.Width);
        Assert.Equal(100, p.Height);
        Assert.False(p.Visible);
        Assert.Equal("T", p.Title);
    }

    [Fact]
    public void Shutdown_quits_the_surface()
    {
        (HelperCore core, FakeSurface surface, _) = Create();

        core.HandleLine("""{"type":"helper.shutdown"}""");

        Assert.Equal(1, surface.QuitCount);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"seq":1}""")]
    [InlineData("""{"type":"helper.nonsense"}""")]
    public void A_bad_line_is_fatal(string line)
    {
        (HelperCore core, FakeSurface surface, StringWriter output) = Create();

        core.HandleLine(line);

        using JsonDocument d = JsonDocument.Parse(Assert.Single(Lines(output)));
        Assert.Equal("helper.fatal", d.RootElement.GetProperty("type").GetString());
        Assert.Equal(1, surface.QuitCount);
        Assert.Empty(surface.Posted);
    }

    [Fact]
    public void An_oversize_line_from_the_client_is_fatal()
    {
        (HelperCore core, FakeSurface surface, StringWriter output) = Create(maxLine: 64);

        core.HandleLine("{\"type\":\"open\",\"seq\":1,\"content\":\"" + new string('x', 200) + "\"}");

        Assert.Contains("line_too_large", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, surface.QuitCount);
    }

    [Fact]
    public void A_page_message_is_compacted_to_a_single_line()
    {
        (HelperCore core, _, StringWriter output) = Create();

        core.OnPageMessage("{\n  \"type\": \"ready\",\n  \"seq\": 1\n}");

        Assert.Equal("""{"type":"ready","seq":1}""", Assert.Single(Lines(output)));
    }

    [Fact]
    public void A_page_message_that_is_not_json_is_fatal()
    {
        (HelperCore core, FakeSurface surface, StringWriter output) = Create();

        core.OnPageMessage("<html>");

        Assert.Contains("page_message_malformed", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, surface.QuitCount);
    }

    [Fact]
    public void Window_closed_is_reported()
    {
        (HelperCore core, _, StringWriter output) = Create();

        core.OnWindowClosed();

        Assert.Contains("helper.windowClosed", Assert.Single(Lines(output)), StringComparison.Ordinal);
    }

    [Fact]
    public void End_of_input_quits_after_handling_every_line()
    {
        (HelperCore core, FakeSurface surface, StringWriter output) = Create();
        using var input = new StringReader("{\"type\":\"helper.ping\",\"id\":1}\n\n{\"type\":\"focus\",\"seq\":1}\n");

        core.RunInputLoop(input);

        Assert.Single(Lines(output));
        Assert.Single(surface.Posted);
        Assert.Equal(1, surface.QuitCount);
    }

    [Fact]
    public void Options_parse_the_command_line()
    {
        Assert.True(HelperOptions.TryParse(["--bundle", "/tmp/b", "--title", "T", "--width", "800", "--height", "600", "--allow-no-sandbox"], out HelperOptions? o, out string? error));
        Assert.Null(error);
        Assert.Equal(Path.GetFullPath("/tmp/b"), o!.BundleDirectory);
        Assert.Equal("host.html", o.EntryFile);
        Assert.Equal("T", o.Title);
        Assert.Equal(800, o.Width);
        Assert.Equal(600, o.Height);
        Assert.True(o.AllowNoSandbox);
    }

    [Theory]
    [InlineData("--width", "800")]
    [InlineData("--bundle")]
    [InlineData("--bundle", "/b", "--entry", "../x.html")]
    [InlineData("--bundle", "/b", "--entry", "/etc/passwd")]
    [InlineData("--bundle", "/b", "--width", "5")]
    [InlineData("--bundle", "/b", "--width", "-1")]
    [InlineData("--bundle", "/b", "--frobnicate")]
    public void Options_reject_bad_command_lines(params string[] args)
    {
        Assert.False(HelperOptions.TryParse(args, out HelperOptions? o, out string? error));
        Assert.Null(o);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void The_sandbox_must_be_on_unless_the_operator_allows_otherwise()
    {
        SandboxDecision ok = SandboxPolicy.Evaluate(allowNoSandbox: false, disableSwitchSet: false, bubblewrapFound: true);
        Assert.True(ok.CanStart);
        Assert.True(ok.SandboxActive);

        SandboxDecision disabled = SandboxPolicy.Evaluate(false, disableSwitchSet: true, bubblewrapFound: true);
        Assert.False(disabled.CanStart);
        Assert.Equal(HelperExitCodes.SandboxDisabled, disabled.ExitCode);

        SandboxDecision noBwrap = SandboxPolicy.Evaluate(false, false, bubblewrapFound: false);
        Assert.False(noBwrap.CanStart);
        Assert.Equal(HelperExitCodes.SandboxUnavailable, noBwrap.ExitCode);

        SandboxDecision allowed = SandboxPolicy.Evaluate(allowNoSandbox: true, disableSwitchSet: true, bubblewrapFound: false);
        Assert.True(allowed.CanStart);
        Assert.False(allowed.SandboxActive);

        SandboxDecision allowedButAvailable = SandboxPolicy.Evaluate(true, false, true);
        Assert.True(allowedButAvailable.SandboxActive);
    }

    [Fact]
    public void Bubblewrap_is_found_on_the_path_only_when_present()
    {
        string dir = Directory.CreateTempSubdirectory("athanor-bwrap").FullName;
        try
        {
            Assert.False(SandboxPolicy.FindBubblewrap(dir));
            File.WriteAllText(Path.Combine(dir, "bwrap"), string.Empty);
            Assert.True(SandboxPolicy.FindBubblewrap("/nonexistent" + Path.PathSeparator + dir));
            Assert.False(SandboxPolicy.FindBubblewrap(null));
            Assert.False(SandboxPolicy.FindBubblewrap(string.Empty));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
