using System.Diagnostics;
using Bazaar.Client.Calcinato.Session;
using Bazaar.Client.Linux;

namespace Bazaar.Client.Linux.Tests;

/// <summary>
/// Opens a real shell through the Linux head's own session manager (the Unix PTY backend, doc 55 section 5.4)
/// and reads a command's output back from the emulator screen.
/// </summary>
public class LinuxTerminalShellTests
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Echo_typed_into_a_real_shell_is_read_back_from_the_screen()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The Linux head's PTY runs on Linux only.");
        }

        await using var sessions = CalcinatoTerminalPanes.CreateSessions();
        var session = sessions.OpenLocal("sh");
        var screen = session.Emulator;

        // Wait for the shell's first output (its prompt) so the typed line is not lost to start-up.
        await WaitForAsync(() => AllText(screen).Trim().Length > 0, "the shell prompt");

        // Compute the marker in the shell, so the typed text never contains the result.
        await session.Source.WriteAsync("echo LINUXPTY_$((6*7))\n"u8.ToArray(), TestContext.Current.CancellationToken);

        await WaitForAsync(() => AllText(screen).Contains("LINUXPTY_42", StringComparison.Ordinal), "the echo output");
    }

    private static string AllText(Bazaar.Client.Calcinato.Emulation.Emulator screen)
    {
        var lines = new List<string>();
        for (var row = 0; row < screen.ScrollbackCount + screen.Rows; row++)
        {
            lines.Add(screen.GetLineText(row));
        }

        return string.Join('\n', lines);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _wait)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }
}
