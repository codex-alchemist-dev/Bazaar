using System.Diagnostics;

namespace Bazaar.Client.DefaultApp.Linux.Tests;

/// <summary>Records every call and answers from a script. Models xdg-mime's per-type default in memory.</summary>
internal sealed class FakeRunner : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];

    public Dictionary<string, string> Defaults { get; } = [];

    public bool Missing { get; set; }

    public bool IgnoreSet { get; set; }

    public int SetExit { get; set; }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (Missing)
        {
            throw new IOException("not found");
        }

        var a = request.Arguments;
        if (request.FileName == "xdg-mime" && a[0] == "default")
        {
            if (SetExit == 0 && !IgnoreSet)
            {
                Defaults[a[2]] = a[1];
            }

            return Task.FromResult(new ProcessResult(SetExit, "", ""));
        }

        if (request.FileName == "xdg-mime" && a[0] == "query")
        {
            return Task.FromResult(new ProcessResult(0, Defaults.GetValueOrDefault(a[2], "") + "\n", ""));
        }

        return Task.FromResult(new ProcessResult(0, "", ""));
    }
}

public sealed class RegistrarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bazaar-defaultapp").FullName;
    private readonly FakeRunner _runner = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private XdgDefaultAppRegistrar Make(string exec = "/opt/bazaar/Bazaar.Client.Linux", string? desktop = null) =>
        new(
            new DefaultAppSettings { ExecPath = exec },
            _runner,
            name => name switch
            {
                "XDG_DATA_HOME" => _dir,
                "XDG_CURRENT_DESKTOP" => desktop,
                _ => null,
            });

    [Fact]
    public async Task Register_writes_entry_runs_tools_and_reports_registered()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux-only behaviour.");
        var reg = Make();
        var status = await reg.RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.Registered, status.State);
        Assert.Null(status.Guidance);
        string text = File.ReadAllText(Path.Combine(_dir, "applications", "bazaar.desktop"));
        Assert.Contains("MimeType=x-scheme-handler/bazaar;", text);
        Assert.Contains("Exec=/opt/bazaar/Bazaar.Client.Linux %u", text);
        Assert.Contains(_runner.Calls, c => c.FileName == "xdg-mime" && c.Arguments.SequenceEqual(["default", "bazaar.desktop", "x-scheme-handler/bazaar"]));
        Assert.Contains(_runner.Calls, c => c.FileName == "update-desktop-database" && c.Arguments.SequenceEqual([Path.Combine(_dir, "applications")]));
    }

    [Fact]
    public async Task Register_is_idempotent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux-only behaviour.");
        var reg = Make();
        await reg.RegisterAsync(TestContext.Current.CancellationToken);
        string file = reg.DesktopFilePath;
        var first = File.GetLastWriteTimeUtc(file);
        string text = File.ReadAllText(file);
        File.SetLastWriteTimeUtc(file, first.AddHours(-1));
        var status = await reg.RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.Registered, status.State);
        Assert.Equal(text, File.ReadAllText(file));
        Assert.Equal(first.AddHours(-1), File.GetLastWriteTimeUtc(file));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "applications"), "*.tmp"));
    }

    [Fact]
    public async Task Register_updates_a_changed_exec_path()
    {
        await Make().RegisterAsync(TestContext.Current.CancellationToken);
        var reg = Make("/opt/new/Bazaar.Client.Linux");
        await reg.RegisterAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Exec=/opt/new/Bazaar.Client.Linux %u", File.ReadAllText(reg.DesktopFilePath));
    }

    [Fact]
    public async Task Register_never_claims_a_default_the_desktop_does_not_report()
    {
        _runner.IgnoreSet = true;
        var status = await Make(desktop: "GNOME").RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Contains("Default Apps", status.Guidance);
    }

    [Fact]
    public async Task Query_reports_another_handler_as_not_default()
    {
        _runner.Defaults["x-scheme-handler/bazaar"] = "other.desktop";
        var status = await Make(desktop: "KDE").QueryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Contains("System Settings", status.Guidance);
    }

    [Fact]
    public async Task Missing_tool_is_unavailable()
    {
        _runner.Missing = true;
        Assert.Equal(DefaultAppState.Unavailable, (await Make().QueryAsync(TestContext.Current.CancellationToken)).State);
        Assert.Equal(DefaultAppState.Unavailable, (await Make().RegisterAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Failed_xdg_mime_set_is_unavailable()
    {
        _runner.SetExit = 4;
        Assert.Equal(DefaultAppState.Unavailable, (await Make().RegisterAsync(TestContext.Current.CancellationToken)).State);
    }

    [Fact]
    public async Task Unregister_removes_own_entry_and_is_repeatable()
    {
        var reg = Make();
        await reg.RegisterAsync(TestContext.Current.CancellationToken);
        await reg.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.False(File.Exists(reg.DesktopFilePath));
        var again = await reg.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DefaultAppState.NotDefault, again.State);
    }

    [Fact]
    public async Task Unregister_leaves_a_foreign_entry_alone()
    {
        var reg = Make();
        Directory.CreateDirectory(reg.ApplicationsDirectory);
        File.WriteAllText(reg.DesktopFilePath, "[Desktop Entry]\nName=Mine\n");
        await reg.UnregisterAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(reg.DesktopFilePath));
    }

    [Fact]
    public void Data_home_falls_back_to_home_local_share()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Linux-only behaviour.");
        var reg = new XdgDefaultAppRegistrar(
            new DefaultAppSettings { ExecPath = "/opt/b" },
            _runner,
            n => n == "HOME" ? "/home/u" : null);
        Assert.Equal("/home/u/.local/share/applications", reg.ApplicationsDirectory);
    }

    [Theory]
    [InlineData("relative/bazaar")]
    [InlineData("")]
    [InlineData("/opt/ba\"zaar")]
    [InlineData("/opt/ba'zaar")]
    [InlineData("/opt/ba\nzaar")]
    [InlineData("/opt/ba\rzaar")]
    [InlineData("/opt/ba\0zaar")]
    public void Bad_exec_paths_are_rejected(string path) =>
        Assert.Throws<ArgumentException>(() => new XdgDefaultAppRegistrar(new DefaultAppSettings { ExecPath = path }, _runner));

    [Theory]
    [InlineData("../evil.desktop")]
    [InlineData("a/b.desktop")]
    [InlineData("noext")]
    public void Bad_desktop_ids_are_rejected(string id) =>
        Assert.Throws<ArgumentException>(() =>
            new XdgDefaultAppRegistrar(new DefaultAppSettings { ExecPath = "/opt/b", DesktopFileId = id }, _runner));

    [Theory]
    [InlineData("scheme")]
    [InlineData("x-scheme-handler/bazaar;evil")]
    public void Bad_mime_types_are_rejected(string mime) =>
        Assert.Throws<ArgumentException>(() =>
            new XdgDefaultAppRegistrar(new DefaultAppSettings { ExecPath = "/opt/b", MimeTypes = [mime] }, _runner));

    [Theory]
    [InlineData("/opt/bazaar/client", "/opt/bazaar/client %u")]
    [InlineData("/opt/my app/client", "\"/opt/my app/client\" %u")]
    [InlineData("/opt/a$b/c", "\"/opt/a\\$b/c\" %u")]
    [InlineData("/opt/a`b", "\"/opt/a\\`b\" %u")]
    [InlineData("/opt/a\\b", "\"/opt/a\\\\b\" %u")]
    [InlineData("/opt/50%/c", "/opt/50%%/c %u")]
    public void Exec_is_quoted_per_the_spec(string path, string expected) =>
        Assert.Equal(expected, DesktopEntry.ExecLine(path));

    [Fact]
    public void Rendered_exec_doubles_backslashes_for_the_string_level()
    {
        string text = DesktopEntry.Render(new DefaultAppSettings { ExecPath = "/opt/a\\b" });
        Assert.Contains("Exec=\"/opt/a\\\\\\\\b\" %u\n", text);
    }
}

public sealed class RealXdgMimeTests
{
    private static bool ToolPresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo("xdg-mime", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task Real_xdg_mime_round_trip_in_a_temp_home()
    {
        Assert.SkipUnless(ToolPresent(), "Needs Linux with xdg-mime installed.");
        string home = Directory.CreateTempSubdirectory("bazaar-xdg-home").FullName;
        try
        {
            string data = Path.Combine(home, "share");
            var runner = new EnvRunner(home, data);
            var reg = new XdgDefaultAppRegistrar(
                new DefaultAppSettings { ExecPath = "/opt/bazaar/Bazaar.Client.Linux", DataHome = data },
                runner);
            var status = await reg.RegisterAsync(TestContext.Current.CancellationToken);
            Assert.True(status.State is DefaultAppState.Registered or DefaultAppState.NotDefault, status.Message);
            if (status.State == DefaultAppState.Registered)
            {
                await reg.UnregisterAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>Runs the real tools with HOME and the config dir pointed at a temp directory so the user's settings are untouched.</summary>
    private sealed class EnvRunner(string home, string data) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
        {
            var env = new Dictionary<string, string>(request.Environment)
            {
                ["HOME"] = home,
                ["XDG_DATA_HOME"] = data,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, "config"),
            };
            return new SystemProcessRunner().RunAsync(request with { Environment = env }, ct);
        }
    }
}
