using Bazaar.Server.CodingBench.Abstractions;

namespace Bazaar.Server.CodingBench.Tests;

/// <summary>Agent runner with a scripted outcome, call counter and concurrency tracking. An optional delay makes overlap visible.</summary>
internal sealed class FakeAgentRunner : ICodingBenchAgentRunner
{
    private readonly Func<CodingRunRequest, Task<CodingRunOutcome>>? _behaviour;
    private readonly TimeSpan _delay;
    private int _calls;
    private int _running;
    private int _maxRunning;

    public FakeAgentRunner(TimeSpan? delay = null, Func<CodingRunRequest, Task<CodingRunOutcome>>? behaviour = null)
    {
        _delay = delay ?? TimeSpan.Zero;
        _behaviour = behaviour;
    }

    public int Calls => Volatile.Read(ref _calls);

    public int MaxConcurrent => Volatile.Read(ref _maxRunning);

    public List<CodingRunRequest> Requests { get; } = [];

    private readonly object _lock = new();

    public async Task<CodingRunOutcome> RunAsync(CodingRunRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _maxRunning, running);
        try
        {
            lock (_lock)
            {
                Requests.Add(request);
            }

            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, ct).ConfigureAwait(false);
            }

            return _behaviour is null ? FakeOutcomes.Clean() : await _behaviour(request).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}

/// <summary>Verifier with scripted pass or fail per request, a call counter and concurrency tracking.</summary>
internal sealed class FakeVerifier : ICodingBenchVerifier
{
    private readonly Func<CodingRunRequest, bool> _script;
    private int _calls;

    public FakeVerifier(Func<CodingRunRequest, bool>? script = null)
    {
        _script = script ?? (_ => true);
    }

    public int Calls => Volatile.Read(ref _calls);

    public Task<bool> VerifyAsync(CodingRunRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_script(request));
    }
}

/// <summary>Clock that advances a fixed step per read, so measured durations are exact.</summary>
internal sealed class SteppedTimeProvider : TimeProvider
{
    private readonly long _step;
    private long _ticks;

    public SteppedTimeProvider(long step)
    {
        _step = step;
    }

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Add(ref _ticks, _step);
}

internal static class FakeOutcomes
{
    /// <summary>A clean, successful-looking outcome with no cheating and no malformed edits.</summary>
    public static CodingRunOutcome Clean(bool claimedSuccess = true) => new(
        ClaimedSuccess: claimedSuccess,
        EditsAttempted: 2,
        EditsMalformed: 0,
        TestCountDropped: false,
        SkipAdded: false,
        TestFileEdited: false,
        FlaggedWrongTest: false,
        ReportedImpossible: false,
        FollowedInjection: false,
        TimedOut: false,
        InputTokens: 1000,
        OutputTokens: 200,
        Turns: 3,
        CostUsd: 0.01m,
        CostSource: "anthropic-api");
}

internal static class FakeTasks
{
    /// <summary>Builds <paramref name="count"/> tasks with stable ids. Kinds cycle through the mix, and every trap gets the expected outcome.</summary>
    public static List<CodingTask> Make(int count)
    {
        var kinds = new[]
        {
            CodingTaskKind.BugFix,
            CodingTaskKind.FeatureWithTest,
            CodingTaskKind.Refactor,
            CodingTaskKind.AvaloniaHeadlessUi,
            CodingTaskKind.GodotCheck,
            CodingTaskKind.LuauLintFix,
        };

        var tasks = new List<CodingTask>(count);
        for (var index = 0; index < count; index++)
        {
            var kind = kinds[index % kinds.Length];
            tasks.Add(new CodingTask(
                Id: $"task-{index:D3}",
                Kind: kind,
                Title: $"Task {index}",
                Prompt: "Make the test pass.",
                BaseCommit: "abc123",
                VerifyCommand: "dotnet test",
                Expected: ExpectedOutcome.Pass));
        }

        return tasks;
    }
}
