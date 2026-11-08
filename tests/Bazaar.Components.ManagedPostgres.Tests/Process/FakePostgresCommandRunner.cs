using System.Net;
using System.Net.Sockets;

namespace Bazaar.Components.ManagedPostgres.Tests;

/// <summary>Scripted command runner. Tool runs are recorded and answered by OnRun; spawned children are recorded.</summary>
internal sealed class FakePostgresCommandRunner : IPostgresCommandRunner
{
    private readonly List<FakePostgresChild> _children = [];
    private readonly List<(string FileName, string[] Arguments)> _runs = [];
    private readonly List<string[]> _startArguments = [];
    private readonly HashSet<int> _alivePids = [];
    private int _nextPid = 4000;
    private int _attachCount;

    public Func<string, IReadOnlyList<string>, PostgresCommandResult> OnRun { get; set; } =
        (_, _) => new PostgresCommandResult(0, string.Empty);

    public Action<FakePostgresChild>? OnChildStarted { get; set; }

    public IReadOnlyList<FakePostgresChild> Children => _children;

    public IReadOnlyList<(string FileName, string[] Arguments)> Runs => _runs;

    public IReadOnlyList<string[]> StartArguments => _startArguments;

    public int AttachCount => _attachCount;

    public HashSet<int> AlivePids => _alivePids;

    public Task<PostgresCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        _runs.Add((fileName, arguments.ToArray()));
        return Task.FromResult(OnRun(fileName, arguments));
    }

    public IPostgresChild Start(string fileName, IReadOnlyList<string> arguments)
    {
        _startArguments.Add(arguments.ToArray());
        var child = new FakePostgresChild(_nextPid++);
        _children.Add(child);
        OnChildStarted?.Invoke(child);
        return child;
    }

    public IPostgresChild Attach(int processId)
    {
        _attachCount++;
        var child = new FakePostgresChild(processId);
        _children.Add(child);
        return child;
    }

    public bool IsProcessAlive(int processId) => _alivePids.Contains(processId);
}

/// <summary>Fake child. Exit is driven by the test; Kill exits the child when ExitsOnKill is true.</summary>
internal sealed class FakePostgresChild(int processId) : IPostgresChild
{
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ProcessId { get; } = processId;

    public bool HasExited => _exit.Task.IsCompleted;

    public bool ExitsOnKill { get; set; } = true;

    public int KillCount { get; private set; }

    public Action? OnExited { get; set; }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

    public void Exit(int exitCode)
    {
        if (_exit.TrySetResult(exitCode))
        {
            OnExited?.Invoke();
        }
    }

    public void Kill()
    {
        KillCount++;
        if (ExitsOnKill)
        {
            Exit(-1);
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Temp folders and loopback ports for the process tests.</summary>
internal static class ProcessTestSupport
{
    public static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "bazaar-pg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static PostgresProcessOptions OptionsFor(string dataDirectory, int port) => new()
    {
        BinDirectory = "/fake/pg/bin",
        DataDirectory = dataDirectory,
        Port = port,
        StartupTimeout = TimeSpan.FromSeconds(5),
        ShutdownTimeout = TimeSpan.FromMilliseconds(300),
        RestartBaseDelay = TimeSpan.FromMilliseconds(10),
        RestartMaxDelay = TimeSpan.FromSeconds(1),
    };

    public static void MarkAsCluster(string dataDirectory) => File.WriteAllText(Path.Combine(dataDirectory, "PG_VERSION"), "18\n");

    public static void WritePostmasterPid(string dataDirectory, int pid, int port) =>
        File.WriteAllText(
            Path.Combine(dataDirectory, "postmaster.pid"),
            $"{pid}\n{dataDirectory}\n1700000000\n{port}\n\n127.0.0.1\n0\nready\n");

    /// <summary>A listener that stands in for a running postgres on the port.</summary>
    public static TcpListener ListenOn(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return listener;
    }
}
