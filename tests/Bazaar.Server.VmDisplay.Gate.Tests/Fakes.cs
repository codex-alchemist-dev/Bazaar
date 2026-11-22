using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Gate.Tests;

internal enum Behaviour
{
    Normal,
    Throw,
    Never,
    Cancelled,
}

internal sealed class FakeKillSwitch : IKillSwitchReader
{
    public bool Frozen { get; set; }

    public Behaviour Mode { get; set; }

    public int Calls { get; private set; }

    public ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken ct = default)
    {
        Calls++;
        return Fake.Run(Mode, Frozen);
    }

    public ValueTask<KillSwitchDecision> EvaluateAsync(string agentId, ToolEffect effect, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

internal sealed class FakeTaint : IAgentSessionTaintReader
{
    public TaintState State { get; set; }

    public Behaviour Mode { get; set; }

    public int Calls { get; private set; }

    public ValueTask<TaintState> GetActiveTaintAsync(string agentId, CancellationToken ct = default)
    {
        Calls++;
        return Fake.Run(Mode, State);
    }
}

internal sealed class FakeMatcher : ILeasedCredentialMatcher
{
    public string? Secret { get; set; }

    public Behaviour Mode { get; set; }

    public List<string> Seen { get; } = [];

    public ValueTask<bool> ContainsLeasedCredentialAsync(string agentId, string text, CancellationToken ct = default)
    {
        Seen.Add(text);
        return Fake.Run(Mode, Secret is not null && text.Contains(Secret, StringComparison.Ordinal));
    }
}

internal sealed class FakeClassifier : IBrowserSessionClassifier
{
    public BrowserSessionClass Class { get; set; }

    public Behaviour Mode { get; set; }

    public ValueTask<BrowserSessionClass> ClassifyAsync(DisplayTarget target, CancellationToken ct) => Fake.Run(Mode, Class);
}

internal sealed class FakeApproval : IDisplayInputApproval
{
    public bool Approve { get; set; }

    public Behaviour Mode { get; set; }

    public int Calls { get; private set; }

    public ValueTask<bool> IsApprovedAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        Calls++;
        return Fake.Run(Mode, Approve);
    }
}

internal static class Fake
{
    // Never: a read that ignores its token and does not complete, so only the gate's own timeout or the caller's token ends the wait.
    public static ValueTask<T> Run<T>(Behaviour mode, T value) => mode switch
    {
        Behaviour.Normal => ValueTask.FromResult(value),
        Behaviour.Throw => throw new InvalidOperationException("reader exploded with private detail"),
        Behaviour.Cancelled => throw new OperationCanceledException(),
        _ => new ValueTask<T>(new TaskCompletionSource<T>().Task),
    };
}
