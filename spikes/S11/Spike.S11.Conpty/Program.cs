using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Spike.S11;

// Spike S11 harness: drives cmd.exe and pwsh through ConPTY and prints one
// CHECK line per criterion. Exit code 0 only when every check passes.
// "diag" mode is a diagnostic for the Ctrl-C result only; it is not a pre-registered check.
internal static class Program
{
    private enum ShellKind { Cmd, Pwsh }

    private sealed record CheckResult(string Shell, string Name, bool Pass, string Evidence);

    private static readonly List<CheckResult> Results = new();
    private static readonly List<string> Timings = new();

    private const string ColourPayloadPwsh =
        "[Console]::Write([char]27+'[31mS11RED'+[char]27+'[0m'+[char]27+'[38;5;196mS11X256'+[char]27+'[0m'+[char]27+'[38;2;255;0;0mS11TRUE'+[char]27+'[0m'); Write-Output ('S11_COLOUR_'+'DONE')";

    private static readonly Regex RedSgr = new(@"\x1b\[(?:[0-9;]*;)?31(?:;[0-9;]*)?m", RegexOptions.Compiled);
    private static readonly Regex Sgr256 = new(@"\x1b\[[0-9;]*38;5;196[0-9;]*m", RegexOptions.Compiled);
    private static readonly Regex SgrTrue = new(@"\x1b\[[0-9;]*38;2;255;0;0[0-9;]*m", RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("ABORT|ConPTY needs Windows");
            return 2;
        }
        string runId = args.Length > 0 ? args[0] : "run";
        // Children inherit the Ctrl+C-ignore disposition; clear it so 0x03 reaches them the way a terminal would.
        bool ctrlCCleared = Native.SetConsoleCtrlHandler(IntPtr.Zero, false);
        Console.WriteLine($"CTRLC|cleared inherited ignore={ctrlCCleared}");
        Console.WriteLine($"RUN|{runId}|{DateTime.UtcNow:O}|{Environment.OSVersion.VersionString}|{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.OSArchitecture}|logical CPUs {Environment.ProcessorCount}");

        if (runId == "diag")
        {
            RunDiag();
            return 0;
        }

        foreach (var shell in new[] { ShellKind.Cmd, ShellKind.Pwsh })
        {
            try { RunShell(shell); }
            catch (Exception ex)
            {
                Results.Add(new CheckResult(shell.ToString(), "HARNESS", false, "exception: " + ex.Message));
            }
        }

        foreach (var t in Timings) Console.WriteLine(t);
        foreach (var r in Results)
            Console.WriteLine($"CHECK|{r.Shell}|{r.Name}|{(r.Pass ? "PASS" : "FAIL")}|{r.Evidence}");
        int failed = Results.Count(r => !r.Pass);
        Console.WriteLine($"SUMMARY|{runId}|checks {Results.Count}|failed {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Add(ShellKind shell, string name, bool pass, string evidence)
        => Results.Add(new CheckResult(shell.ToString(), name, pass, Esc(evidence)));

    // Make escape bytes visible in evidence lines.
    private static string Esc(string s) => s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n");

    private static HashSet<int> PingPids()
        => Process.GetProcessesByName("PING").Select(p => p.Id).ToHashSet();

    private static HashSet<int> ConhostPids()
        => Process.GetProcessesByName("conhost").Select(p => p.Id).ToHashSet();

    private static List<int> NewAlive(HashSet<int> baseline, Func<HashSet<int>> current)
        => current().Where(id => !baseline.Contains(id)).ToList();

    private static string TailOf(string text, int chars) => text.Length <= chars ? text : text[^chars..];

    // Diagnostic only: where does a Ctrl-C get lost? Not a pre-registered check.
    private static void RunDiag()
    {
        foreach (var shell in new[] { ShellKind.Cmd, ShellKind.Pwsh })
        {
            string cmdline = shell == ShellKind.Cmd ? "cmd.exe" : "pwsh.exe -NoLogo -NoProfile";
            using var s = PtySession.Start(cmdline, 80, 25);
            s.WaitForText(shell == ShellKind.Cmd ? ">" : "PS ", 30);
            Thread.Sleep(1000);
            // A: raw 0x03 at an idle prompt. A processed Ctrl-C shows ^C in the output.
            int m = s.Mark();
            s.Write("\x03");
            Thread.Sleep(2000);
            Console.WriteLine($"DIAG|{shell}|idle prompt raw 0x03|tail: {Esc(TailOf(s.TextSince(m), 200))}");
            // B: raw 0x03 with a native child that handles Ctrl-C by name.
            m = s.Mark();
            s.Write(shell == ShellKind.Cmd ? "ping -t 127.0.0.1\r" : "ping -t 127.0.0.1\r");
            bool running = s.WaitForText("Reply from 127.0.0.1", 20, m);
            int m2 = s.Mark();
            s.Write("\x03");
            Thread.Sleep(3000);
            Console.WriteLine($"DIAG|{shell}|ping raw 0x03|ping running={running}|tail: {Esc(TailOf(s.TextSince(m2), 200))}");
            // Stop ping by any means so the shell can be checked.
            foreach (var id in PingPids()) { try { Process.GetProcessById(id).Kill(); } catch { } }
            Thread.Sleep(1500);
            int m3 = s.Mark();
            s.Write(shell == ShellKind.Cmd ? "echo S11_DIAG_%TEMP:~0,0%OK\r" : "Write-Output (S11_DIAG_+OK)\r");
            bool alive = s.WaitForText("S11_DIAG_OK", 15, m3);
            Console.WriteLine($"DIAG|{shell}|after kill|shell responsive={alive}");
        }
    }

    private static void RunShell(ShellKind shell)
    {
        string cmdline = shell == ShellKind.Cmd ? "cmd.exe" : "pwsh.exe -NoLogo -NoProfile";
        string prompt = shell == ShellKind.Cmd ? ">" : "PS ";
        var pingBaseline = PingPids();

        using var s = PtySession.Start(cmdline, 80, 25);
        var sw = Stopwatch.StartNew();
        bool promptSeen = s.WaitForText(prompt, 30);
        double promptMs = sw.Elapsed.TotalMilliseconds;

        // SHELL_ECHO: the command's output (not its typed echo) must appear.
        {
            int mark = s.Mark();
            string cmd = shell == ShellKind.Cmd
                ? "echo S11_RESULT_%TEMP:~0,0%42"
                : "Write-Output ('S11_RESULT_' + (6*7))";
            s.Write(cmd + "\r");
            bool ok = s.WaitForText("S11_RESULT_42", 30, mark);
            Add(shell, "SHELL_ECHO", promptSeen && ok,
                $"prompt seen={promptSeen}; output marker S11_RESULT_42 seen={ok}; tail: {TailOf(s.Tail(), 160)}");
        }

        // RESIZE: 80x25 -> 100x30 -> 132x40, shell reports the new size each time.
        {
            bool allOk = true;
            var seen = new List<string>();
            foreach (var (cols, rows) in new[] { ((short)100, (short)30), ((short)132, (short)40) })
            {
                s.Resize(cols, rows);
                Thread.Sleep(300);
                int mark = s.Mark();
                int gotCols, gotRows;
                if (shell == ShellKind.Cmd)
                {
                    s.Write("mode con & echo S11_END_%TEMP:~0,0%1\r");
                    bool done = s.WaitForText("S11_END_1", 30, mark);
                    var text = s.TextSince(mark);
                    var c = Regex.Matches(text, @"Columns:\s*(\d+)");
                    var l = Regex.Matches(text, @"Lines:\s*(\d+)");
                    gotCols = c.Count > 0 ? int.Parse(c[^1].Groups[1].Value) : -1;
                    gotRows = l.Count > 0 ? int.Parse(l[^1].Groups[1].Value) : -1;
                    allOk &= done;
                }
                else
                {
                    s.Write("Write-Output ('S11_SIZE_' + $Host.UI.RawUI.WindowSize.Width + 'x' + $Host.UI.RawUI.WindowSize.Height)\r");
                    var re = new Regex(@"S11_SIZE_(\d+)x(\d+)");
                    bool done = s.WaitForRegex(re, 30, mark);
                    var m = re.Match(s.TextSince(mark));
                    gotCols = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                    gotRows = m.Success ? int.Parse(m.Groups[2].Value) : -1;
                    allOk &= done;
                }
                bool match = gotCols == cols && gotRows == rows;
                allOk &= match;
                seen.Add($"want {cols}x{rows} got {gotCols}x{gotRows}");
            }
            Add(shell, "RESIZE", allOk, string.Join("; ", seen));
        }

        // COLOUR: SGR 31 (16-colour), 38;5;196 (256-colour), 38;2;255;0;0 (truecolor) in the output stream.
        {
            int mark = s.Mark();
            if (shell == ShellKind.Pwsh)
                s.Write(ColourPayloadPwsh + "\r");
            else
                s.Write("powershell -NoProfile -Command \"" + ColourPayloadPwsh + "\"\r");
            bool done = s.WaitForText("S11_COLOUR_DONE", 60, mark);
            var text = s.TextSince(mark);
            bool red = RedSgr.IsMatch(text) && text.Contains("S11RED");
            bool c256 = Sgr256.IsMatch(text) && text.Contains("S11X256");
            bool tc = SgrTrue.IsMatch(text) && text.Contains("S11TRUE");
            Add(shell, "COLOUR", done && red && c256 && tc,
                $"done={done}; SGR31={red}; SGR38;5;196={c256}; SGR38;2;255;0;0={tc}");
        }

        // CTRL_C: ping -t runs, 0x03 stops it, the shell survives and runs a command.
        {
            var pingsBefore = pingBaseline;
            int mark = s.Mark();
            s.Write("ping -t 127.0.0.1\r");
            bool running = s.WaitForText("Reply from 127.0.0.1", 20, mark);
            int markBreak = s.Mark();
            s.Write("\x03");
            bool stopped = s.WaitForText("Ping statistics for 127.0.0.1", 10, markBreak);
            Thread.Sleep(1000);
            var stillAlive = NewAlive(pingsBefore, PingPids);
            int markAlive = s.Mark();
            s.Write(shell == ShellKind.Cmd ? "echo S11_ALIVE_%TEMP:~0,0%OK\r" : "Write-Output ('S11_ALIVE_'+'OK')\r");
            bool alive = s.WaitForText("S11_ALIVE_OK", 20, markAlive);
            bool pass = running && stopped && stillAlive.Count == 0 && alive;
            Add(shell, "CTRL_C", pass,
                $"ping running={running}; ping stopped on 0x03={stopped}; ping.exe left running={stillAlive.Count}; shell alive after={alive}");
        }

        // SHUTDOWN: ping running, ClosePseudoConsole, shell and ping gone within 5 s.
        {
            var pingsBefore = pingBaseline;
            var conhostBefore = ConhostPids();
            int mark = s.Mark();
            s.Write("ping -t 127.0.0.1\r");
            bool running = s.WaitForText("Reply from 127.0.0.1", 20, mark);
            bool shellAliveBefore = s.IsShellAlive();
            var (closeReturned, shellExited) = s.CloseAndWait();
            var orphans = new List<int>();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                orphans = NewAlive(pingsBefore, PingPids);
                if (orphans.Count == 0) break;
                Thread.Sleep(200);
            } while (DateTime.UtcNow < deadline);
            foreach (var id in orphans)
            {
                try { Process.GetProcessById(id).Kill(); } catch { /* already gone */ }
            }
            int conhostLeft = NewAlive(conhostBefore, ConhostPids).Count;
            bool pass = running && shellAliveBefore && closeReturned && shellExited && orphans.Count == 0;
            Add(shell, "SHUTDOWN", pass,
                $"ping running={running}; shell alive before close={shellAliveBefore}; ClosePseudoConsole returned={closeReturned}; shell exited within 5 s={shellExited}; orphan ping.exe={orphans.Count}; new conhost.exe still alive (informational)={conhostLeft}");
        }

        Timings.Add($"TIMING|{shell}|first output ms {(s.FirstOutputMs is double f ? f.ToString("F1") : "n/a")}|prompt ms after CreateProcess returned {promptMs.ToString("F1")}|prompt seen={promptSeen}");
    }
}
