using System.Diagnostics;

namespace Bazaar.Client.DefaultApp.Linux.Tests;

/// <summary>Answers gsettings and kreadconfig reads from a script and records every call.</summary>
internal sealed class ReadOnlyToolRunner : IProcessRunner
{
    public List<ProcessRequest> Calls { get; } = [];

    public string? GnomeExec { get; set; }

    public string? KdeService { get; set; }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        Calls.Add(request);
        if (request.FileName == "gsettings" && GnomeExec is not null)
        {
            return Task.FromResult(new ProcessResult(0, $"'{GnomeExec}'\n", ""));
        }

        if (request.FileName == "kreadconfig6" && KdeService is not null)
        {
            return Task.FromResult(new ProcessResult(0, KdeService + "\n", ""));
        }

        throw new IOException("not found");
    }
}

public sealed class TerminalRegistrarTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bazaar-terminal").FullName;
    private readonly ReadOnlyToolRunner _runner = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Data => Path.Combine(_dir, "data");

    private string Config => Path.Combine(_dir, "config");

    private string SystemConfig => Path.Combine(_dir, "etc");

    private string UserList => Path.Combine(Config, "xdg-terminals.list");

    private XdgDefaultTerminalRegistrar Make(string exec = "/opt/bazaar/Bazaar.Terminal", string? desktop = null) =>
        new(
            new DefaultTerminalSettings { ExecPath = exec, DataHome = Data, ConfigHome = Config, ConfigDirs = SystemConfig },
            _runner,
            name => name == "XDG_CURRENT_DESKTOP" ? desktop : null);

    [Fact]
    public async Task Register_writes_a_separate_managed_terminal_entry_and_the_list()
    {
        var status = await Make().RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.Registered, status.State);
        Assert.Null(status.Guidance);
        string text = File.ReadAllText(Path.Combine(Data, "applications", "bazaar-terminal.desktop"));
        Assert.Contains("Categories=TerminalEmulator;System;\n", text);
        Assert.Contains("X-Bazaar-Managed=true\n", text);
        Assert.Contains("Exec=/opt/bazaar/Bazaar.Terminal\n", text);
        Assert.DoesNotContain("MimeType", text, StringComparison.Ordinal);
        Assert.Equal("bazaar-terminal.desktop\n", File.ReadAllText(UserList));
    }

    [Fact]
    public async Task Register_runs_no_writing_tool()
    {
        await Make().RegisterAsync(TestContext.Current.CancellationToken);

        Assert.All(_runner.Calls, c => Assert.True(c.FileName is "gsettings" or "kreadconfig6" or "kreadconfig5"));
        Assert.DoesNotContain(_runner.Calls, c => c.Arguments.Contains("set") || c.FileName.Contains("kwrite", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Register_keeps_the_users_lines_puts_bazaar_first_and_is_idempotent()
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(UserList, "# mine\nfoot.desktop\n-xterm.desktop\nbazaar-terminal.desktop\n");
        var reg = Make();

        await reg.RegisterAsync(TestContext.Current.CancellationToken);
        string once = File.ReadAllText(UserList);
        await reg.RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal("# mine\nbazaar-terminal.desktop\nfoot.desktop\n-xterm.desktop\n", once);
        Assert.Equal(once, File.ReadAllText(UserList));
    }

    [Fact]
    public async Task A_desktop_specific_list_that_names_another_terminal_wins_and_is_reported()
    {
        Directory.CreateDirectory(Config);
        File.WriteAllText(Path.Combine(Config, "gnome-xdg-terminals.list"), "org.gnome.Ptyxis.desktop\n");
        var status = await Make(desktop: "GNOME").RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Contains("gnome-xdg-terminals.list names org.gnome.Ptyxis.desktop first", status.Message, StringComparison.Ordinal);
        Assert.Contains("sudo update-alternatives --set x-terminal-emulator '/opt/bazaar/Bazaar.Terminal'", status.Guidance, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_with_no_list_is_not_default_and_changes_nothing()
    {
        var status = await Make().QueryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Contains("undefined order", status.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Config));
        Assert.False(Directory.Exists(Data));
    }

    [Fact]
    public async Task Query_reads_a_system_list_and_ignores_comments_exclusions_and_actions()
    {
        Directory.CreateDirectory(SystemConfig);
        File.WriteAllText(Path.Combine(SystemConfig, "xdg-terminals.list"), "# c\n-bad.desktop\n/directive\nkitty.desktop:new-window\n");
        var status = await Make().QueryAsync(TestContext.Current.CancellationToken);

        Assert.Contains("names kitty.desktop first", status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Query_mentions_gnome_and_kde_values_without_deciding_from_them()
    {
        _runner.GnomeExec = "gnome-terminal";
        _runner.KdeService = "org.kde.konsole.desktop";
        var status = await Make().QueryAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Contains("GNOME's legacy exec key reads gnome-terminal (current GNOME ignores it)", status.Message, StringComparison.Ordinal);
        Assert.Contains("KDE's TerminalService reads org.kde.konsole.desktop", status.Message, StringComparison.Ordinal);
        Assert.Contains(_runner.Calls, c => c.FileName == "gsettings" && c.Arguments.SequenceEqual(["get", "org.gnome.desktop.default-applications.terminal", "exec"]));
    }

    [Fact]
    public async Task Unregister_removes_only_bazaars_lines_and_its_own_entry()
    {
        var reg = Make();
        Directory.CreateDirectory(Config);
        File.WriteAllText(UserList, "foot.desktop\n");
        await reg.RegisterAsync(TestContext.Current.CancellationToken);

        var status = await reg.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppState.NotDefault, status.State);
        Assert.Equal("foot.desktop\n", File.ReadAllText(UserList));
        Assert.False(File.Exists(Path.Combine(Data, "applications", "bazaar-terminal.desktop")));
    }

    [Fact]
    public async Task Unregister_deletes_a_list_it_created_and_is_safe_to_repeat()
    {
        var reg = Make();
        await reg.RegisterAsync(TestContext.Current.CancellationToken);
        await reg.UnregisterAsync(TestContext.Current.CancellationToken);
        await reg.UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.False(File.Exists(UserList));
    }

    [Fact]
    public async Task Unregister_leaves_a_foreign_entry_alone()
    {
        string path = Path.Combine(Data, "applications", "bazaar-terminal.desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "[Desktop Entry]\nName=Mine\n");

        var status = await Make().UnregisterAsync(TestContext.Current.CancellationToken);

        Assert.Contains("left alone", status.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Entry_escapes_a_path_with_reserved_characters_and_percent()
    {
        string text = DesktopEntry.RenderTerminal(new DefaultTerminalSettings { ExecPath = "/opt/my app/100%/t$" });

        Assert.Contains("Exec=\"/opt/my app/100%%/t\\\\$\"\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("%u", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/opt/a\"b")]
    [InlineData("/opt/a\nb")]
    public void Unsafe_exec_paths_are_refused(string exec) =>
        Assert.Throws<ArgumentException>(() => new XdgDefaultTerminalRegistrar(new DefaultTerminalSettings { ExecPath = exec }, _runner));

    [Fact]
    public void Unsafe_config_home_and_desktop_id_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new DefaultTerminalSettings { ExecPath = "/a", ConfigHome = "rel" }.Validate());
        Assert.Throws<ArgumentException>(() => new DefaultTerminalSettings { ExecPath = "/a", DesktopFileId = "../x.desktop" }.Validate());
    }
}

public sealed class RealTerminalToolTests
{
    private static bool ToolPresent(string tool)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "--help") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit(5000);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [Fact]
    public async Task Real_gsettings_read_does_not_throw_and_register_stays_in_the_temp_dirs()
    {
        Assert.SkipUnless(ToolPresent("gsettings"), "Needs Linux with gsettings installed.");
        string dir = Directory.CreateTempSubdirectory("bazaar-terminal-real").FullName;
        try
        {
            var reg = new XdgDefaultTerminalRegistrar(
                new DefaultTerminalSettings { ExecPath = "/opt/bazaar/Bazaar.Terminal", DataHome = dir + "/d", ConfigHome = dir + "/c", ConfigDirs = dir + "/e" },
                new SystemProcessRunner(),
                _ => null);
            var status = await reg.RegisterAsync(TestContext.Current.CancellationToken);
            Assert.Equal(DefaultAppState.Registered, status.State);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
