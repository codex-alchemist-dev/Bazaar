using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Spike.S11.Pty;

// One shell attached to a POSIX pseudo-terminal. The shell is started with posix_spawn and
// POSIX_SPAWN_SETSID, and its stdio is the slave opened by the child itself: as a new session
// leader with no controlling terminal, that open makes the slave the controlling terminal.
// Output is drained on a background thread that polls, so the reader can be stopped before
// the master is closed.
internal sealed class PtySession : IDisposable
{
    private const int SpawnStructBytes = 1024;
    private const int SigsetBytes = 128;

    private int _master = -1;
    private int _pid;
    private bool _exited;
    private int _exitStatus = -1;
    private volatile bool _stop;
    private Thread? _reader;
    private readonly MemoryStream _output = new();
    private readonly object _gate = new();
    private long _launchTimestamp;
    private double? _firstOutputMs;
    private nint _fileActions;
    private nint _attr;
    private nint _sigAll;
    private nint _sigEmpty;

    public int ProcessId => _pid;
    public double? FirstOutputMs { get { lock (_gate) return _firstOutputMs; } }
    public int ExitStatus => _exitStatus;

    private PtySession() { }

    public static PtySession Start(string path, IReadOnlyList<string> argv, IReadOnlyList<string> env, short cols, short rows)
    {
        var s = new PtySession();
        var owned = new List<nint>();
        try
        {
            s._master = Native.posix_openpt(Native.O_RDWR | Native.O_NOCTTY | Native.O_CLOEXEC);
            if (s._master < 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "posix_openpt");
            if (Native.grantpt(s._master) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "grantpt");
            if (Native.unlockpt(s._master) != 0) throw new Win32Exception(Marshal.GetLastPInvokeError(), "unlockpt");

            var nameBuf = new byte[256];
            if (Native.ptsname_r(s._master, ref nameBuf[0], (nuint)nameBuf.Length) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ptsname_r");
            string slave = Encoding.ASCII.GetString(nameBuf, 0, Array.IndexOf(nameBuf, (byte)0));

            var ws = new Native.Winsize { Row = (ushort)rows, Col = (ushort)cols };
            if (Native.ioctl(s._master, Native.TIOCSWINSZ, ref ws) != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "ioctl TIOCSWINSZ");

            s._fileActions = AllocZeroed(SpawnStructBytes);
            s._attr = AllocZeroed(SpawnStructBytes);
            s._sigAll = AllocZeroed(SigsetBytes);
            s._sigEmpty = AllocZeroed(SigsetBytes);
            for (int i = 0; i < SigsetBytes; i++) Marshal.WriteByte(s._sigAll, i, 0xFF);

            Check(Native.posix_spawn_file_actions_init(s._fileActions), "posix_spawn_file_actions_init");
            // Child opens the slave without O_NOCTTY after setsid: that makes it the controlling tty.
            nint slavePath = Marshal.StringToCoTaskMemUTF8(slave); owned.Add(slavePath);
            Check(Native.posix_spawn_file_actions_addopen(s._fileActions, 0, slavePath, Native.O_RDWR, 0), "addopen slave");
            Check(Native.posix_spawn_file_actions_adddup2(s._fileActions, 0, 1), "adddup2 stdout");
            Check(Native.posix_spawn_file_actions_adddup2(s._fileActions, 0, 2), "adddup2 stderr");

            Check(Native.posix_spawnattr_init(s._attr), "posix_spawnattr_init");
            Check(Native.posix_spawnattr_setflags(s._attr, Native.SpawnFlags), "posix_spawnattr_setflags");
            // Reset ignored signals (the .NET runtime ignores SIGPIPE) and clear the mask, so the shell starts clean.
            Check(Native.posix_spawnattr_setsigdefault(s._attr, s._sigAll), "setsigdefault");
            Check(Native.posix_spawnattr_setsigmask(s._attr, s._sigEmpty), "setsigmask");

            nint pathPtr = Marshal.StringToCoTaskMemUTF8(path); owned.Add(pathPtr);
            nint argvPtr = StringArray(argv, owned);
            nint envPtr = StringArray(env, owned);

            s._reader = new Thread(s.ReadLoop) { IsBackground = true, Name = "pty-reader" };
            s._launchTimestamp = Stopwatch.GetTimestamp();
            int rc = Native.posix_spawn(out int pid, pathPtr, s._fileActions, s._attr, argvPtr, envPtr);
            if (rc != 0) throw new Win32Exception(rc, "posix_spawn: " + path);
            s._pid = pid;
            s._reader.Start();
        }
        catch
        {
            s.Dispose();
            throw;
        }
        finally
        {
            foreach (var p in owned) Marshal.FreeCoTaskMem(p);
        }
        return s;
    }

    private static void Check(int rc, string what)
    {
        if (rc != 0) throw new Win32Exception(rc, what);
    }

    private static nint AllocZeroed(int bytes)
    {
        var p = Marshal.AllocHGlobal(bytes);
        for (int i = 0; i < bytes; i++) Marshal.WriteByte(p, i, 0);
        return p;
    }

    // Builds a NULL-terminated char* array. Strings and the array are recorded in owned for freeing.
    private static nint StringArray(IReadOnlyList<string> items, List<nint> owned)
    {
        var arr = Marshal.AllocHGlobal(IntPtr.Size * (items.Count + 1));
        owned.Add(arr);
        for (int i = 0; i < items.Count; i++)
        {
            nint p = Marshal.StringToCoTaskMemUTF8(items[i]);
            owned.Add(p);
            Marshal.WriteIntPtr(arr, i * IntPtr.Size, p);
        }
        Marshal.WriteIntPtr(arr, items.Count * IntPtr.Size, IntPtr.Zero);
        return arr;
    }

    private void ReadLoop()
    {
        var buf = new byte[4096];
        var pfd = new Native.PollFd { Fd = _master, Events = Native.POLLIN };
        while (!_stop)
        {
            pfd.Revents = 0;
            if (Native.poll(ref pfd, 1, 100) <= 0) continue;
            if ((pfd.Revents & (Native.POLLIN | Native.POLLHUP | Native.POLLERR)) == 0) continue;
            nint n = Native.read(_master, ref buf[0], (nuint)buf.Length);
            if (n < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == Native.EINTR || err == Native.EAGAIN) continue;
                break; // EIO once the slave has no open fds (shell exited or hangup)
            }
            if (n == 0) break;
            lock (_gate)
            {
                if (_firstOutputMs is null)
                    _firstOutputMs = Stopwatch.GetElapsedTime(_launchTimestamp).TotalMilliseconds;
                _output.Write(buf, 0, (int)n);
            }
        }
    }

    public int Mark() { lock (_gate) return (int)_output.Length; }

    public string TextSince(int mark)
    {
        lock (_gate)
        {
            var bytes = _output.ToArray();
            if (mark >= bytes.Length) return string.Empty;
            return Encoding.UTF8.GetString(bytes, mark, bytes.Length - mark);
        }
    }

    public string Tail(int chars = 400)
    {
        var t = TextSince(0);
        return t.Length <= chars ? t : t[^chars..];
    }

    public void Write(string text)
    {
        var bytes = new UTF8Encoding(false).GetBytes(text);
        int off = 0;
        while (off < bytes.Length)
        {
            nint n = Native.write(_master, ref bytes[off], (nuint)(bytes.Length - off));
            if (n < 0)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err == Native.EINTR || err == Native.EAGAIN) continue;
                throw new Win32Exception(err, "write to pty master");
            }
            off += (int)n;
        }
    }

    public void Resize(short cols, short rows)
    {
        var ws = new Native.Winsize { Row = (ushort)rows, Col = (ushort)cols };
        if (Native.ioctl(_master, Native.TIOCSWINSZ, ref ws) != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "ioctl TIOCSWINSZ");
    }

    public bool WaitForText(string needle, int timeoutSeconds, int mark = 0)
        => WaitFor(() => TextSince(mark).Contains(needle, StringComparison.Ordinal), timeoutSeconds);

    public bool WaitForRegex(System.Text.RegularExpressions.Regex re, int timeoutSeconds, int mark = 0)
        => WaitFor(() => re.IsMatch(TextSince(mark)), timeoutSeconds);

    private static bool WaitFor(Func<bool> pred, int timeoutSeconds)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (pred()) return true;
            Thread.Sleep(50);
        }
        return pred();
    }

    // Reaps the shell if it has exited. Returns true while it is still running.
    public bool IsShellAlive()
    {
        if (_exited) return false;
        int rc = Native.waitpid(_pid, out int status, Native.WNOHANG);
        if (rc == 0) return true;
        _exited = true;
        _exitStatus = rc == _pid ? status : -1; // -1 with ECHILD means someone else reaped it
        return false;
    }

    // Closes the master while the shell may still be running (kernel hangs up the slave: SIGHUP to the session).
    // Returns whether close() succeeded and whether the shell exited on its own within 5 s; if not, SIGKILL.
    public (bool closeReturned, bool shellExitedOnItsOwn) CloseAndWait()
    {
        StopReader();
        int rc = Native.close(_master);
        _master = -1;
        bool exited = WaitForShellExit(TimeSpan.FromSeconds(5));
        if (!exited)
        {
            Native.kill(_pid, Native.SIGKILL);
            WaitForShellExit(TimeSpan.FromSeconds(5));
        }
        return (rc == 0, exited);
    }

    private bool WaitForShellExit(TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!IsShellAlive()) return true;
            Thread.Sleep(50);
        }
        return !IsShellAlive();
    }

    private void StopReader()
    {
        _stop = true;
        if (_reader is not null && _reader.IsAlive) _reader.Join(TimeSpan.FromSeconds(2));
    }

    // Live processes (not zombies) whose session id is sid. The shell is a session leader, so sid == its pid.
    public static List<(int Pid, string Comm)> SessionMembers(int sid)
    {
        var result = new List<(int, string)>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out int pid)) continue;
            try
            {
                string stat = File.ReadAllText(Path.Combine(dir, "stat"));
                int open = stat.IndexOf('(');
                int close = stat.LastIndexOf(')');
                string comm = stat[(open + 1)..close];
                string[] rest = stat[(close + 2)..].Split(' ');
                // rest: state, ppid, pgrp, session, ...
                char state = rest[0][0];
                int session = int.Parse(rest[3]);
                if (session == sid && state != 'Z' && state != 'X') result.Add((pid, comm));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    public static bool WaitForSessionProcess(int sid, string comm, int timeoutSeconds, bool present)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (SessionMembers(sid).Any(m => m.Comm == comm) == present) return true;
            Thread.Sleep(50);
        }
        return SessionMembers(sid).Any(m => m.Comm == comm) == present;
    }

    public static void KillSession(int sid)
    {
        foreach (var (pid, _) in SessionMembers(sid)) Native.kill(pid, Native.SIGKILL);
    }

    public void Dispose()
    {
        StopReader();
        if (_master >= 0)
        {
            Native.close(_master);
            _master = -1;
        }
        if (_pid != 0)
        {
            KillSession(_pid);
            if (!_exited)
            {
                Native.kill(_pid, Native.SIGKILL);
                WaitForShellExit(TimeSpan.FromSeconds(5));
            }
        }
        if (_fileActions != 0)
        {
            _ = Native.posix_spawn_file_actions_destroy(_fileActions);
            Marshal.FreeHGlobal(_fileActions);
            _fileActions = 0;
        }
        if (_attr != 0)
        {
            _ = Native.posix_spawnattr_destroy(_attr);
            Marshal.FreeHGlobal(_attr);
            _attr = 0;
        }
        if (_sigAll != 0) { Marshal.FreeHGlobal(_sigAll); _sigAll = 0; }
        if (_sigEmpty != 0) { Marshal.FreeHGlobal(_sigEmpty); _sigEmpty = 0; }
    }
}
