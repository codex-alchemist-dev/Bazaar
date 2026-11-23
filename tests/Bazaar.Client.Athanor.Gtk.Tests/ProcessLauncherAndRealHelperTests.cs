using System.Runtime.InteropServices;
using Bazaar.Client.Athanor.Gtk.Helper;

namespace Bazaar.Client.Athanor.Gtk.Tests;

public sealed class ProcessLauncherAndRealHelperTests
{
    [Fact]
    public async Task The_process_launcher_round_trips_lines_and_reports_exit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/cat"), "Needs /bin/cat.");
        var launcher = new ProcessEditorHelperLauncher("/bin/cat", []);
        await using IEditorHelperConnection connection = await launcher.LaunchAsync(TestContext.Current.CancellationToken);

        await connection.WriteLineAsync("""{"type":"helper.ping","id":1}""", TestContext.Current.CancellationToken);
        string? echoed = await connection.ReadLineAsync(TestContext.Current.CancellationToken);

        Assert.Equal("""{"type":"helper.ping","id":1}""", echoed);
        connection.Kill();
        Assert.True(await connection.Exited.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken) != 0);
        Assert.Null(await connection.ReadLineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_process_launcher_refuses_a_line_over_the_limit()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/cat"), "Needs /bin/cat.");
        var launcher = new ProcessEditorHelperLauncher("/bin/cat", [], maxLineChars: 100);
        await using IEditorHelperConnection connection = await launcher.LaunchAsync(TestContext.Current.CancellationToken);

        await connection.WriteLineAsync(new string('x', 500), TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await connection.ReadLineAsync(TestContext.Current.CancellationToken));
        connection.Kill();
    }

    [Fact]
    public async Task A_missing_helper_executable_fails_to_launch()
    {
        var launcher = new ProcessEditorHelperLauncher("/definitely/not/a/helper", []);

        await Assert.ThrowsAnyAsync<Exception>(async () => await launcher.LaunchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_helper_that_dies_at_start_is_a_crash_through_the_real_launcher()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && File.Exists("/bin/sh"), "Needs /bin/sh.");
        var launcher = new ProcessEditorHelperLauncher("/bin/sh", ["-c", "echo boom >&2; exit 3"]);
        await using var host = new GtkEditorHost(
            launcher,
            new GtkEditorHostOptions { MaxRestarts = 0, ExitGrace = TimeSpan.FromSeconds(2) });
        var crashed = new TaskCompletionSource<EditorCrashReason>();
        host.Crashed += (_, r) => crashed.TrySetResult(r);

        Task start = host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EditorCrashReason.RendererExited, await crashed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EditorHelperException>(() => start);
        Assert.Contains("Exit code 3", host.LastFailure, StringComparison.Ordinal);
        Assert.Contains("boom", host.LastFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_libwebkit_the_real_helper_exits_with_the_missing_library_code()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Assert.SkipWhen(NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out nint h) && Free(h), "libwebkit2gtk is installed.");
        string dll = HelperDll();
        Assert.SkipUnless(File.Exists(dll), "Helper assembly was not copied next to the tests.");
        string bundle = Directory.CreateTempSubdirectory("athanor-bundle").FullName;
        File.WriteAllText(Path.Combine(bundle, "host.html"), "<html></html>");
        try
        {
            // --allow-no-sandbox so the check that fails is the library, not the sandbox tool.
            var launcher = new ProcessEditorHelperLauncher("dotnet", [dll, "--bundle", bundle, "--allow-no-sandbox"], allowNoSandbox: true);
            await using IEditorHelperConnection connection = await launcher.LaunchAsync(TestContext.Current.CancellationToken);

            int code = await connection.Exited.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.Equal(HelperExitCodes.NativeLibraryMissing, code);
        }
        finally
        {
            Directory.Delete(bundle, true);
        }
    }

    /// <summary>
    /// The real path: the real helper, real WebKitGTK, a real display and a Monaco bundle that speaks doc 57 section 4.
    /// Opt-in. Needs libwebkit2gtk-4.1, a display, and BAZAAR_ATHANOR_MONACO_BUNDLE pointing at a directory with
    /// host.html (the Bazaar.Client.Athanor.Bundle output). Set BAZAAR_ATHANOR_ALLOW_NO_SANDBOX=1 in a root container.
    /// </summary>
    [Fact]
    public async Task Real_webkitgtk_helper_reaches_ready_and_round_trips_a_document()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Assert.SkipUnless(NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out nint lib) && Free(lib), "libwebkit2gtk-4.1 is not installed.");
        Assert.SkipUnless(
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
            "No display.");
        string? bundle = Environment.GetEnvironmentVariable("BAZAAR_ATHANOR_MONACO_BUNDLE");
        Assert.SkipUnless(!string.IsNullOrEmpty(bundle) && File.Exists(Path.Combine(bundle, "host.html")), "Set BAZAAR_ATHANOR_MONACO_BUNDLE to a Monaco bundle directory.");
        string dll = HelperDll();
        Assert.SkipUnless(File.Exists(dll), "Helper assembly was not copied next to the tests.");
        bool noSandbox = Environment.GetEnvironmentVariable("BAZAAR_ATHANOR_ALLOW_NO_SANDBOX") == "1";

        var options = new GtkEditorHostOptions { AllowNoSandbox = noSandbox, HandshakeTimeout = TimeSpan.FromSeconds(60), MaxRestarts = 0 };
        var launcher = ProcessEditorHelperLauncher.ForHelper(dll, bundle!, options);
        await using var host = new GtkEditorHost(launcher, options);
        var id = new EditorDocumentId("ws", "a.txt");

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.OpenAsync(new EditorDocument(id, "hello", "r1", "plaintext", false), TestContext.Current.CancellationToken);

        Assert.Equal(EditorHelperState.Running, host.State);
    }

    private static string HelperDll() => Path.Combine(AppContext.BaseDirectory, "Bazaar.Client.Athanor.Gtk.Helper.dll");

    private static bool Free(nint handle)
    {
        NativeLibrary.Free(handle);
        return true;
    }
}
