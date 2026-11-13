using System.Threading.Channels;
using Bazaar.Conduit.Ssh;

namespace Bazaar.Conduit.Tests.Ssh;

/// <summary>One recorded call to <see cref="FakeSshProcessLauncher.Start"/>.</summary>
internal sealed record SshLaunchCall(string FileName, IReadOnlyList<string> Arguments);

/// <summary>Launcher that records calls and hands out <see cref="FakeSshProcess"/> instances.</summary>
internal sealed class FakeSshProcessLauncher : ISshProcessLauncher
{
    private readonly List<SshLaunchCall> _calls = [];
    private readonly List<FakeSshProcess> _processes = [];
    private readonly Lock _gate = new();

    /// <summary>When set, <see cref="Start"/> throws this exception instead of starting a process.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<SshLaunchCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToList();
            }
        }
    }

    public int StartCount
    {
        get
        {
            lock (_gate)
            {
                return _processes.Count;
            }
        }
    }

    public FakeSshProcess Process(int index)
    {
        lock (_gate)
        {
            return _processes[index];
        }
    }

    public ISshProcess Start(string fileName, IReadOnlyList<string> arguments)
    {
        lock (_gate)
        {
            _calls.Add(new SshLaunchCall(fileName, arguments.ToArray()));
            if (FailWith is not null)
            {
                throw FailWith;
            }

            var process = new FakeSshProcess();
            _processes.Add(process);
            return process;
        }
    }
}

/// <summary>An ssh process that the test drives: it prints stderr lines and exits when told to.</summary>
internal sealed class FakeSshProcess : ISshProcess
{
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>True once <see cref="Kill"/> was called.</summary>
    public bool Killed { get; private set; }

    public IAsyncEnumerable<string> StandardErrorLines => _stderr.Reader.ReadAllAsync();

    public Task<int> Exited => _exited.Task;

    public void Kill()
    {
        Killed = true;
        Exit(-9);
    }

    /// <summary>Prints a line on standard error.</summary>
    public void WriteStderr(string line) => _stderr.Writer.TryWrite(line);

    /// <summary>Ends the process with the given exit code.</summary>
    public void Exit(int code)
    {
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(code);
    }
}
