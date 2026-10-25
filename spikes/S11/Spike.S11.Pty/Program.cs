using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Spike.S11.Pty;

// Spike S11 Linux harness: drives bash and sh through a POSIX pty and prints one CHECK line
// per criterion, in the same format as the Windows ConPTY harness (spikes/S11/Spike.S11.Conpty).
// Exit code 0 only when every check passes.
internal static class Program
{
    private sealed record ShellSpec(string Name, string Path, string[] Argv);
    private sealed record CheckResult(string Shell, string Name, bool Pass, string Evidence);

    private static readonly string[] BashArgv = { "bash", "--noprofile", "--norc", "-i" };
    private static readonly string[] ShArgv = { "sh", "-i" };
    private static readonly List<CheckResult> Results = new();
    private static readonly List<string> Timings = new();

    private static readonly Regex PromptRe = new(@"[$#] ", RegexOptions.Compiled);
    private static readonly Regex SizeRe = new(@"S11_SIZE_(\d+)x(\d+)", RegexOptions.Compiled);
    private static readonly Regex RedSgr = new(@"\x1b\[(?:[0-9;]*;)?31(?:;[0-9;]*)?m", RegexOptions.Compiled);
    private static readonly Regex Sgr256 = new(@"\x1b\[[0-9;]*38;5;196[0-9;]*m", RegexOptions.Compiled);
    private static readonly Regex SgrTrue = new(@"\x1b\[[0-9;]*38;2;255;0;0[0-9;]*m", RegexOptions.Compiled);

    // Typed text is echoed by the tty and by readline, so every check matches output that is
    // built from quoted or computed pieces, never a contiguous typed copy of the marker.
    private const string ColourCommand =
        @"printf '\033[31mS11RED\033[0m\033[38;5;196mS11X256\033[0m\033[38;2;255;0;0mS11TRUE\033[0m\n'; echo ""S11_COLOUR_""DONE";

    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("ABORT|POSIX pty harness needs Linux");
            return 2;
        }
        string runId = args.Length > 0 ? args[0] : "run";
        Console.WriteLine($"RUN|{runId}|{DateTime.UtcNow:O}|{Environment.OSVersion.VersionString}|{RuntimeInformation.FrameworkDescription}|{RuntimeInformation.OSArchitecture}|logical CPUs {Environment.ProcessorCount}");

        var env = new List<string>
        {
            "PATH=/usr/local/bin:/usr/bin:/bin",
            "HOME=" + AppContext.BaseDirectory,
            "HISTFILE=/dev/null",
            "TERM=xterm-256color",
            "LANG=C.UTF-8",
        };
        var shells = new[]
        {
            new ShellSpec("Bash", "/usr/bin/bash", BashArgv),
            new ShellSpec("Sh", "/bin/sh", ShArgv),
        };
        foreach (var shell in shells)
        {
            if (!File.Exists(shell.Path))
            {
                Console.WriteLine($"SKIP|{shell.Name}|{shell.Path} not installed");
                continue;
            }
            try { RunShell(shell, env); }
            catch (Exception ex)
            {
                Results.Add(new CheckResult(shell.Name, "HARNESS", false, "exception: " + ex.Message));
            }
        }

        foreach (var t in Timings) Console.WriteLine(t);
        foreach (var r in Results)
            Console.WriteLine($"CHECK|{r.Shell}|{r.Name}|{(r.Pass ? "PASS" : "FAIL")}|{r.Evidence}");
        int failed = Results.Count(r => !r.Pass);
        Console.WriteLine($"SUMMARY|{runId}|checks {Results.Count}|failed {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static void Add(string shell, string name, bool pass, string evidence)
        => Results.Add(new CheckResult(shell, name, pass, Esc(evidence)));

    private static string Esc(string s) => s.Replace("\x1b", "\\e").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string TailOf(string text, int chars) => text.Length <= chars ? text : text[^chars..];

    private static void RunShell(ShellSpec shell, List<string> env)
    {
        using var s = PtySession.Start(shell.Path, shell.Argv, env, 80, 25);
        int sid = s.ProcessId;
        var sw = Stopwatch.StartNew();
        bool promptSeen = s.WaitForRegex(PromptRe, 30);
        double promptMs = sw.Elapsed.TotalMilliseconds;

        // SHELL_ECHO: the command's output (not its typed echo) must appear.
        {
            int mark = s.Mark();
            s.Write("echo S11_RESULT_$((6*7))\r");
            bool ok = s.WaitForText("S11_RESULT_42", 30, mark);
            Add(shell.Name, "SHELL_ECHO", promptSeen && ok,
                $"prompt seen={promptSeen}; output marker S11_RESULT_42 seen={ok}; tail: {TailOf(s.Tail(), 160)}");
        }

        // RESIZE: 80x25 -> 100x30 -> 132x40 (cols x rows); shell reports the kernel's winsize via stty.
        {
            bool allOk = true;
            var seen = new List<string>();
            foreach (var (cols, rows) in new[] { ((short)100, (short)30), ((short)132, (short)40) })
            {
                s.Resize(cols, rows);
                Thread.Sleep(300);
                int mark = s.Mark();
                s.Write("set -- $(stty size); echo S11_SIZE_${2}x${1}\r");
                bool done = s.WaitForRegex(SizeRe, 30, mark);
                var m = SizeRe.Match(s.TextSince(mark));
                int gotCols = m.Success ? int.Parse(m.Groups[1].Value) : -1;
                int gotRows = m.Success ? int.Parse(m.Groups[2].Value) : -1;
                bool match = done && gotCols == cols && gotRows == rows;
                allOk &= match;
                seen.Add($"want {cols}x{rows} got {gotCols}x{gotRows}");
            }
            Add(shell.Name, "RESIZE", allOk, string.Join("; ", seen));
        }

        // COLOUR: SGR 31 (16-colour), 38;5;196 (256-colour), 38;2;255;0;0 (truecolor) must arrive unmangled.
        {
            int mark = s.Mark();
            s.Write(ColourCommand + "\r");
            bool done = s.WaitForText("S11_COLOUR_DONE", 30, mark);
            var text = s.TextSince(mark);
            bool red = RedSgr.IsMatch(text) && text.Contains("S11RED", StringComparison.Ordinal);
            bool c256 = Sgr256.IsMatch(text) && text.Contains("S11X256", StringComparison.Ordinal);
            bool tc = SgrTrue.IsMatch(text) && text.Contains("S11TRUE", StringComparison.Ordinal);
            Add(shell.Name, "COLOUR", done && red && c256 && tc,
                $"done={done}; SGR31={red}; SGR38;5;196={c256}; SGR38;2;255;0;0={tc}");
        }

        // CTRL_C: sleep runs in the foreground, 0x03 stops it, the shell survives and runs a command.
        {
            int mark = s.Mark();
            s.Write("sleep 30\r");
            bool running = PtySession.WaitForSessionProcess(sid, "sleep", 10, present: true);
            int markBreak = s.Mark();
            s.Write("\x03");
            bool stopped = PtySession.WaitForSessionProcess(sid, "sleep", 5, present: false);
            int markAlive = s.Mark();
            s.Write("echo S11_ALIVE_\"OK\"\r");
            bool alive = s.WaitForText("S11_ALIVE_OK", 20, markAlive) && s.IsShellAlive();
            int left = PtySession.SessionMembers(sid).Count(m => m.Comm == "sleep");
            bool pass = running && stopped && left == 0 && alive;
            Add(shell.Name, "CTRL_C", pass,
                $"sleep running={running}; sleep stopped on 0x03={stopped}; sleep left running={left}; shell alive after={alive}");
        }

        // SHUTDOWN: sleep running, close the master (SIGHUP to the session), shell and sleep gone within 5 s.
        {
            s.Write("sleep 30\r");
            bool running = PtySession.WaitForSessionProcess(sid, "sleep", 10, present: true);
            bool shellAliveBefore = s.IsShellAlive();
            var (closeReturned, shellExited) = s.CloseAndWait();
            var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            List<(int Pid, string Comm)> orphans;
            do
            {
                orphans = PtySession.SessionMembers(sid);
                if (orphans.Count == 0) break;
                Thread.Sleep(100);
            } while (Stopwatch.GetTimestamp() < deadline);
            string orphanNames = orphans.Count == 0 ? "none" : string.Join(",", orphans.Select(o => o.Comm));
            PtySession.KillSession(sid);
            bool pass = running && shellAliveBefore && closeReturned && shellExited && orphans.Count == 0;
            Add(shell.Name, "SHUTDOWN", pass,
                $"sleep running={running}; shell alive before close={shellAliveBefore}; close(master) returned ok={closeReturned}; shell exited within 5 s={shellExited} (exit status {s.ExitStatus}); orphan processes in session={orphans.Count} ({orphanNames})");
        }

        Timings.Add($"TIMING|{shell.Name}|first output ms {(s.FirstOutputMs is double f ? f.ToString("F1") : "n/a")}|prompt ms after spawn returned {promptMs.ToString("F1")}|prompt seen={promptSeen}");
    }
}
