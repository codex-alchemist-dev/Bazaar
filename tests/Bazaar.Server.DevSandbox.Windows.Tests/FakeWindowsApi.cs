using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.DevSandbox.Windows;

namespace Bazaar.Server.DevSandbox.Windows.Tests;

/// <summary>What the fake Windows does, so each self-test probe can be made to fail.</summary>
internal sealed class FakeBehavior
{
    public bool AppContainerSupported { get; set; } = true;

    public bool KillOnCloseWorks { get; set; } = true;

    public bool ChildSpawns { get; set; } = true;

    public bool BreakawayAllowed { get; set; }

    public bool KillOnCloseFlag { get; set; } = true;

    public long? ProcessMemoryOverride { get; set; }

    public int? ActiveProcessOverride { get; set; }

    public uint? UiOverride { get; set; }

    public bool ScriptRuns { get; set; } = true;

    public bool ReadOutside { get; set; }

    public bool WriteOutside { get; set; }

    public bool WorkdirWritable { get; set; } = true;

    public bool NetworkConnects { get; set; }

    public int CurlExit { get; set; } = 7;

    public bool SpinExitsEarly { get; set; }
}

/// <summary>A fake <see cref="IWindowsConfinementApi"/> that records every call in order and can fail any of them.</summary>
internal sealed class FakeWindowsApi(FakeBehavior? behavior = null) : IWindowsConfinementApi
{
    private readonly object _gate = new();

    public FakeBehavior Behavior { get; } = behavior ?? new FakeBehavior();

    public List<string> Calls { get; } = [];

    public HashSet<string> MissingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? FailAt { get; set; }

    public bool OnWindows { get; set; } = true;

    public List<WindowsProcessRequest> Requests { get; } = [];

    public List<FakeJob> Jobs { get; } = [];

    public List<FakeProcess> Processes { get; } = [];

    public List<string> DeletedProfiles { get; } = [];

    public bool IsWindows => OnWindows;

    public bool IsAppContainerSupported() => Behavior.AppContainerSupported;

    public bool FileExists(string path) => !MissingFiles.Contains(path);

    public IWindowsJob CreateJob(WindowsJobSpec spec)
    {
        Record("CreateJob");
        var job = new FakeJob(this, spec);
        lock (_gate)
        {
            Jobs.Add(job);
        }

        return job;
    }

    public WindowsAppContainerProfile CreateAppContainerProfile(string name)
    {
        Record("CreateAppContainerProfile");
        return new WindowsAppContainerProfile(name, "S-1-15-2-1-2-3-4-5-6-7");
    }

    public void DeleteAppContainerProfile(string name)
    {
        Record("DeleteAppContainerProfile");
        lock (_gate)
        {
            DeletedProfiles.Add(name);
        }
    }

    public void GrantWorkDirectoryAccess(string directory, string? appContainerSid)
    {
        Record("GrantWorkDirectoryAccess");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("The work directory must exist before its access list is set.");
        }
    }

    public IWindowsProcess CreateProcessSuspended(WindowsProcessRequest request)
    {
        Record("CreateProcessSuspended");
        lock (_gate)
        {
            Requests.Add(request);
        }

        var process = new FakeProcess(this, request);
        lock (_gate)
        {
            Processes.Add(process);
        }

        return process;
    }

    internal void Record(string call)
    {
        lock (_gate)
        {
            Calls.Add(call);
        }

        if (FailAt == call)
        {
            throw new InvalidOperationException("injected failure at " + call);
        }
    }
}

internal sealed class FakeJob(FakeWindowsApi api, WindowsJobSpec spec) : IWindowsJob
{
    public List<FakeProcess> Assigned { get; } = [];

    public bool Disposed { get; private set; }

    public bool Terminated { get; private set; }

    public WindowsJobLimits QueryLimits()
    {
        var b = api.Behavior;
        return new WindowsJobLimits(
            b.KillOnCloseFlag,
            b.BreakawayAllowed,
            true,
            b.ActiveProcessOverride ?? spec.ActiveProcessLimit,
            b.ProcessMemoryOverride ?? spec.ProcessMemoryBytes,
            spec.JobMemoryBytes,
            spec.CpuTime,
            b.UiOverride ?? 0xFF);
    }

    public IReadOnlyList<int> ProcessIds()
    {
        var ids = Assigned.Select(p => p.Id).ToList();
        if (api.Behavior.ChildSpawns && Assigned.Any(p => p.Kind == "spin" && p.Resumed))
        {
            ids.Add(9999);
        }

        return ids;
    }

    public void Assign(IWindowsProcess process)
    {
        api.Record("Assign");
        Assigned.Add((FakeProcess)process);
    }

    public void Terminate(uint exitCode)
    {
        api.Record("TerminateJob");
        Terminated = true;
        foreach (var p in Assigned)
        {
            p.Exit((int)exitCode);
        }
    }

    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Disposed = true;
        api.Record("DisposeJob");
        if (api.Behavior.KillOnCloseWorks)
        {
            foreach (var p in Assigned)
            {
                p.Exit(1);
            }
        }
    }
}

internal sealed partial class FakeProcess : IWindowsProcess
{
    private readonly FakeWindowsApi _api;
    private readonly MemoryStream _stdout = new();
    private int _exitCode;
    private volatile bool _exited;

    public FakeProcess(FakeWindowsApi api, WindowsProcessRequest request)
    {
        _api = api;
        Request = request;
        Kind = request.CommandLine.Contains("spin.cmd", StringComparison.Ordinal) ? "spin"
            : request.CommandLine.Contains("files.cmd", StringComparison.Ordinal) ? "files"
            : request.CommandLine.Contains("curl.exe", StringComparison.Ordinal) ? "net"
            : "other";
    }

    public WindowsProcessRequest Request { get; }

    public string Kind { get; }

    public bool Resumed { get; private set; }

    public bool Disposed { get; private set; }

    public bool Terminated { get; private set; }

    public int Id { get; } = Interlocked.Increment(ref _nextId);

    private static int _nextId = 1000;

    public Stream StandardOutput => _stdout.Length == 0 && !Resumed ? new MemoryStream() : new MemoryStream(_stdout.ToArray());

    public Stream StandardError { get; } = new MemoryStream();

    public bool HasExited => _exited;

    public int ExitCode => _exitCode;

    public void Resume()
    {
        _api.Record("Resume");
        Resumed = true;
        Behave();
    }

    public void Terminate(uint exitCode)
    {
        _api.Record("TerminateProcess");
        Terminated = true;
        Exit((int)exitCode);
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_exited)
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(5, ct);
        }

        return true;
    }

    public void Dispose()
    {
        if (!Disposed)
        {
            Disposed = true;
            _api.Record("DisposeProcess");
        }
    }

    internal void Exit(int code)
    {
        if (!_exited)
        {
            _exitCode = code;
            _exited = true;
        }
    }

    private void Behave()
    {
        var b = _api.Behavior;
        switch (Kind)
        {
            case "spin":
                if (b.SpinExitsEarly)
                {
                    Exit(5);
                }

                break;
            case "files":
                RunFiles(b);
                break;
            case "net":
                RunNet(b);
                break;
            default:
                Write("hello\n");
                Exit(0);
                break;
        }
    }

    private void RunFiles(FakeBehavior b)
    {
        if (!b.ScriptRuns)
        {
            Exit(1);
            return;
        }

        var script = File.ReadAllText(Path.Combine(Request.WorkingDirectory, "files.cmd"));
        var canary = TypeLine().Match(script).Groups[1].Value;
        var canaryDir = Path.GetDirectoryName(canary)!;
        var sb = new StringBuilder("PROBE=started\r\n");
        if (b.ReadOutside)
        {
            sb.Append(File.ReadAllText(canary)).Append("\r\nOUTSIDE_READ=read\r\n");
        }
        else
        {
            sb.Append("OUTSIDE_READ=denied\r\n");
        }

        if (b.WriteOutside)
        {
            File.WriteAllText(Path.Combine(canaryDir, "written.txt"), "leak");
        }

        if (b.WorkdirWritable)
        {
            File.WriteAllText(Path.Combine(Request.WorkingDirectory, "probe.txt"), "ok");
            sb.Append("ok\r\n");
        }

        Write(sb.ToString());
        Exit(0);
    }

    private void RunNet(FakeBehavior b)
    {
        if (b.NetworkConnects)
        {
            var m = UrlPattern().Match(Request.CommandLine);
            using var client = new TcpClient();
            client.Connect(m.Groups[1].Value, int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            Exit(0);
            return;
        }

        Exit(b.CurlExit);
    }

    private void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _stdout.Write(bytes, 0, bytes.Length);
    }

    [GeneratedRegex("type \"([^\"]+)\"")]
    private static partial Regex TypeLine();

    [GeneratedRegex(@"http://([0-9.]+):(\d+)/")]
    private static partial Regex UrlPattern();
}
