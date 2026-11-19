using System.Diagnostics;

namespace Bazaar.Guest.Pointer.Tests;

/// <summary>The result of running one process to completion.</summary>
internal sealed record RunResult(int ExitCode, byte[] Stdout, string Stderr, TimeSpan Elapsed);

/// <summary>
/// A headless cage compositor (the kiosk setup of the agents' Linux VMs) running a Chromium that shows the pointer probe page,
/// on a private runtime directory. Everything it starts is hard-killed on dispose, and anything left by an earlier run is killed
/// first, so a measurement never reads a stale instance. Not ready (with the reason) when cage, grim or Chromium is missing.
/// </summary>
public sealed class CageFixture : IAsyncLifetime
{
    private const string _dirPrefix = "bazaar-pointer-test-";
    private const string _probeReady = "30,60,90";

    private string? _dir;
    private Process? _cage;

    /// <summary>Why the real tests cannot run here, or <see langword="null"/> when they can.</summary>
    public string? SkipReason { get; private set; }

    internal string RuntimeDir => _dir ?? throw new InvalidOperationException("The compositor is not running.");

    /// <summary>The tool's invocation: its apphost when present, otherwise <c>dotnet bazaar-pointer.dll</c>.</summary>
    internal static (string File, string[] Prefix) Tool()
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "bazaar-pointer.dll");
        string host = Path.Combine(AppContext.BaseDirectory, "bazaar-pointer");
        return File.Exists(host) ? (host, []) : ("dotnet", [dll]);
    }

    public async ValueTask InitializeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            SkipReason = "needs Linux (cage and grim)";
            return;
        }

        string? cage = Which("cage");
        string? grim = Which("grim");
        string? chrome = FindChrome();
        if (cage is null || grim is null || chrome is null)
        {
            SkipReason = $"needs cage ({(cage is null ? "missing" : "found")}), grim ({(grim is null ? "missing" : "found")}) and a Chromium under /opt/pw-browsers ({(chrome is null ? "missing" : "found")})";
            return;
        }

        KillStale();
        _dir = Path.Combine(Path.GetTempPath(), _dirPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string profile = Path.Combine(_dir, "profile");
        Directory.CreateDirectory(profile);
        string page = Path.Combine(AppContext.BaseDirectory, "Pages", "pointer-probe.html");

        var psi = new ProcessStartInfo(cage) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["XDG_RUNTIME_DIR"] = _dir;
        psi.Environment["WLR_BACKENDS"] = "headless";
        psi.Environment["WLR_RENDERER"] = "pixman";
        psi.Environment["WLR_LIBINPUT_NO_DEVICES"] = "1";
        psi.Environment.Remove("WAYLAND_DISPLAY");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add(chrome);
        psi.ArgumentList.Add("--ozone-platform=wayland");
        psi.ArgumentList.Add("--disable-gpu");
        psi.ArgumentList.Add("--no-first-run");
        // Kiosk and no infobars, so the page is the whole 1280x720 output and client coordinates equal output coordinates.
        psi.ArgumentList.Add("--kiosk");
        psi.ArgumentList.Add("--test-type");
        psi.ArgumentList.Add("--disable-infobars");
        // Chromium refuses to run as root without this. It is for this local test page only and is never a product setting.
        if (Environment.UserName == "root")
            psi.ArgumentList.Add("--no-sandbox");
        psi.ArgumentList.Add($"--user-data-dir={profile}");
        psi.ArgumentList.Add(new Uri(page).AbsoluteUri);
        _cage = Process.Start(psi) ?? throw new InvalidOperationException("cage did not start");
        _ = _cage.StandardOutput.ReadToEndAsync();
        _ = _cage.StandardError.ReadToEndAsync();

        // The page paints a fixed colour once it has loaded; reading that colour is the readiness check.
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (_cage.HasExited)
                throw new InvalidOperationException("cage exited early: code " + _cage.ExitCode);
            if (File.Exists(Path.Combine(_dir, "wayland-0")) && await TryPixelAsync(2, 2) is { } p && p == _probeReady)
                return;
            await Task.Delay(250);
        }
        throw new TimeoutException("the probe page did not appear within 60 seconds");
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_cage is not null && !_cage.HasExited)
                _cage.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
        KillStale();
        try
        {
            if (_dir is not null)
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A killed browser may still be releasing files; the temp directory is not otherwise used.
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>The colour of one pixel of the output as <c>r,g,b</c>, or <see langword="null"/> when grim fails.</summary>
    internal async Task<string?> TryPixelAsync(int x, int y)
    {
        RunResult r = await RunAsync("grim", ["-g", $"{x},{y} 1x1", "-t", "ppm", "-"], RuntimeDir, TimeSpan.FromSeconds(10));
        // P6, then "1 1", then "255", each ended by one whitespace byte, then 3 bytes of colour.
        return r.ExitCode == 0 && r.Stdout.Length >= 14 && r.Stdout[0] == 'P' ? $"{r.Stdout[^3]},{r.Stdout[^2]},{r.Stdout[^1]}" : null;
    }

    /// <summary>The size of the output as <c>WxH</c>.</summary>
    internal async Task<string> OutputSizeAsync()
    {
        RunResult r = await RunAsync("grim", ["-t", "ppm", "-"], RuntimeDir, TimeSpan.FromSeconds(10));
        string[] head = System.Text.Encoding.ASCII.GetString(r.Stdout, 0, 20).Split('\n');
        return head[1].Replace(' ', 'x');
    }

    internal static async Task<RunResult> RunAsync(string file, IEnumerable<string> args, string runtimeDir, TimeSpan timeout, string? display = "wayland-0")
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["XDG_RUNTIME_DIR"] = runtimeDir;
        if (display is null)
            psi.Environment.Remove("WAYLAND_DISPLAY");
        else
            psi.Environment["WAYLAND_DISPLAY"] = display;
        var sw = Stopwatch.StartNew();
        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        Task copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            await copy;
        }
        catch (OperationCanceledException)
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException($"{file} did not finish within {timeout.TotalSeconds} seconds");
        }
        return new RunResult(p.ExitCode, ms.ToArray(), await err, sw.Elapsed);
    }

    private static string? Which(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    private static string? FindChrome()
    {
        const string root = "/opt/pw-browsers";
        if (!Directory.Exists(root))
            return null;
        return Directory.EnumerateDirectories(root, "chromium-*").Order(StringComparer.Ordinal)
            .Select(d => Path.Combine(d, "chrome-linux64", "chrome")).FirstOrDefault(File.Exists);
    }

    // Kills every process of this test (an earlier run included) that has our directory prefix in its environment or command
    // line: cage with its private runtime directory, and Chromium with its profile directory.
    private static void KillStale()
    {
        if (!OperatingSystem.IsLinux())
            return;
        string marker = Path.Combine(Path.GetTempPath(), _dirPrefix);
        foreach (string proc in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(proc), out int pid) || pid == Environment.ProcessId)
                continue;
            try
            {
                string text = File.ReadAllText(Path.Combine(proc, "cmdline")) + File.ReadAllText(Path.Combine(proc, "environ"));
                if (text.Contains(marker, StringComparison.Ordinal))
                    Process.GetProcessById(pid).Kill();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                // The process ended or is not ours to read.
            }
        }
    }
}
