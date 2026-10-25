using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Spike.S11;

// One child process attached to a pseudo console. Output is drained on a
// background thread so ClosePseudoConsole can always complete.
internal sealed class PtySession : IDisposable
{
    private IntPtr _hPC;
    private IntPtr _inWrite;
    private IntPtr _outRead;
    private IntPtr _attrList;
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private FileStream? _input;
    private Thread? _reader;
    private readonly MemoryStream _output = new();
    private readonly object _gate = new();
    private long _launchTimestamp;
    private double? _firstOutputMs;

    public int ProcessId { get; private set; }
    public double? FirstOutputMs { get { lock (_gate) return _firstOutputMs; } }

    private PtySession() { }

    public static PtySession Start(string commandLine, short cols, short rows)
    {
        var s = new PtySession();
        try
        {
            // Input pipe: we write s._inWrite, ConPTY reads inRead.
            if (!Native.CreatePipe(out var inRead, out s._inWrite, IntPtr.Zero, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input)");
            // Output pipe: ConPTY writes outWrite, we read s._outRead.
            if (!Native.CreatePipe(out s._outRead, out var outWrite, IntPtr.Zero, 0))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output)");

            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(
                new Native.COORD { X = cols, Y = rows }, inRead, outWrite, 0, out s._hPC));

            // ConPTY now owns these ends; close our copies so EOF propagates.
            Native.CloseHandle(inRead);
            Native.CloseHandle(outWrite);

            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            s._attrList = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(s._attrList, 1, 0, ref size))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");
            if (!Native.UpdateProcThreadAttribute(s._attrList, 0,
                    (IntPtr)Native.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, s._hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

            var si = new Native.STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<Native.STARTUPINFOEX>();
            si.lpAttributeList = s._attrList;
            // STARTF_USESTDHANDLES with null handles: the child must use the pseudo console, not this harness's redirected stdio.
            si.StartupInfo.dwFlags = 0x00000100;

            s._reader = new Thread(s.ReadLoop) { IsBackground = true, Name = "conpty-reader" };
            s._launchTimestamp = Stopwatch.GetTimestamp();
            if (!Native.CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    Native.EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out var pi))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW: " + commandLine);

            s._hProcess = pi.hProcess;
            s._hThread = pi.hThread;
            s.ProcessId = pi.dwProcessId;
            s._reader.Start();
            s._input = new FileStream(new SafeFileHandle(s._inWrite, true), FileAccess.Write, 1, false);
        }
        catch
        {
            s.Dispose();
            throw;
        }
        return s;
    }

    private void ReadLoop()
    {
        try
        {
            using var fs = new FileStream(new SafeFileHandle(_outRead, true), FileAccess.Read, 4096, false);
            var buf = new byte[4096];
            while (true)
            {
                int n;
                try { n = fs.Read(buf, 0, buf.Length); }
                catch (IOException) { break; }
                if (n <= 0) break;
                lock (_gate)
                {
                    if (_firstOutputMs is null)
                        _firstOutputMs = Stopwatch.GetElapsedTime(_launchTimestamp).TotalMilliseconds;
                    _output.Write(buf, 0, n);
                }
            }
        }
        finally
        {
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
        _input!.Write(bytes, 0, bytes.Length);
        _input.Flush();
    }

    public void Resize(short cols, short rows)
    {
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(_hPC, new Native.COORD { X = cols, Y = rows }));
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

    public bool IsShellAlive()
    {
        return Native.WaitForSingleObject(_hProcess, 0) == Native.WAIT_TIMEOUT;
    }

    // Closes the pseudo console while the child may still be running.
    // Returns whether ClosePseudoConsole returned in time, whether the shell
    // exited on its own within 5 s, and whether the harness had to terminate it.
    public (bool closeReturned, bool shellExitedOnItsOwn) CloseAndWait()
    {
        var hpc = _hPC;
        _hPC = IntPtr.Zero;
        var closing = Task.Run(() => Native.ClosePseudoConsole(hpc));
        bool closeReturned = closing.Wait(TimeSpan.FromSeconds(15));
        bool exited = Native.WaitForSingleObject(_hProcess, 5000) == Native.WAIT_OBJECT_0;
        if (!exited)
            Native.TerminateProcess(_hProcess, 1);
        return (closeReturned, exited);
    }

    public void Dispose()
    {
        if (_hPC != IntPtr.Zero)
        {
            // Not closed through CloseAndWait: close now (may block until drained, reader is alive).
            var hpc = _hPC;
            _hPC = IntPtr.Zero;
            var closing = Task.Run(() => Native.ClosePseudoConsole(hpc));
            closing.Wait(TimeSpan.FromSeconds(15));
        }
        if (_hProcess != IntPtr.Zero && Native.WaitForSingleObject(_hProcess, 0) == Native.WAIT_TIMEOUT)
            Native.TerminateProcess(_hProcess, 1);
        if (_hProcess != IntPtr.Zero) { Native.CloseHandle(_hProcess); _hProcess = IntPtr.Zero; }
        if (_hThread != IntPtr.Zero) { Native.CloseHandle(_hThread); _hThread = IntPtr.Zero; }
        _input?.Dispose();
        _input = null;
        if (_attrList != IntPtr.Zero)
        {
            Native.DeleteProcThreadAttributeList(_attrList);
            Marshal.FreeHGlobal(_attrList);
            _attrList = IntPtr.Zero;
        }
        if (_reader is not null && _reader.IsAlive) _reader.Join(TimeSpan.FromSeconds(5));
    }
}
