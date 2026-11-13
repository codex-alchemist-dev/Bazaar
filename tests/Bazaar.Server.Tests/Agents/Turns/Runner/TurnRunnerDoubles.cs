using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Turns;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Fakes;

namespace Bazaar.Server.Tests.Agents.Turns.Runner;

/// <summary>Returns a fixed plan and records each request.</summary>
internal sealed class FakeContextAssembler : ITurnContextAssembler
{
    public List<TurnContextRequest> Requests { get; } = [];

    public ContextPlan Plan { get; set; } = new(
        [new ModelMessage(ModelRole.System, "system"), new ModelMessage(ModelRole.User, "wake")],
        """{"sections":{"system":"v1"}}""");

    public Task<ContextPlan> BuildAsync(TurnContextRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Plan);
    }
}

/// <summary>Runs each call through a handler (default: executed with a fixed result) and records the requests.</summary>
internal sealed class FakeToolDispatcher : IToolDispatcher
{
    public List<ToolDispatchRequest> Requests { get; } = [];

    public Func<ToolDispatchRequest, CancellationToken, Task<ToolDispatchResult>> Handler { get; set; } =
        (_, _) => Task.FromResult(new ToolDispatchResult(ToolDispatchOutcome.Executed, """{"ok":true}"""));

    public async Task<ToolDispatchResult> InvokeAsync(ToolDispatchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await Handler(request, cancellationToken);
    }
}

/// <summary>Records each post.</summary>
internal sealed class FakePostSink : IAgentPostSink
{
    public sealed record Post(Guid AgentId, Guid TurnId, string GcId, string Text);

    public List<Post> Posts { get; } = [];

    /// <summary>When set, the post throws this instead of recording (a refused post appends nothing).</summary>
    public Exception? ThrowOnPost { get; set; }

    public Task PostAsync(Guid agentId, Guid turnId, string gcId, string text, CancellationToken cancellationToken)
    {
        if (ThrowOnPost is { } failure)
            throw failure;
        Posts.Add(new Post(agentId, turnId, gcId, text));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Puts a <see cref="FakeProvider"/> behind <see cref="IModelGateway"/>. <c>onCall</c> runs before each call, so a test can
/// interrupt the run or steal its lease at a known point.
/// </summary>
internal sealed class RunnerGateway(FakeProvider provider, Action? onCall = null) : IModelGateway
{
    public IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, CancellationToken cancellationToken)
    {
        onCall?.Invoke();
        return provider.StreamAsync(request, cancellationToken);
    }
}
