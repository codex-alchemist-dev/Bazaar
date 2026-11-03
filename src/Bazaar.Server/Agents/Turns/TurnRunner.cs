using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gateway;
using Bazaar.Server.Gateway.Ai;
using Npgsql;

namespace Bazaar.Server.Agents.Turns;

/// <summary>
/// The turn loop over the model gateway (doc 06, "Turn loop" and "Concurrency and recovery"). One run takes the agent's
/// lease, starts a turn, claims due inbox items, and calls the model step by step. Each step is written pending before the
/// call and completed with the response before the turn acts on it. A final message completes the turn; a tool call, a
/// stop reason other than a final message or a continuation, the step cap, the token ceiling or the wall clock fails it.
/// <para>
/// A failed or cancelled run returns its claimed items to pending. The lease is released in every case. Before a model
/// call, a completed step with the same request hash from an abandoned turn of the same agent is reused, so recovery does
/// not call the model twice for one request. Tool dispatch is not part of this runner (WP6): a tool call fails the turn.
/// </para>
/// </summary>
public sealed class TurnRunner
{
    /// <summary>The user message sent when the model stops at its output limit, so it continues the same answer.</summary>
    private const string _continueMessage = "Continue from where you stopped.";

    private readonly TurnRunnerOptions _options;
    private readonly IModelGateway _gateway;
    private readonly LeaseRepository _leases;
    private readonly TurnRepository _turns;
    private readonly TurnStepRepository _steps;
    private readonly InboxRepository _inbox;

    /// <exception cref="ArgumentException">The options are inconsistent (see <see cref="TurnRunnerOptions.Validate"/>).</exception>
    public TurnRunner(NpgsqlDataSource dataSource, IModelGateway gateway, TurnRunnerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(gateway);
        _options = options ?? new TurnRunnerOptions();
        _options.Validate();
        _gateway = gateway;
        _leases = new LeaseRepository(dataSource);
        _turns = new TurnRepository(dataSource);
        _steps = new TurnStepRepository(dataSource);
        _inbox = new InboxRepository(dataSource);
    }

    /// <summary>
    /// Runs one turn for the agent. Returns <see cref="TurnOutcome.NotAcquired"/> without writing anything when another
    /// holder has the lease. Returns <see cref="TurnOutcome.Failed"/> for model, limit and tool-call failures. Cancelling
    /// <paramref name="cancellationToken"/> fails the turn, releases its claims and lease, and then throws
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<TurnResult> RunAsync(TurnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WakeReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route);
        ArgumentOutOfRangeException.ThrowIfNegative(request.InboxClaimLimit);
        if (request.MessageBuilder is null && request.Messages.Count == 0)
            throw new ArgumentException("A turn needs at least one message.", nameof(request));

        var holder = $"turn-runner:{Guid.CreateVersion7():N}";
        if (!await _leases.TryAcquireAsync(request.AgentId, holder, _options.LeaseDuration, cancellationToken).ConfigureAwait(false))
            return TurnResult.NotAcquired;

        var progress = new Progress(holder);

        // The wall clock is a linked token, so a model call in flight is cancelled when it passes, and an external cancel still
        // reaches the call. A cancel that is not the caller's therefore means the wall clock passed.
        using var wallClock = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wallClock.CancelAfter(_options.WallClock);
        try
        {
            return await RunTurnAsync(request, progress, wallClock.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await FailAsync(progress, "wall clock exceeded", TurnEndReason.WallClock).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(progress, Describe(ex), ex is GatewayModelException ? TurnEndReason.GatewayFailed : null).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                // An external cancel, or an exception the catches above did not handle, leaves the turn open. Fail it here.
                if (!progress.Finished)
                    await FailAsync(progress, "cancelled").ConfigureAwait(false);
            }
            finally
            {
                await _leases.ReleaseAsync(request.AgentId, holder, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<TurnResult> RunTurnAsync(TurnRequest request, Progress progress, CancellationToken ct)
    {
        var turnId = await _turns.StartAsync(
            new TurnStart(request.AgentId, request.SessionId, request.Role, request.WakeReason), ct).ConfigureAwait(false);
        progress.TurnId = turnId;

        if (request.InboxClaimLimit > 0)
        {
            var rows = await _inbox.ClaimAsync(request.AgentId, progress.Holder, request.InboxClaimLimit, ct).ConfigureAwait(false);
            progress.ClaimedIds.AddRange(rows.Select(r => r.InboxItemId));
        }

        var tools = request.Tools ?? [];
        // A message builder runs once the turn row exists, so the context manifest can record the turn id (doc 06).
        var messages = request.MessageBuilder is null
            ? request.Messages.ToList()
            : (await request.MessageBuilder(turnId, ct).ConfigureAwait(false)).ToList();
        if (messages.Count == 0)
            return await FailAsync(progress, "the message builder returned no messages").ConfigureAwait(false);
        var text = new StringBuilder();
        long spent = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // An override lowers the ceiling for this run only; it never raises the configured cap.
            var stepCap = request.StepCapOverride is { } overrideCap ? Math.Min(overrideCap, _options.StepCap) : _options.StepCap;
            if (progress.ModelSteps >= stepCap)
                return await FailAsync(progress, $"step cap of {stepCap} reached", TurnEndReason.StepCap).ConfigureAwait(false);

            var hash = ModelRequestHash.Compute(request.Route, messages, tools);
            var (response, reused) = await CallModelAsync(request, progress, messages, tools, hash, ct).ConfigureAwait(false);

            // A reused response was paid for by the abandoned turn, so only a new call counts against the ceiling.
            if (!reused)
                spent += response.Tokens;
            if (spent > _options.TokenCeiling)
                return await FailAsync(progress, $"token ceiling of {_options.TokenCeiling} exceeded", TurnEndReason.TokenCeiling).ConfigureAwait(false);

            if (response.ToolCalls.Count > 0)
                return await FailAsync(progress, $"model requested tool '{response.ToolCalls[0].Name}'; tool dispatch is not available").ConfigureAwait(false);

            text.Append(response.Text);
            switch (Enum.Parse<StopReason>(response.StopReason))
            {
                case StopReason.EndTurn:
                    return await CompleteAsync(progress, text.ToString()).ConfigureAwait(false);

                case StopReason.MaxTokens:
                    // The answer was cut at the output limit. Ask for the rest; the step cap bounds how often that can happen.
                    messages.Add(new ModelMessage(ModelRole.Assistant, response.Text));
                    messages.Add(new ModelMessage(ModelRole.User, _continueMessage));
                    break;

                default:
                    return await FailAsync(progress, $"model stopped with {response.StopReason}").ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// One model step. A matching completed step from an abandoned turn is reused and the gateway is not called. Otherwise the
    /// step is written pending, the gateway streams the response, and the completed response is stored before it is returned.
    /// </summary>
    private async Task<(ModelStepResponse Response, bool Reused)> CallModelAsync(
        TurnRequest request,
        Progress progress,
        IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        string hash,
        CancellationToken ct)
    {
        var turnId = progress.TurnId!.Value;
        var reusedJson = await _steps.FindAbandonedModelResponseAsync(request.AgentId, hash, ct).ConfigureAwait(false);

        var n = await _steps.BeginAsync(turnId, TurnStepKind.Model, hash, ct: ct).ConfigureAwait(false);
        progress.PendingStep = n;
        progress.ModelSteps++;

        if (reusedJson is not null)
        {
            var reused = JsonSerializer.Deserialize(reusedJson, TurnJsonContext.Default.ModelStepResponse)
                ?? throw new InvalidOperationException("A reused model response is empty.");
            await CompleteStepAsync(turnId, n, reusedJson, ct).ConfigureAwait(false);
            progress.ReusedSteps++;
            return (reused, true);
        }

        var modelRequest = new ModelRequest(request.AgentId.ToString("D"), request.Route, messages, tools, turnId.ToString("D"));
        var response = await ReadResponseAsync(modelRequest, ct).ConfigureAwait(false);
        await CompleteStepAsync(turnId, n, JsonSerializer.Serialize(response, TurnJsonContext.Default.ModelStepResponse), ct).ConfigureAwait(false);
        return (response, false);
    }

    /// <summary>Reads one streamed model response into a <see cref="ModelStepResponse"/>. A failure event or a broken stream throws.</summary>
    private async Task<ModelStepResponse> ReadResponseAsync(ModelRequest request, CancellationToken ct)
    {
        var text = new StringBuilder();
        var calls = new List<PendingToolCall>();
        StopReason? stop = null;
        var served = "";
        long tokens = 0;

        await foreach (var modelEvent in _gateway.CallAsync(request, ct).ConfigureAwait(false))
        {
            switch (modelEvent)
            {
                case ModelEvent.TextDelta delta:
                    text.Append(delta.Text);
                    break;

                case ModelEvent.ToolCallStart start:
                    calls.Add(new PendingToolCall(start.CallId, start.Name));
                    break;

                case ModelEvent.ToolCallArgsDelta args:
                    FindCall(calls, args.CallId).Arguments.Append(args.ArgumentsFragment);
                    break;

                case ModelEvent.ToolCallEnd end:
                    FindCall(calls, end.CallId).Ended = true;
                    break;

                case ModelEvent.UsageReported usage:
                    var u = usage.Usage;
                    tokens += u.Input + u.CacheWrite5m + u.CacheWrite1h + u.CacheRead + u.Output;
                    break;

                case ModelEvent.Failure failure:
                    throw new GatewayModelException(failure.Code, failure.Message, failure.RetryAfterSeconds);

                case ModelEvent.Done done:
                    stop = done.StopReason;
                    served = done.ServedModel;
                    break;

                case ModelEvent.ThinkingDelta:
                case ModelEvent.Warning:
                    // Reasoning is not stored in the turn; Thinking GCs are a separate writer (doc 06). Warnings do not change the response.
                    break;
            }
        }

        if (stop is null)
            throw new GatewayModelException("stream_dropped", "The stream ended without a done event.");
        if (calls.Any(c => !c.Ended))
            throw new GatewayModelException("stream_dropped", "The stream ended while a tool call was open.");

        return new ModelStepResponse(
            text.ToString(),
            stop.Value.ToString(),
            served,
            calls.Select(c => new ModelStepToolCall(c.CallId, c.Name, c.Arguments.ToString())).ToList(),
            tokens);
    }

    private async Task CompleteStepAsync(Guid turnId, int n, string responseJson, CancellationToken ct)
    {
        if (!await _steps.CompleteAsync(turnId, n, responseJson, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Model step {n} of turn {turnId:D} was not pending.");
    }

    /// <summary>Completes the turn: claimed items become done, the turn becomes completed. The caller releases the lease.</summary>
    private async Task<TurnResult> CompleteAsync(Progress progress, string finalText)
    {
        // The commit runs without the caller's token: a model answer already received must not be lost to a late cancel.
        var turnId = progress.TurnId!.Value;
        await _inbox.CompleteAsync(progress.Holder, progress.ClaimedIds, CancellationToken.None).ConfigureAwait(false);
        await _turns.EndAsync(turnId, TurnStatus.Completed, CancellationToken.None).ConfigureAwait(false);
        progress.Finished = true;
        return Result(TurnOutcome.Completed, progress, finalText, null, TurnEndReason.FinalMessage);
    }

    /// <summary>
    /// Fails the turn: a pending model step is marked abandoned, the turn is marked failed, and claimed items return to pending.
    /// Runs with no cancellation, so a cancelled run is still cleaned up. The flag is set first, so a failing cleanup is not retried.
    /// </summary>
    private async Task<TurnResult> FailAsync(Progress progress, string reason, TurnEndReason? endReason = null)
    {
        progress.Finished = true;
        if (progress.TurnId is { } turnId)
        {
            if (progress.PendingStep is { } n)
                await _steps.MarkUnfinishedAsync(turnId, n, TurnStepStatus.Abandoned, CancellationToken.None).ConfigureAwait(false);
            await _turns.EndAsync(turnId, TurnStatus.Failed, CancellationToken.None).ConfigureAwait(false);
        }

        await _inbox.ReleaseAsync(progress.Holder, CancellationToken.None).ConfigureAwait(false);
        return Result(TurnOutcome.Failed, progress, null, reason, endReason);
    }

    private static TurnResult Result(
        TurnOutcome outcome, Progress progress, string? finalText, string? reason, TurnEndReason? endReason = null) =>
        new(outcome, progress.TurnId, finalText, reason, progress.ModelSteps, progress.ReusedSteps, progress.ClaimedIds.Count, endReason);

    private static PendingToolCall FindCall(List<PendingToolCall> calls, string callId) =>
        calls.LastOrDefault(c => c.CallId == callId && !c.Ended)
        ?? throw new GatewayModelException("provider_error", $"Tool call '{callId}' was not started.");

    private static string Describe(Exception ex) => ex is GatewayModelException gateway
        ? $"model call failed ({gateway.Code})"
        : $"turn error ({ex.GetType().Name})";

    /// <summary>Mutable state of one run, shared by the steps so the cleanup knows what to undo.</summary>
    private sealed class Progress(string holder)
    {
        public string Holder { get; } = holder;

        public Guid? TurnId { get; set; }

        public List<Guid> ClaimedIds { get; } = [];

        /// <summary>The model step written pending and not yet completed, if any.</summary>
        public int? PendingStep { get; set; }

        public int ModelSteps { get; set; }

        public int ReusedSteps { get; set; }

        /// <summary>Set once the turn has been completed or failed, so the cleanup in <see cref="RunAsync"/> runs once.</summary>
        public bool Finished { get; set; }
    }

    private sealed class PendingToolCall(string callId, string name)
    {
        public string CallId { get; } = callId;

        public string Name { get; } = name;

        public StringBuilder Arguments { get; } = new();

        public bool Ended { get; set; }
    }
}
