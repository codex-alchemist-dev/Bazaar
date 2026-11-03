using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Agents.Data;
using Bazaar.Server.Gateway;

namespace Bazaar.Server.Agents.Turns.Runner;

/// <summary>
/// The turn loop over the model gateway seam (doc 06 "Turn loop", "Concurrency and recovery"). For one agent it takes the lease,
/// starts the turn, assembles context, then loops: call the model, persist the response, dispatch tool calls, append results.
/// It ends on a final message, a step cap, a token ceiling, the wall clock, an interrupt (cancellation), a gateway failure or a
/// lost lease. A final message is posted to the trigger GC. The inbox items are completed on a final message and released
/// otherwise. The lease is released last, and only if this holder still holds it.
/// </summary>
/// <remarks>
/// Every step is written as pending before its action and completed in place before the runner acts on its result, so a crash
/// leaves a record. A run that finds no lease returns <see cref="TurnEndReason.LeaseLost"/> with <see cref="TurnRunOutcome.TurnId"/>
/// set to <see cref="Guid.Empty"/>: no turn was started and no inbox item was touched. Cancellation during a run ends it as
/// <see cref="TurnEndReason.Interrupted"/>; cleanup then runs without the cancelled token. An unexpected exception ends the turn as
/// failed, releases the items and lease, and is rethrown.
/// </remarks>
public sealed class TurnRunner : ITurnRunner
{
    private readonly LeaseRepository _leases;
    private readonly InboxRepository _inbox;
    private readonly TurnRepository _turns;
    private readonly TurnStepRepository _steps;
    private readonly IModelGateway _gateway;
    private readonly ITurnContextAssembler _assembler;
    private readonly IToolDispatcher _dispatcher;
    private readonly IAgentPostSink _posts;
    private readonly TimeProvider _time;
    private readonly TurnRunnerOptions _options;
    private readonly HashSet<string> _idempotent;

    public TurnRunner(
        LeaseRepository leases,
        InboxRepository inbox,
        TurnRepository turns,
        TurnStepRepository steps,
        IModelGateway gateway,
        ITurnContextAssembler assembler,
        IToolDispatcher dispatcher,
        IAgentPostSink posts,
        TimeProvider time,
        TurnRunnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _leases = leases ?? throw new ArgumentNullException(nameof(leases));
        _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));
        _turns = turns ?? throw new ArgumentNullException(nameof(turns));
        _steps = steps ?? throw new ArgumentNullException(nameof(steps));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _posts = posts ?? throw new ArgumentNullException(nameof(posts));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options;
        _idempotent = new HashSet<string>(options.IdempotentTools, StringComparer.Ordinal);
    }

    public Task<TurnRunOutcome> RunAsync(TurnRunRequest request, CancellationToken cancellationToken) =>
        ClaimAndRunAsync(request, resumeTurnId: null, cancellationToken);

    /// <summary>
    /// The restart path: re-enters a turn that is still running. Completed model and tool steps are reused, and the model is
    /// called again only for the next step. Unfinished steps are settled as doc 06 gives. This is not on <see cref="ITurnRunner"/>;
    /// the host calls it. A turn that is not running, or that belongs to another agent, is rejected.
    /// </summary>
    public Task<TurnRunOutcome> ResumeAsync(Guid turnId, TurnRunRequest request, CancellationToken cancellationToken) =>
        ClaimAndRunAsync(request, turnId, cancellationToken);

    private async Task<TurnRunOutcome> ClaimAndRunAsync(TurnRunRequest request, Guid? resumeTurnId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Holder);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Route);
        if (ct.IsCancellationRequested)
            return NotStarted(TurnEndReason.Interrupted);

        try
        {
            if (!await _leases.TryAcquireAsync(request.AgentId, request.Holder, _options.LeaseDuration, ct).ConfigureAwait(false))
                return NotStarted(TurnEndReason.LeaseLost);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NotStarted(TurnEndReason.Interrupted);
        }

        return await RunHeldAsync(request, resumeTurnId, ct).ConfigureAwait(false);
    }

    private async Task<TurnRunOutcome> RunHeldAsync(TurnRunRequest request, Guid? resumeTurnId, CancellationToken ct)
    {
        var run = new RunState(request, _time.GetUtcNow() + _options.WallClock);
        Exception? failure = null;
        try
        {
            await _inbox.ClaimByIdAsync(request.AgentId, request.Holder, request.InboxItemIds, ct).ConfigureAwait(false);
            if (resumeTurnId is { } existing)
            {
                await VerifyResumableAsync(existing, request, ct).ConfigureAwait(false);
                run.TurnId = existing;
                await SettleUnfinishedAsync(existing, ct).ConfigureAwait(false);
            }
            else
            {
                run.TurnId = await _turns.StartAsync(
                    new TurnStart(request.AgentId, request.SessionId, request.Role, request.WakeReason, request.ParentTurnId, request.Depth),
                    ct).ConfigureAwait(false);
            }

            await LoadHistoryAsync(run, ct).ConfigureAwait(false);
            var plan = await _assembler.BuildAsync(
                new TurnContextRequest(
                    request.AgentId, run.TurnId, request.Role, request.WakeReason, request.InboxItemIds, request.TriggerGcId, request.TriggerGcSeq),
                ct).ConfigureAwait(false);
            if (resumeTurnId is null)
                await _turns.SetContextManifestAsync(run.TurnId, plan.ManifestJson, ct).ConfigureAwait(false);

            run.Reason = await LoopAsync(run, plan.Messages.ToList(), ct).ConfigureAwait(false);
            if (run.Reason == TurnEndReason.FinalMessage)
                await PostFinalAsync(run, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            run.Reason = TurnEndReason.Interrupted;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            // Cleanup runs without the caller's token: a cancelled run must still record its end and release its claims.
            await CommitAsync(run, request, failure is not null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _leases.ReleaseAsync(request.AgentId, request.Holder, CancellationToken.None).ConfigureAwait(false);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
        return new TurnRunOutcome(run.TurnId, run.Reason ?? throw new InvalidOperationException("A run ended without a reason."), run.Summary, run.StepCount);
    }

    /// <summary>The loop of doc 06 step 4. Returns the reason the turn ended, with the final text in <see cref="RunState.Summary"/>.</summary>
    private async Task<TurnEndReason> LoopAsync(RunState run, List<ModelMessage> messages, CancellationToken ct)
    {
        var tools = OfferedTools(run.Request.ToolAllowlist);
        while (true)
        {
            // Interrupts are checked at step boundaries (doc 06, "Concurrency and recovery").
            ct.ThrowIfCancellationRequested();
            if (_time.GetUtcNow() >= run.Deadline)
                return TurnEndReason.WallClock;

            ModelStepPayload response;
            if (run.ReplayModel.TryDequeue(out var replayed))
            {
                response = replayed;
            }
            else
            {
                var stop = await BeforeNewStepAsync(run, ct).ConfigureAwait(false);
                if (stop is not null)
                    return stop.Value;
                var called = await CallModelAsync(run, messages, tools, ct).ConfigureAwait(false);
                if (called is null)
                    return TurnEndReason.GatewayFailed;
                response = called;
            }

            run.ModelResponses++;
            run.Tokens += TotalTokens(response.Usage);
            if (response.ToolCalls.Count == 0)
            {
                run.Summary = response.Text;
                return TurnEndReason.FinalMessage;
            }

            messages.Add(new ModelMessage(ModelRole.Assistant, response.Text, ToolCalls: response.ToolCalls));
            if (run.Tokens >= _options.TokenCeiling)
                return TurnEndReason.TokenCeiling;

            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var (stop, content) = await RunToolAsync(run, call, ct).ConfigureAwait(false);
                if (stop is not null)
                    return stop.Value;
                messages.Add(new ModelMessage(ModelRole.Tool, content, ToolCallId: call.Id));
            }
        }
    }

    /// <summary>Checks the step cap and renews the lease before a new model or tool step. Null means the step may go ahead.</summary>
    private async Task<TurnEndReason?> BeforeNewStepAsync(RunState run, CancellationToken ct)
    {
        if (run.StepCount >= _options.StepCap)
            return TurnEndReason.StepCap;
        var renewed = await _leases.RenewAsync(run.Request.AgentId, run.Request.Holder, _options.LeaseDuration, ct).ConfigureAwait(false);
        return renewed ? null : TurnEndReason.LeaseLost;
    }

    /// <summary>
    /// Calls the model for one step: pending row, stream, then the completed response (or abandoned, on failure).
    /// Returns null when the call failed. The response is persisted before the runner acts on it.
    /// </summary>
    private async Task<ModelStepPayload?> CallModelAsync(
        RunState run, List<ModelMessage> messages, IReadOnlyList<ModelToolDefinition> tools, CancellationToken ct)
    {
        var snapshot = messages.ToArray();
        var n = await _steps.BeginAsync(run.TurnId, TurnStepKind.Model, TurnStepHash.ModelRequest(snapshot, tools), false, ct).ConfigureAwait(false);
        run.StepCount++;
        run.InFlight = new InFlight(n, TurnStepKind.Model, false);

        var request = new ModelRequest(run.Request.AgentId.ToString(), run.Request.Route, snapshot, tools, TurnId: run.TurnId.ToString());
        var collector = new ModelResponseCollector();
        try
        {
            await foreach (var ev in _gateway.CallAsync(request, ct).ConfigureAwait(false))
            {
                if (collector.Apply(ev))
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A stream exception is a gateway failure (doc 06 step 3). The step is marked unfinished below.
            collector.Fail();
        }

        var payload = collector.Completed ? collector.ToPayload() : null;
        if (payload is null)
        {
            await MarkUnfinishedAsync(run.TurnId, n, TurnStepStatus.Abandoned, ct).ConfigureAwait(false);
        }
        else
        {
            await CompleteStepAsync(run.TurnId, n, JsonSerializer.Serialize(payload, TurnRunnerJson.Default.ModelStepPayload), ct).ConfigureAwait(false);
        }

        run.InFlight = null;
        return payload;
    }

    /// <summary>
    /// Runs one tool call (doc 06 step 5). A completed step is reused and not dispatched again. An in-flight step that was not
    /// idempotent is never run again; the model is told it is unknown. Returns a stop reason, or the tool message content.
    /// </summary>
    private async Task<(TurnEndReason? Stop, string Content)> RunToolAsync(RunState run, ModelToolCall call, CancellationToken ct)
    {
        var hash = TurnStepHash.ToolCall(run.ModelResponses, call);
        if (run.ReplayTools.TryGetValue(hash, out var prior))
        {
            if (prior.Status == TurnStepStatus.Completed)
            {
                var done = TurnRunnerJson.Read(prior.ResponseJson, TurnRunnerJson.Default.ToolStepPayload);
                return (null, ToolContent(done.Outcome, done.ResultJson, done.Reason));
            }

            if (prior.Status is not TurnStepStatus.Abandoned || !prior.Idempotent)
                return (null, _unknownContent);
            // An abandoned idempotent call is re-issued below.
        }

        var stop = await BeforeNewStepAsync(run, ct).ConfigureAwait(false);
        if (stop is not null)
            return (stop, string.Empty);

        var idempotent = _idempotent.Contains(call.Name);
        var n = await _steps.BeginAsync(run.TurnId, TurnStepKind.Tool, hash, idempotent, ct).ConfigureAwait(false);
        run.StepCount++;
        run.InFlight = new InFlight(n, TurnStepKind.Tool, idempotent);

        var request = new ToolDispatchRequest(run.Request.AgentId, run.TurnId, run.Request.SessionId, run.Request.Role, call, idempotent);
        var result = await _dispatcher.InvokeAsync(request, ct).ConfigureAwait(false);
        await CompleteStepAsync(
            run.TurnId, n, JsonSerializer.Serialize(new ToolStepPayload(call.Id, result.Outcome, result.ResultJson, result.Reason), TurnRunnerJson.Default.ToolStepPayload), ct)
            .ConfigureAwait(false);
        run.InFlight = null;
        return (null, ToolContent(result.Outcome, result.ResultJson, result.Reason));
    }

    /// <summary>Doc 06 step 7: posts the final text to the trigger GC, once. A post already completed on this turn is not repeated.</summary>
    private async Task PostFinalAsync(RunState run, CancellationToken ct)
    {
        var gcId = run.Request.TriggerGcId;
        var text = run.Summary;
        if (gcId is null || string.IsNullOrWhiteSpace(text) || run.PostDone)
            return;

        var n = await _steps.BeginAsync(run.TurnId, TurnStepKind.Post, TurnStepHash.Post(gcId, text), false, ct).ConfigureAwait(false);
        run.StepCount++;
        run.InFlight = new InFlight(n, TurnStepKind.Post, false);
        await _posts.PostAsync(run.Request.AgentId, run.TurnId, gcId, text, ct).ConfigureAwait(false);
        await CompleteStepAsync(run.TurnId, n, JsonSerializer.Serialize(new PostStepPayload(gcId), TurnRunnerJson.Default.PostStepPayload), ct)
            .ConfigureAwait(false);
        run.InFlight = null;
        run.PostDone = true;
    }

    /// <summary>
    /// Doc 06 step 8. Settles a step still in flight, ends the turn (completed on a final message, failed otherwise) and settles the
    /// inbox items: completed on a final message, released to pending otherwise so a later wake can process them.
    /// </summary>
    private async Task CommitAsync(RunState run, TurnRunRequest request, bool failed, CancellationToken ct)
    {
        if (run.InFlight is { } flight)
        {
            run.InFlight = null;
            await MarkUnfinishedAsync(run.TurnId, flight.N, UnfinishedStatus(flight.Kind, flight.Idempotent), ct).ConfigureAwait(false);
        }

        var completed = run.Reason == TurnEndReason.FinalMessage && !failed;
        if (run.TurnId != Guid.Empty)
            await _turns.EndAsync(run.TurnId, completed ? TurnStatus.Completed : TurnStatus.Failed, ct).ConfigureAwait(false);

        if (completed)
            await _inbox.CompleteAsync(request.Holder, request.InboxItemIds, ct).ConfigureAwait(false);
        else
            await _inbox.ReleaseAsync(request.Holder, ct).ConfigureAwait(false);
    }

    /// <summary>Checks that a resumed turn exists, belongs to this agent and is still running.</summary>
    private async Task VerifyResumableAsync(Guid turnId, TurnRunRequest request, CancellationToken ct)
    {
        var turn = await _turns.GetAsync(turnId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Turn '{turnId}' does not exist.");
        if (turn.AgentId != request.AgentId)
            throw new InvalidOperationException($"Turn '{turnId}' belongs to another agent.");
        if (turn.Status != TurnStatus.Running)
            throw new InvalidOperationException($"Turn '{turnId}' is {turn.Status}; only a running turn can resume.");
    }

    /// <summary>Settles steps the crashed run left pending, with the restart rule of doc 06.</summary>
    private async Task SettleUnfinishedAsync(Guid turnId, CancellationToken ct)
    {
        foreach (var step in await _steps.ListAsync(turnId, ct).ConfigureAwait(false))
        {
            if (step.Status == TurnStepStatus.Pending)
                await MarkUnfinishedAsync(turnId, step.N, UnfinishedStatus(step.Kind, step.Idempotent), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the steps of this turn so a resumed run can reuse them. A new turn has none.</summary>
    private async Task LoadHistoryAsync(RunState run, CancellationToken ct)
    {
        var history = await _steps.ListAsync(run.TurnId, ct).ConfigureAwait(false);
        run.StepCount = history.Count;
        foreach (var step in history)
        {
            switch (step.Kind)
            {
                case TurnStepKind.Model when step.Status == TurnStepStatus.Completed:
                    run.ReplayModel.Enqueue(TurnRunnerJson.Read(step.ResponseJson, TurnRunnerJson.Default.ModelStepPayload));
                    break;
                case TurnStepKind.Tool:
                    // History is in step order, so the latest step for a hash wins.
                    run.ReplayTools[step.RequestHash] = step;
                    break;
                case TurnStepKind.Post when step.Status == TurnStepStatus.Completed:
                    run.PostDone = true;
                    break;
            }
        }
    }

    private async Task CompleteStepAsync(Guid turnId, int n, string responseJson, CancellationToken ct)
    {
        if (!await _steps.CompleteAsync(turnId, n, responseJson, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Step {n} of turn '{turnId}' is no longer pending.");
    }

    private async Task MarkUnfinishedAsync(Guid turnId, int n, TurnStepStatus status, CancellationToken ct)
    {
        if (!await _steps.MarkUnfinishedAsync(turnId, n, status, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Step {n} of turn '{turnId}' is no longer pending.");
    }

    private ModelToolDefinition[] OfferedTools(IReadOnlyList<string> allowlist)
    {
        var allowed = allowlist.ToHashSet(StringComparer.Ordinal);
        return _options.ToolCatalogue.Where(t => allowed.Contains(t.Name)).ToArray();
    }

    /// <summary>What a step still pending when a run stops becomes (doc 06): a model call or an idempotent tool is abandoned and may be re-issued.</summary>
    private static TurnStepStatus UnfinishedStatus(TurnStepKind kind, bool idempotent) =>
        kind == TurnStepKind.Model || (kind == TurnStepKind.Tool && idempotent) ? TurnStepStatus.Abandoned : TurnStepStatus.Unknown;

    private static long TotalTokens(GatewayTokenUsage usage) =>
        usage.Input + usage.CacheWrite5m + usage.CacheWrite1h + usage.CacheRead + usage.Output + usage.Thinking;

    private static string ToolContent(ToolDispatchOutcome outcome, string? resultJson, string? reason) => outcome switch
    {
        ToolDispatchOutcome.Executed => resultJson ?? "null",
        ToolDispatchOutcome.Denied => reason is null
            ? "The tool call was denied by policy and did not run."
            : $"The tool call was denied by policy and did not run: {reason}",
        ToolDispatchOutcome.PendingApproval => "The tool call needs owner approval and did not run.",
        ToolDispatchOutcome.Unknown => _unknownContent,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown tool dispatch outcome."),
    };

    private const string _unknownContent =
        "The tool call was in flight when the runtime restarted and is not idempotent, so it was not run again. " +
        "Whether it took effect is unknown; do not assume it succeeded or failed.";

    private static TurnRunOutcome NotStarted(TurnEndReason reason) => new(Guid.Empty, reason, null, 0);

    /// <summary>The state of one run: the turn, counters, the replay queues and the step in flight.</summary>
    private sealed class RunState(TurnRunRequest request, DateTimeOffset deadline)
    {
        public TurnRunRequest Request { get; } = request;
        public DateTimeOffset Deadline { get; } = deadline;
        public Guid TurnId { get; set; }
        public int StepCount { get; set; }
        public int ModelResponses { get; set; }
        public long Tokens { get; set; }
        public string? Summary { get; set; }
        public TurnEndReason? Reason { get; set; }
        public bool PostDone { get; set; }
        public InFlight? InFlight { get; set; }

        /// <summary>Completed model responses of an earlier run of this turn, in order. Each is used before the gateway is called.</summary>
        public Queue<ModelStepPayload> ReplayModel { get; } = new();

        /// <summary>Tool steps of this turn by request hash, latest step per hash.</summary>
        public Dictionary<string, TurnStepRecord> ReplayTools { get; } = new(StringComparer.Ordinal);
    }

    private readonly record struct InFlight(int N, TurnStepKind Kind, bool Idempotent);

    /// <summary>Folds gateway events into one response. A Done ends the call; a failure or a missing Done leaves it incomplete.</summary>
    private sealed class ModelResponseCollector
    {
        private readonly StringBuilder _text = new();
        private readonly List<PendingCall> _calls = [];
        private GatewayTokenUsage? _usage;
        private StopReason _stop = StopReason.Other;
        private string? _servedModel;
        private bool _done;
        private bool _failed;

        public bool Completed => _done && !_failed;

        /// <summary>Applies one event. Returns true when the stream is over.</summary>
        public bool Apply(ModelEvent ev)
        {
            switch (ev)
            {
                case ModelEvent.TextDelta text:
                    _text.Append(text.Text);
                    return false;
                case ModelEvent.ToolCallStart start:
                    _calls.Add(new PendingCall(start.CallId, start.Name, start.ProviderData));
                    return false;
                case ModelEvent.ToolCallArgsDelta args:
                    var call = _calls.Find(c => c.Id == args.CallId);
                    if (call is null)
                    {
                        _failed = true;
                        return true;
                    }

                    call.Arguments.Append(args.ArgumentsFragment);
                    return false;
                case ModelEvent.UsageReported usage:
                    _usage = usage.Usage;
                    return false;
                case ModelEvent.Failure:
                    _failed = true;
                    return true;
                case ModelEvent.Done done:
                    _done = true;
                    _stop = done.StopReason;
                    _servedModel = done.ServedModel;
                    return true;
                default:
                    // Thinking deltas count toward usage; tool-call ends and warnings need no action here.
                    return false;
            }
        }

        public void Fail() => _failed = true;

        public ModelStepPayload ToPayload() => new(
            _text.ToString(),
            _calls.Select(c => new ModelToolCall(c.Id, c.Name, c.Arguments.ToString(), c.ProviderData)).ToArray(),
            _usage ?? new GatewayTokenUsage(0, 0, 0, 0, 0, 0),
            _stop,
            _servedModel ?? string.Empty);

        private sealed record PendingCall(string Id, string Name, string? ProviderData)
        {
            public StringBuilder Arguments { get; } = new();
        }
    }
}
