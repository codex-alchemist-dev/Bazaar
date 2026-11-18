using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.WhatIf;

/// <summary>
/// A deterministic stand-in for the sandbox VM runner (which depends on docs 16 and 17). The measurement is derived from a hash of
/// the branch name, its tasks and the spec hash, so the same input always scores the same. It runs the same request guard a real
/// runner must, and records every request it accepted.
/// </summary>
public sealed class FakeSandboxRunner : ISandboxRunner
{
    private readonly List<SandboxRequest> _requests = [];

    /// <summary>When set, the outcome reports this spec hash instead of the request's, to simulate a runner scoring on another spec.</summary>
    public string? ReportedSpecHash { get; set; }

    /// <summary>When set, the runner throws this for every request, to simulate a failed run.</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<SandboxRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public Task<SandboxOutcome> RunAsync(SandboxRequest request, CancellationToken ct = default)
    {
        SandboxRequestGuard.EnsureValid(request);
        ct.ThrowIfCancellationRequested();
        lock (_requests)
            _requests.Add(request);
        if (FailWith is not null)
            return Task.FromException<SandboxOutcome>(FailWith);

        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', [request.Branch, request.MetricsSpecHash, .. request.Tasks])));
        var measurement = new BranchMeasurement(
            CiPassed: seed[0] % 4 != 0,
            DiffLines: (seed[1] << 8 | seed[2]) % 1500,
            Review: (ReviewBand)(seed[3] % 3),
            Tokens: 1_000 + ((seed[4] << 16 | seed[5] << 8 | seed[6]) % 900_000));
        return Task.FromResult(new SandboxOutcome(ReportedSpecHash ?? request.MetricsSpecHash, measurement, request.PushRef));
    }
}

/// <summary>A fork source that mints a fresh sandbox GC id and records the fork. For tests and development; the Time Machine adapter replaces it.</summary>
public sealed class FakeWhatIfForkSource : IWhatIfForkSource
{
    private readonly List<WhatIfFork> _forks = [];

    public IReadOnlyList<WhatIfFork> Forks
    {
        get
        {
            lock (_forks)
                return [.. _forks];
        }
    }

    public Task<WhatIfFork> ForkAsync(Guid sourceGcId, long sourceSeq, CancellationToken ct = default)
    {
        var fork = new WhatIfFork(Guid.CreateVersion7(), sourceGcId, sourceSeq, VaultCommit: null);
        lock (_forks)
            _forks.Add(fork);
        return Task.FromResult(fork);
    }
}

/// <summary>A report sink that keeps the entries it was given.</summary>
public sealed class InMemoryWhatIfReportSink : IWhatIfReportSink
{
    private readonly List<(Guid GcId, string Text)> _entries = [];

    public IReadOnlyList<(Guid GcId, string Text)> Entries
    {
        get
        {
            lock (_entries)
                return [.. _entries];
        }
    }

    public Task PostSystemEntryAsync(Guid gcId, string text, CancellationToken ct = default)
    {
        lock (_entries)
            _entries.Add((gcId, text));
        return Task.CompletedTask;
    }
}
