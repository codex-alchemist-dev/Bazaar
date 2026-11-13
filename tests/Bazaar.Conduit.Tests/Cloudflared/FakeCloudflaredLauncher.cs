using Bazaar.Conduit.Cloudflared;

namespace Bazaar.Conduit.Tests.Cloudflared;

/// <summary>One recorded call to <see cref="FakeCloudflaredLauncher.Start"/>.</summary>
internal sealed record LaunchCall(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>Launcher that records calls and hands out <see cref="FakeCloudflaredProcess"/> instances.</summary>
internal sealed class FakeCloudflaredLauncher : ICloudflaredLauncher
{
    private readonly List<LaunchCall> _calls = [];
    private readonly List<FakeCloudflaredProcess> _processes = [];
    private readonly Lock _gate = new();

    /// <summary>When set, <see cref="Start"/> throws this exception instead of starting a process.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<LaunchCall> Calls
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

    public FakeCloudflaredProcess Process(int index)
    {
        lock (_gate)
        {
            return _processes[index];
        }
    }

    public ICloudflaredProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        lock (_gate)
        {
            _calls.Add(new LaunchCall(fileName, arguments.ToArray(), new Dictionary<string, string>(environment)));
            if (FailWith is not null)
            {
                throw FailWith;
            }

            var process = new FakeCloudflaredProcess();
            _processes.Add(process);
            return process;
        }
    }
}
