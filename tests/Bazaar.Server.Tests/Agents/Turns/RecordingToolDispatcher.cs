using Bazaar.Server.Agents.Turns;

namespace Bazaar.Server.Tests.Agents.Turns;

/// <summary>
/// Dispatcher fake for the turn runner. It records every request, so a test can see which calls reached the dispatcher, and
/// answers each one with <paramref name="answer"/>. The default answer runs the call and returns a small result.
/// </summary>
internal sealed class RecordingToolDispatcher(Func<ToolDispatchRequest, ToolDispatchResult>? answer = null) : IToolDispatcher
{
    private readonly Func<ToolDispatchRequest, ToolDispatchResult> _answer =
        answer ?? (_ => new ToolDispatchResult(ToolDispatchOutcome.Executed, """{"ok":true}"""));

    public List<ToolDispatchRequest> Requests { get; } = [];

    public Task<ToolDispatchResult> InvokeAsync(ToolDispatchRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_answer(request));
    }
}
