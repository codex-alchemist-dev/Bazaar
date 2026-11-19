using Bazaar.Server.BrowserPool.LinuxHost;

namespace Bazaar.Server.BrowserPool.LinuxHost.Tests;

/// <summary>A scripted <see cref="IProcessRunner"/>: records every request and answers from a handler.</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public const string ListingMarker = "list";

    private readonly Func<ProcessRequest, ProcessResult> _handler;

    public FakeProcessRunner(Func<ProcessRequest, ProcessResult> handler) => _handler = handler;

    public List<ProcessRequest> Runs { get; } = [];

    public List<FakeRunning> Started { get; } = [];

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Runs.Add(request);
        return Task.FromResult(_handler(request));
    }

    /// <summary>When set, <see cref="Start"/> throws it (a launch that fails at spawn).</summary>
    public Exception? StartFailure { get; set; }

    public IRunningProcess Start(ProcessRequest request)
    {
        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        var running = new FakeRunning(1000 + Started.Count, request);
        Started.Add(running);
        return running;
    }

    public static ProcessResult Ok(string stdout = "") => new(0, stdout, string.Empty, false);

    public static ProcessResult Fail(int code, string stderr = "") => new(code, string.Empty, stderr, false);

    internal sealed class FakeRunning : IRunningProcess
    {
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeRunning(int id, ProcessRequest request)
        {
            Id = id;
            Request = request;
        }

        public int Id { get; }

        public ProcessRequest Request { get; }

        public int Kills { get; private set; }

        public Task<int> Exited => _exit.Task;

        public string StdErr => string.Empty;

        public void Exit(int code) => _exit.TrySetResult(code);

        public void KillTree()
        {
            Kills++;
            _exit.TrySetResult(137);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
