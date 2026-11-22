using System.Runtime.CompilerServices;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Ai;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Policy;
using Bazaar.Server.Gateway.Secrets;
using Bazaar.Server.Gateway.Usage;

namespace Bazaar.Server.Gateway.Pipeline;

/// <summary>
/// The model gateway pipeline (docs/design/05-model-gateway.md): resolve the route against policy, then try the
/// resolved candidates in order, streaming the first one that works.
/// <para>
/// Fallback rules: a candidate is skipped (never called) when its provider is missing, disabled or has a refused
/// credential, its adapter or secret is missing, its key is quota-blocked or its breaker is open. A retryable failure
/// (<c>rate_limited</c>, <c>spend_capped</c>, <c>timeout</c>, <c>provider_error</c>, <c>stream_dropped</c>) that comes
/// before any content event is swallowed and the next candidate is tried. Once a text, thinking or tool-call event has
/// been forwarded there is no fallback: a later failure is forwarded and ends the call. Fallback uses only the
/// candidates in the resolved route.
/// </para>
/// <para>
/// Buffering: <see cref="ModelEvent.Warning"/> is forwarded at once. <see cref="ModelEvent.UsageReported"/> is held
/// and forwarded just before the terminal event of the call, so a caller sees at most one per call and a retried
/// attempt's usage never reaches it. Every attempt that reached an adapter writes exactly one usage row, also when the
/// caller cancels or disposes the enumerator early.
/// </para>
/// </summary>
public sealed class ModelGateway : IModelGateway
{
    private static readonly HashSet<string> _retryableCodes =
        ["rate_limited", "spend_capped", "timeout", "provider_error", "stream_dropped"];

    private const string _embeddingScopeSuffix = "#embedding";
    private const string _embeddingQuotaPrefix = "embedding:";

    private readonly RouteResolver _resolver;
    private readonly IGatewayDirectory _directory;
    private readonly IAdapterRegistry _adapters;
    private readonly IProviderSecretSource _secrets;
    private readonly HealthTracker _health;
    private readonly UsageRecorder _recorder;
    private readonly PriceTable? _prices;
    private readonly IQuotaTracker _quota;
    private readonly IGatewayEventSink _events;
    private readonly IRouteSuspender _suspender;
    private readonly TimeProvider _time;
    private readonly Action<Exception>? _onInternalError;
    private readonly SecretScrubber _scrubber;
    private readonly IEmbeddingProfile _embeddingProfile;
    private readonly IEmbeddingRouteSource? _embeddingRoute;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _lastPublishedAvailable = [];

    /// <param name="onInternalError">Told about a usage write or event publish that threw; the caller's stream is not affected.</param>
    /// <param name="scrubber">Replaces every resolved key in failure and warning text. A private scrubber is used when none is given.</param>
    /// <param name="embeddingProfile">The install embedding profile whose dimension every embedding is checked against. No check when none is given.</param>
    /// <param name="settings">Read for <c>gateway.embedding.route</c> when an embedding request names no route. Without it the setting counts as unset.</param>
    public ModelGateway(
        RouteResolver resolver,
        IGatewayDirectory directory,
        IAdapterRegistry adapters,
        IProviderSecretSource secrets,
        HealthTracker health,
        UsageRecorder recorder,
        PriceTable? prices = null,
        IQuotaTracker? quota = null,
        IGatewayEventSink? events = null,
        IRouteSuspender? suspender = null,
        TimeProvider? time = null,
        Action<Exception>? onInternalError = null,
        SecretScrubber? scrubber = null,
        IEmbeddingProfile? embeddingProfile = null,
        IEmbeddingRouteSource? embeddingRoute = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _prices = prices;
        _quota = quota ?? NullQuotaTracker.Instance;
        _events = events ?? NullGatewayEventSink.Instance;
        _suspender = suspender ?? NullRouteSuspender.Instance;
        _time = time ?? TimeProvider.System;
        _onInternalError = onInternalError;
        _scrubber = scrubber ?? new SecretScrubber();
        _embeddingProfile = embeddingProfile ?? NullEmbeddingProfile.Instance;
        _embeddingRoute = embeddingRoute;
    }

    /// <summary>Streams the call's events. Failure and warning text is scrubbed of every resolved key before it is yielded.</summary>
    public async IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var e in CallPipelineAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return _scrubber.ScrubEvent(e);
        }
    }

    private async IAsyncEnumerable<ModelEvent> CallPipelineAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resolution = await _resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolution is RouteResolution.Denied denied)
        {
            await RecordDeniedSafeAsync(request, denied).ConfigureAwait(false);
            yield return new ModelEvent.Failure("policy_denied", $"{denied.Reason}: {denied.Detail}");
            yield break;
        }

        var route = ((RouteResolution.Allowed)resolution).Route;
        GwRoute? delegatedRoute = route.IsDelegated
            ? await _directory.GetRouteAsync(request.Route, cancellationToken).ConfigureAwait(false)
            : null;

        var tried = new HashSet<ModelRef>();
        var excluded = new HashSet<ModelRef>();
        var skips = new List<Skip>();
        ModelEvent.Failure? lastFailure = null;

        while (route.Next(tried, excluded) is { } candidate)
        {
            var (ready, skip) = await PrepareAsync(candidate, requireEmbedding: false, cancellationToken).ConfigureAwait(false);
            if (ready is null)
            {
                excluded.Add(candidate.Model);
                skips.Add(skip!);
                continue;
            }

            tried.Add(candidate.Model);
            var key = ready.Key;
            var acc = new UsageAccumulator(
                request.AgentId,
                request.TurnId,
                candidate.Model.ProviderId,
                key.KeyRef,
                candidate.Model.ModelId,
                ready.Identity,
                _time,
                EstimateInputTokens(request));

            var settled = false;
            var contentSent = false;
            ModelEvent.UsageReported? heldUsage = null;
            ModelEvent.Failure? retryable = null;
            IAsyncEnumerator<ModelEvent>? stream = null;
            try
            {
                stream = Open(ready.Adapter, ready.Endpoint, request, cancellationToken);
                while (true)
                {
                    var next = await ReadNextAsync(stream, cancellationToken).ConfigureAwait(false);
                    // Scrubbed here, so the quota tracker, health and usage see the same text the caller does.
                    var e = _scrubber.ScrubEvent(next ?? new ModelEvent.Failure("stream_dropped", "The stream ended without a final event."));
                    acc.Observe(e);
                    _health.Observe(key, e);
                    if (e is ModelEvent.Done or ModelEvent.Failure)
                    {
                        settled = true;
                    }

                    if (e is ModelEvent.UsageReported usage)
                    {
                        heldUsage = usage;
                        continue;
                    }

                    if (e is ModelEvent.Failure failure)
                    {
                        Report(key, candidate.Family, failure);
                        if (!contentSent && _retryableCodes.Contains(failure.Code))
                        {
                            retryable = failure;
                            break;
                        }

                        if (heldUsage is not null)
                        {
                            yield return heldUsage;
                        }

                        yield return failure;
                        yield break;
                    }

                    if (e is ModelEvent.Done done)
                    {
                        if (delegatedRoute is not null
                            && DelegatedRouteAudit.Violations(delegatedRoute, done.ServedModel) is { Count: > 0 } violations)
                        {
                            yield return new ModelEvent.Warning(
                                "served_model_outside_expected_set",
                                $"Route '{delegatedRoute.Id}' served '{done.ServedModel}', outside its expected set: {string.Join(", ", violations)}.");
                            await SuspendSafeAsync(delegatedRoute.Id, violations, cancellationToken).ConfigureAwait(false);
                        }

                        PublishAvailabilityRestored(request.AgentId);
                        if (heldUsage is not null)
                        {
                            yield return heldUsage;
                        }

                        yield return done;
                        yield break;
                    }

                    contentSent |= IsContent(e);
                    yield return e;
                }
            }
            finally
            {
                if (!settled)
                {
                    // Cancelled or disposed early: release a half-open trial slot; this is not a health failure.
                    _health.Abandon(key);
                }

                await RecordSafeAsync(acc).ConfigureAwait(false);
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }

            if (retryable is not null)
            {
                // The failure just reported may have blocked this candidate, so the capacity check below sees it now.
                skips.Add(AfterFailure(candidate.Model, key, candidate.Family));
            }

            lastFailure = retryable;
        }

        if (lastFailure is not null)
        {
            PublishIfExhausted(request.AgentId, skips);
            yield return lastFailure;
            yield break;
        }

        yield return NoCapacity(request.AgentId, skips);
    }

    /// <summary>
    /// Embeds a batch through the same resolve, policy, health, quota, adapter, classify, fallback and usage path as
    /// <see cref="CallAsync"/>, over embedding models only. An omitted route is the install's
    /// <c>gateway.embedding.route</c>. The batch is split into chunks within the serving model's caps and the results are merged
    /// in input order, with one usage row per upstream request. A candidate whose adapter cannot embed is skipped like any other
    /// unusable candidate. A failure on every candidate, or on any chunk, returns nothing and is thrown as a
    /// <see cref="GatewayModelException"/> carrying the gateway's failure code.
    /// </summary>
    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        if (request.Dimensions is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Dimensions must be positive when given.");
        }

        if (request.Inputs.Count == 0)
        {
            return new EmbeddingResult([], request.Route ?? "", 0);
        }

        var routeName = request.Route;
        if (string.IsNullOrWhiteSpace(routeName))
        {
            routeName = _embeddingRoute is null
                ? ""
                : await _embeddingRoute.GetEmbeddingRouteAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(routeName))
            {
                throw new GatewayModelException(
                    "embedding_route_unset",
                    "The request names no embedding route and the setting gateway.embedding.route is empty.");
            }
        }

        var failure = await EmbedPipelineAsync(request, routeName, cancellationToken).ConfigureAwait(false);
        if (failure.Result is { } result)
        {
            return result;
        }

        var f = _scrubber.ScrubEvent(failure.Failure!) as ModelEvent.Failure ?? failure.Failure!;
        throw new GatewayModelException(f.Code, f.Message, f.RetryAfterSeconds);
    }

    private async Task<(EmbeddingResult? Result, ModelEvent.Failure? Failure)> EmbedPipelineAsync(
        EmbeddingRequest request, string routeName, CancellationToken cancellationToken)
    {
        var resolveRequest = new ModelRequest(request.AgentId, routeName, [], []);
        var resolution = await _resolver.ResolveAsync(resolveRequest, GwModelKind.Embedding, cancellationToken).ConfigureAwait(false);
        if (resolution is RouteResolution.Denied denied)
        {
            return (null, new ModelEvent.Failure("policy_denied", $"{denied.Reason}: {denied.Detail}"));
        }

        var route = ((RouteResolution.Allowed)resolution).Route;

        // Input length is checked against the caps before any call. An input no candidate accepts fails the whole call; an input
        // only some candidates refuse skips those candidates.
        var longest = request.Inputs.Max(i => i.Length);
        var caps = new Dictionary<ModelRef, EmbedCaps>();
        foreach (var candidate in route.Candidates)
        {
            caps[candidate.Model] = await CapsAsync(candidate, cancellationToken).ConfigureAwait(false);
        }

        if (caps.Values.All(c => longest > c.MaxInputChars))
        {
            return (null, new ModelEvent.Failure(
                "embedding_input_too_long",
                $"An input has {longest} characters; the largest accepted by the route is {caps.Values.Max(c => c.MaxInputChars)}."));
        }

        var expectedDimensions = await _embeddingProfile.GetDimensionsAsync(cancellationToken).ConfigureAwait(false);
        GwRoute? delegatedRoute = route.IsDelegated
            ? await _directory.GetRouteAsync(routeName, cancellationToken).ConfigureAwait(false)
            : null;

        var tried = new HashSet<ModelRef>();
        var excluded = new HashSet<ModelRef>();
        var skips = new List<Skip>();
        ModelEvent.Failure? lastFailure = null;

        while (route.Next(tried, excluded) is { } candidate)
        {
            var limit = caps[candidate.Model];
            if (longest > limit.MaxInputChars)
            {
                excluded.Add(candidate.Model);
                skips.Add(Skip.Config(candidate.Model, "an input is longer than the model accepts"));
                continue;
            }

            var (ready, skip) = await PrepareAsync(candidate, requireEmbedding: true, cancellationToken).ConfigureAwait(false);
            if (ready is null)
            {
                excluded.Add(candidate.Model);
                skips.Add(skip!);
                continue;
            }

            tried.Add(candidate.Model);
            var key = ready.Key;
            var scope = ready.Scope;
            var qclass = ready.QuotaClass;
            var vectors = new List<float[]>(request.Inputs.Count);
            long tokens = 0;
            string? served = null;
            var settledAny = false;
            ModelEvent.Failure? retryable = null;

            try
            {
                for (var start = 0; start < request.Inputs.Count; start += limit.MaxBatch)
                {
                    var chunk = request.Inputs.Skip(start).Take(limit.MaxBatch).ToList();
                    var chunkRequest = request with { Route = routeName, Inputs = chunk };
                    var estimated = (chunk.Sum(i => (long)i.Length) + 3) / 4;
                    var acc = new UsageAccumulator(
                        request.AgentId, null, candidate.Model.ProviderId, key.KeyRef, candidate.Model.ModelId, ready.Identity, _time, estimated);
                    var settled = false;
                    ModelEvent.Failure? failed = null;
                    EmbeddingOutcome.Succeeded? done = null;
                    string chunkServed = candidate.Model.ModelId;
                    try
                    {
                        EmbeddingOutcome outcome;
                        try
                        {
                            outcome = await ((IEmbeddingAdapter)ready.Adapter)
                                .EmbedAsync(ready.Endpoint, chunkRequest, cancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            outcome = new EmbeddingOutcome.Failed(AdapterFault(ex));
                        }

                        if (outcome is EmbeddingOutcome.Succeeded ok)
                        {
                            if (ok.Vectors.Count != chunk.Count)
                            {
                                outcome = new EmbeddingOutcome.Failed(new ModelEvent.Failure("provider_error", "The upstream returned the wrong number of vectors."));
                            }
                            else if (expectedDimensions is { } want && ok.Vectors.FirstOrDefault(v => v.Length != want) is { } wrong)
                            {
                                // Fail closed: the vectors are dropped here and never leave the gateway.
                                outcome = new EmbeddingOutcome.Failed(new ModelEvent.Failure(
                                    "embedding_dimension_mismatch",
                                    $"The upstream returned a vector of {wrong.Length} dimensions; the install embedding profile expects {want}."));
                            }
                        }

                        // The outcome becomes the same events a chat call produces, so usage, health and quota see one shape.
                        chunkServed = (outcome as EmbeddingOutcome.Succeeded)?.ServedModel ?? candidate.Model.ModelId;
                        ModelEvent[] events = outcome is EmbeddingOutcome.Succeeded success
                            ? [
                                new ModelEvent.UsageReported(new GatewayTokenUsage(success.InputTokens > 0 ? success.InputTokens : estimated, 0, 0, 0, 0, 0)),
                                new ModelEvent.Done(StopReason.EndTurn, chunkServed),
                            ]
                            : [((EmbeddingOutcome.Failed)outcome).Failure];

                        foreach (var raw in events)
                        {
                            var e = _scrubber.ScrubEvent(raw);
                            acc.Observe(e);
                            _health.Observe(scope, e);
                            settled |= e is ModelEvent.Done or ModelEvent.Failure;
                            failed = e as ModelEvent.Failure ?? failed;
                        }

                        done = outcome as EmbeddingOutcome.Succeeded;
                    }
                    finally
                    {
                        settledAny |= settled;
                        await RecordSafeAsync(acc).ConfigureAwait(false);
                    }

                    if (failed is not null)
                    {
                        Report(scope, qclass, failed);
                        if (!_retryableCodes.Contains(failed.Code))
                        {
                            return (null, failed);
                        }

                        retryable = failed;
                        break;
                    }

                    vectors.AddRange(done!.Vectors);
                    tokens += done.InputTokens > 0 ? done.InputTokens : estimated;
                    served ??= chunkServed;
                }
            }
            finally
            {
                if (!settledAny)
                {
                    _health.Abandon(scope);
                }
            }

            if (retryable is null)
            {
                if (delegatedRoute is not null
                    && DelegatedRouteAudit.Violations(delegatedRoute, served!) is { Count: > 0 } violations)
                {
                    await SuspendSafeAsync(delegatedRoute.Id, violations, cancellationToken).ConfigureAwait(false);
                }

                PublishAvailabilityRestored(request.AgentId);
                return (new EmbeddingResult(vectors, served!, tokens), null);
            }

            // A retryable failure on any chunk discards what earlier chunks returned: a batch is never split across models.
            skips.Add(AfterFailure(candidate.Model, scope, qclass));
            lastFailure = retryable;
        }

        if (lastFailure is not null)
        {
            PublishIfExhausted(request.AgentId, skips);
            return (null, lastFailure);
        }

        return (null, NoCapacity(request.AgentId, skips));
    }

    /// <summary>The caps for one candidate: its model row's values, else the adapter's provider defaults.</summary>
    private async ValueTask<EmbedCaps> CapsAsync(RouteCandidate candidate, CancellationToken ct)
    {
        var batch = 2048;
        var chars = 32768;
        var provider = await _directory.GetProviderAsync(candidate.Model.ProviderId, ct).ConfigureAwait(false);
        if (provider is not null && _adapters.Get(provider.Adapter) is IEmbeddingAdapter embedder)
        {
            batch = embedder.DefaultMaxBatch;
            chars = embedder.DefaultMaxInputChars;
        }

        var row = await _directory.GetModelAsync(candidate.Model, ct).ConfigureAwait(false);
        return new EmbedCaps(row?.EmbedMaxBatch ?? batch, row?.EmbedMaxInputChars ?? chars);
    }

    private readonly record struct EmbedCaps(int MaxBatch, int MaxInputChars);

    private ModelEvent.Failure NoCapacity(string agentId, List<Skip> skips)
    {
        var now = _time.GetUtcNow();
        var earliest = Earliest(skips);
        PublishIfExhausted(agentId, skips);

        int? retryAfter = earliest is { } at ? Math.Max(1, (int)Math.Ceiling((at - now).TotalSeconds)) : null;
        var detail = skips.Count == 0
            ? "The route has no usable candidate."
            : string.Join("; ", skips.Select(s => s.Describe()));
        return new ModelEvent.Failure("no_capacity", $"No candidate can take the call: {detail}.", retryAfter);
    }

    /// <summary>
    /// Publishes that the agent is unavailable, once, when every candidate is blocked by quota or health. A candidate
    /// skipped for configuration, or one that failed without being blocked, keeps the agent available.
    /// </summary>
    private void PublishIfExhausted(string agentId, List<Skip> skips)
    {
        if (skips.Count == 0 || !skips.All(s => s.Kind is SkipKind.Quota or SkipKind.Health) || !SetPublished(agentId, available: false))
        {
            return;
        }

        Publish(new AgentCapacityChanged(agentId, false, Earliest(skips), skips[^1].State ?? QuotaState.Down));
    }

    private static DateTimeOffset? Earliest(List<Skip> skips)
    {
        DateTimeOffset? earliest = null;
        foreach (var skip in skips)
        {
            if (skip.Until is { } until && (earliest is null || until < earliest))
            {
                earliest = until;
            }
        }

        return earliest;
    }

    /// <summary>How a candidate that just failed retryably stands now: blocked by its quota or breaker, or only failed.</summary>
    private Skip AfterFailure(ModelRef model, ProviderKey key, string modelClass)
    {
        if (_quota.BlockedState(key, modelClass) is { } state)
        {
            return Skip.Blocked(SkipKind.Quota, model, state, _quota.BlockedUntil(key, modelClass));
        }

        return _health.IsAvailable(key)
            ? new Skip(SkipKind.Failed, model, "its call failed", null, null)
            : Skip.Blocked(SkipKind.Health, model, QuotaState.Down, null);
    }

    private async ValueTask<(Ready? Ready, Skip? Skip)> PrepareAsync(RouteCandidate candidate, bool requireEmbedding, CancellationToken ct)
    {
        var model = candidate.Model;
        var provider = await _directory.GetProviderAsync(model.ProviderId, ct).ConfigureAwait(false);
        if (provider is not { Enabled: true })
        {
            return (null, Skip.Config(model, "the provider is missing or disabled"));
        }

        if (!CredentialKinds.TryParse(provider.AuthKind, out var kind) || !CredentialKinds.IsAcceptable(provider.AuthKind, provider.SecretRef))
        {
            return (null, Skip.Config(model, "the provider credential is refused"));
        }

        var adapter = _adapters.Get(provider.Adapter);
        if (adapter is null)
        {
            return (null, Skip.Config(model, "the provider adapter is unknown"));
        }

        if (requireEmbedding && adapter is not IEmbeddingAdapter)
        {
            return (null, Skip.Config(model, "the provider adapter cannot embed"));
        }

        if (!TryParseHeaders(provider.HeadersJson, out var headers))
        {
            return (null, Skip.Config(model, "the provider headers are invalid"));
        }

        var identity = GatewayIdentity.Native;
        var row = await _directory.GetModelAsync(model, ct).ConfigureAwait(false);
        if (row is not null)
        {
            identity = row.Identity;
        }
        else if (!candidate.Delegated)
        {
            return (null, Skip.Config(model, "the model row is missing"));
        }

        var key = new ProviderKey(model.ProviderId, provider.SecretRef ?? "none");
        var scope = requireEmbedding ? key with { KeyRef = key.KeyRef + _embeddingScopeSuffix } : key;
        var quotaClass = requireEmbedding ? _embeddingQuotaPrefix + candidate.Family : candidate.Family;
        if (_quota.BlockedState(scope, quotaClass) is { } blocked)
        {
            return (null, Skip.Blocked(SkipKind.Quota, model, blocked, _quota.BlockedUntil(scope, quotaClass)));
        }

        string? apiKey = null;
        if (kind != CredentialKind.None)
        {
            try
            {
                apiKey = await _secrets.ResolveAsync(provider.SecretRef!, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, Skip.Config(model, "the provider secret could not be read"));
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                return (null, Skip.Config(model, "the provider secret is missing"));
            }

            // Tracked before any adapter sees the key, so every later failure text can be scrubbed.
            _scrubber.Track(apiKey);
        }

        if (!_health.TryAcquire(scope))
        {
            return (null, Skip.Blocked(SkipKind.Health, model, QuotaState.Down, null));
        }

        return (new Ready(adapter, new ProviderEndpoint(provider.BaseUrl, model.ModelId, apiKey, headers), key, identity, scope, quotaClass), null);
    }

    private static IAsyncEnumerator<ModelEvent> Open(IModelAdapter adapter, ProviderEndpoint endpoint, ModelRequest request, CancellationToken ct)
    {
        try
        {
            return adapter.StreamAsync(endpoint, request, ct).GetAsyncEnumerator(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return FailedStream(ex).GetAsyncEnumerator(ct);
        }
    }

    private static async ValueTask<ModelEvent?> ReadNextAsync(IAsyncEnumerator<ModelEvent> stream, CancellationToken ct)
    {
        try
        {
            return await stream.MoveNextAsync().ConfigureAwait(false) ? stream.Current : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AdapterFault(ex);
        }
    }

    private static async IAsyncEnumerable<ModelEvent> FailedStream(Exception ex)
    {
        yield return AdapterFault(ex);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // Only the exception type is reported: its message could echo a request detail.
    private static ModelEvent.Failure AdapterFault(Exception ex) =>
        new("provider_error", $"The adapter failed ({ex.GetType().Name}).");

    private static bool IsContent(ModelEvent e) =>
        e is ModelEvent.TextDelta or ModelEvent.ThinkingDelta or ModelEvent.ToolCallStart or ModelEvent.ToolCallArgsDelta or ModelEvent.ToolCallEnd;

    private static long EstimateInputTokens(ModelRequest request)
    {
        long chars = 0;
        foreach (var message in request.Messages)
        {
            chars += message.Text.Length;
        }

        return (chars + 3) / 4;
    }

    private static bool TryParseHeaders(string json, out IReadOnlyDictionary<string, string>? headers)
    {
        headers = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                result[property.Name] = property.Value.GetString()!;
            }

            headers = result.Count == 0 ? null : result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The callback gets a scrubbed message, never the raw exception: a host logger must not see a resolved key.
    private void NotifyInternalError(Exception ex)
    {
        _onInternalError?.Invoke(new InvalidOperationException(_scrubber.Scrub($"{ex.GetType().FullName}: {ex.Message}")));
    }

    private void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure)
    {
        try
        {
            _quota.Report(key, modelClass, failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotifyInternalError(ex);
        }
    }

    private async ValueTask RecordDeniedSafeAsync(ModelRequest request, RouteResolution.Denied denied)
    {
        try
        {
            var provider = denied.Dropped.Count > 0
                ? denied.Dropped[0].Model.ProviderId
                : ModelRef.TryParse(request.Route, out var asked) ? asked.ProviderId : null;
            await _recorder.RecordDeniedAsync(
                request.AgentId, request.TurnId, request.Route, provider, UsageDenyCode(denied.Reason), _time.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NotifyInternalError(ex);
        }
    }

    private static string UsageDenyCode(DenyReason reason) => "policy_denied:" + reason switch
    {
        DenyReason.UnknownAgent => "unknown_agent",
        DenyReason.InvalidRoute => "invalid_route",
        DenyReason.NoRoute => "no_route",
        DenyReason.ModelNotAllowed => "model_not_allowed",
        DenyReason.RouteNotAllowed => "route_not_allowed",
        DenyReason.UnknownRoute => "unknown_route",
        DenyReason.ToolsNotAllowed => "tools_not_allowed",
        DenyReason.DelegatedSetExceedsPolicy => "delegated_set_exceeds_policy",
        DenyReason.CredentialRefused => "credential_refused",
        DenyReason.ProviderUnavailable => "provider_unavailable",
        DenyReason.ModelUnavailable => "model_unavailable",
        DenyReason.DeniedByTightening => "denied_by_tightening",
        DenyReason.FamilyMismatch => "family_mismatch",
        _ => "no_eligible_candidate",
    };

    private async ValueTask RecordSafeAsync(UsageAccumulator acc)
    {
        try
        {
            // Not tied to the caller's token, so a cancelled call still records as Aborted.
            await _recorder.RecordAsync(acc, _prices, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            NotifyInternalError(ex);
        }
    }

    private async ValueTask SuspendSafeAsync(string routeId, IReadOnlyList<string> violations, CancellationToken ct)
    {
        try
        {
            await _suspender.SuspendAsync(routeId, violations, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotifyInternalError(ex);
        }
    }

    private void PublishAvailabilityRestored(string agentId)
    {
        if (SetPublished(agentId, available: true))
        {
            Publish(new AgentCapacityChanged(agentId, true, null, QuotaState.Available));
        }
    }

    /// <summary>Records the last published state for the agent. True when it changed and an event should go out.</summary>
    private bool SetPublished(string agentId, bool available)
    {
        lock (_gate)
        {
            var wasAvailable = _lastPublishedAvailable.GetValueOrDefault(agentId, true);
            if (wasAvailable == available)
            {
                return false;
            }

            _lastPublishedAvailable[agentId] = available;
            return true;
        }
    }

    private void Publish(GatewayQuotaEvent e)
    {
        try
        {
            _events.Publish(e);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            NotifyInternalError(ex);
        }
    }

    /// <summary>
    /// <c>Key</c> is the provider key that usage rows name. <c>Scope</c> is the key that health is tracked under and
    /// <c>QuotaClass</c> the class quota is tracked under; both are separate for embedding, so a failing embedding endpoint does
    /// not open the chat breaker or block chat quota (doc 05, Embedding kind).
    /// </summary>
    private sealed record Ready(IModelAdapter Adapter, ProviderEndpoint Endpoint, ProviderKey Key, GatewayIdentity Identity, ProviderKey Scope, string QuotaClass);

    private enum SkipKind
    {
        Config,
        Quota,
        Health,
        // Tried in this call and failed without a block: the agent still has capacity.
        Failed,
    }

    private sealed record Skip(SkipKind Kind, ModelRef Model, string Reason, QuotaState? State, DateTimeOffset? Until)
    {
        public static Skip Config(ModelRef model, string reason) => new(SkipKind.Config, model, reason, null, null);

        public static Skip Blocked(SkipKind kind, ModelRef model, QuotaState state, DateTimeOffset? until) =>
            new(kind, model, kind == SkipKind.Health ? "its breaker is open" : $"quota state {state}", state, until);

        public string Describe() => $"{Model} skipped: {Reason}";
    }
}
