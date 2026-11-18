using System.Text.RegularExpressions;
using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Pty.Tests;

/// <summary>The S11 Linux PTY checks, run against real shells in this container (docs/spikes/S11.md).</summary>
public sealed class UnixPtyBackendTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(10);

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The Unix PTY backend runs on Linux only.");
        }
    }

    [Fact]
    public void Backend_reports_supported_on_linux()
    {
        RequireLinux();

        Assert.True(new UnixPtyBackend().IsSupported);
    }

    [Fact]
    public void Default_selector_returns_the_unix_backend_on_linux()
    {
        RequireLinux();

        Assert.IsType<UnixPtyBackend>(PtyBackendSelector.CreateDefault());
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    public async Task Marker_echo_reaches_the_output(string shell)
    {
        RequireLinux();
        await using var session = new UnixPtyBackend().Open(new TerminalSpec(shell, 80, 25));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync("echo S11_RESULT_$((6*7))\n"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("S11_RESULT_42", _wait), transcript.Text);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    public async Task Resize_is_seen_by_the_shell(string shell)
    {
        RequireLinux();
        await using var session = new UnixPtyBackend().Open(new TerminalSpec(shell, 80, 25));
        await using var transcript = new PtyTranscript(session);

        session.Resize(100, 30);
        await session.WriteAsync("stty size\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(await transcript.WaitForAsync("30 100", _wait), transcript.Text);

        session.Resize(132, 40);
        await session.WriteAsync("stty size\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(await transcript.WaitForAsync("40 132", _wait), transcript.Text);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    public async Task Colour_sequences_arrive_intact(string shell)
    {
        RequireLinux();
        await using var session = new UnixPtyBackend().Open(new TerminalSpec(shell, 80, 25));
        await using var transcript = new PtyTranscript(session);

        // The typed command holds the escapes as text (backslash-0-3-3), so only the executed output carries ESC bytes.
        const string command = "printf '\\033[31mA\\033[0m\\n'; printf '\\033[38;5;196mB\\033[0m\\n'; printf '\\033[38;2;255;0;0mC\\033[0m\\n'; echo COLOUR_$((6*7))\n";
        await session.WriteAsync(System.Text.Encoding.UTF8.GetBytes(command), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("COLOUR_42", _wait), transcript.Text);
        Assert.Contains("\u001b[31m", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;5;196m", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("\u001b[38;2;255;0;0m", transcript.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("sh")]
    public async Task Ctrl_c_stops_a_foreground_job_and_the_shell_stays_alive(string shell)
    {
        RequireLinux();
        await using var session = new UnixPtyBackend().Open(new TerminalSpec(shell, 80, 25));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync("sleep 30\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        await session.WriteAsync(new byte[] { 0x03 }, TestContext.Current.CancellationToken);
        await session.WriteAsync("echo CTRLC_$((1+1))\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // Without Ctrl-C the sleep holds the shell for 30 s and the echo cannot appear within the wait.
        Assert.True(await transcript.WaitForAsync("CTRLC_2", _wait), transcript.Text);
    }

    [Fact]
    public async Task Exit_code_is_reported()
    {
        RequireLinux();
        await using var session = new UnixPtyBackend().Open(new TerminalSpec("sh", 80, 25));
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync("exit 3\n"u8.ToArray(), TestContext.Current.CancellationToken);

        var code = await session.Exited.WaitAsync(_wait, TestContext.Current.CancellationToken);
        Assert.Equal(3, code);
    }

    [Fact]
    public async Task Environment_is_filtered_and_spec_values_are_passed()
    {
        RequireLinux();
        var spec = new TerminalSpec(
            "sh",
            80,
            25,
            Environment: new Dictionary<string, string>
            {
                ["CALC_TEST_VALUE"] = "ok",
                ["BAZAAR_HIDDEN"] = "secret",
                ["MY_TOKEN"] = "secret",
            });
        await using var session = new UnixPtyBackend().Open(spec);
        await using var transcript = new PtyTranscript(session);

        await session.WriteAsync("printf 'ENV_%s_%s_%s\\n' \"${CALC_TEST_VALUE-unset}\" \"${BAZAAR_HIDDEN-unset}\" \"${MY_TOKEN-unset}\"\n"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(await transcript.WaitForAsync("ENV_ok_unset_unset", _wait), transcript.Text);
    }

    [Fact]
    public async Task Working_directory_is_the_spec_directory()
    {
        RequireLinux();
        var directory = Path.Combine(Path.GetTempPath(), "calcinato-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var session = new UnixPtyBackend().Open(new TerminalSpec("sh", 80, 25, WorkingDirectory: directory));
            await using var transcript = new PtyTranscript(session);

            await session.WriteAsync("pwd\n"u8.ToArray(), TestContext.Current.CancellationToken);

            Assert.True(await transcript.WaitForAsync(Path.GetFileName(directory) + "\r\n", _wait)
                || await transcript.WaitForAsync(Path.GetFileName(directory) + "\n", _wait), transcript.Text);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Clean_shutdown_leaves_no_process_in_the_session_within_five_seconds()
    {
        RequireLinux();
        var session = new UnixPtyBackend().Open(new TerminalSpec("bash", 80, 25));
        await using var transcript = new PtyTranscript(session);

        // A background job is in the session but not in the foreground group, so the hangup alone does not reach it.
        await session.WriteAsync("sleep 300 &\necho BG_$!\n"u8.ToArray(), TestContext.Current.CancellationToken);
        Assert.True(await transcript.WaitForAsync("BG_", _wait), transcript.Text);
        var match = Regex.Match(transcript.Text, @"BG_(\d+)");
        Assert.True(match.Success, transcript.Text);
        var background = int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
        var shellPid = SessionOf(background);
        Assert.True(shellPid > 0);

        await session.DisposeAsync();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5) && LiveMembers(shellPid).Count > 0)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Empty(LiveMembers(shellPid));
    }

    /// <summary>Session id of a process, read from /proc.</summary>
    private static int SessionOf(int pid)
    {
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        return int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Processes in the session led by <paramref name="sessionId"/>, excluding zombies.</summary>
    private static List<int> LiveMembers(int sessionId)
    {
        var members = new List<int>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                if (fields[0] != "Z" && int.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture) == sessionId)
                {
                    members.Add(pid);
                }
            }
            catch (IOException)
            {
                // Exited while being read.
            }
        }

        return members;
    }
}
