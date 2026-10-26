using System.Runtime.CompilerServices;
using System.Text.Json;
using Bazaar.Contracts.Gateway;
using Bazaar.Server.Gateway.Adapters;
using Bazaar.Server.Gateway.Data;
using Bazaar.Server.Gateway.Health;
using Bazaar.Server.Gateway.Policy;
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
    private readonly Lock _gate = new();
    private readonly Dictionary<string, bool> _lastPublishedAvailable = [];

    /// <param name="onInternalError">Told about a usage write or event publish that threw; the caller's stream is not affected.</param>
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
        Action<Exception>? onInternalError = null)
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
    }

    public async IAsyncEnumerable<ModelEvent> CallAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolution = await _resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
        if (resolution is RouteResolution.Denied denied)
        {
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
            var (ready, skip) = await PrepareAsync(candidate, cancellationToken).ConfigureAwait(false);
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
                    var e = next ?? new ModelEvent.Failure("stream_dropped", "The stream ended without a final event.");
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

            lastFailure = retryable;
        }

        if (lastFailure is not null)
        {
            yield return lastFailure;
            yield break;
        }

        yield return NoCapacity(request.AgentId, skips);
    }

    private ModelEvent.Failure NoCapacity(string agentId, List<Skip> skips)
    {
        var now = _time.GetUtcNow();
        DateTimeOffset? earliest = null;
        foreach (var skip in skips)
        {
            if (skip.Until is { } until && (earliest is null || until < earliest))
            {
                earliest = until;
            }
        }

        // A capacity change is reported only when every candidate was blocked by quota or health, not by configuration.
        if (skips.Count > 0 && skips.All(s => s.Kind != SkipKind.Config) && SetPublished(agentId, available: false))
        {
            Publish(new AgentCapacityChanged(agentId, false, earliest, skips[^1].State ?? QuotaState.Down));
        }

        int? retryAfter = earliest is { } at ? Math.Max(1, (int)Math.Ceiling((at - now).TotalSeconds)) : null;
        var detail = skips.Count == 0
            ? "The route has no usable candidate."
            : string.Join("; ", skips.Select(s => s.Describe()));
        return new ModelEvent.Failure("no_capacity", $"No candidate can take the call: {detail}.", retryAfter);
    }

    private async ValueTask<(Ready? Ready, Skip? Skip)> PrepareAsync(RouteCandidate candidate, CancellationToken ct)
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
        if (_quota.BlockedState(key, candidate.Family) is { } blocked)
        {
            return (null, Skip.Blocked(SkipKind.Quota, model, blocked, _quota.BlockedUntil(key, candidate.Family)));
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
        }

        if (!_health.TryAcquire(key))
        {
            return (null, Skip.Blocked(SkipKind.Health, model, QuotaState.Down, null));
        }

        return (new Ready(adapter, new ProviderEndpoint(provider.BaseUrl, model.ModelId, apiKey, headers), key, identity), null);
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

    private void Report(ProviderKey key, string modelClass, ModelEvent.Failure failure)
    {
        try
        {
            _quota.Report(key, modelClass, failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _onInternalError?.Invoke(ex);
        }
    }

    private async ValueTask RecordSafeAsync(UsageAccumulator acc)
    {
        try
        {
            // Not tied to the caller's token, so a cancelled call still records as Aborted.
            await _recorder.RecordAsync(acc, _prices, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _onInternalError?.Invoke(ex);
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
            _onInternalError?.Invoke(ex);
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
            _onInternalError?.Invoke(ex);
        }
    }

    private sealed record Ready(IModelAdapter Adapter, ProviderEndpoint Endpoint, ProviderKey Key, GatewayIdentity Identity);

    private enum SkipKind
    {
        Config,
        Quota,
        Health,
    }

    private sealed record Skip(SkipKind Kind, ModelRef Model, string Reason, QuotaState? State, DateTimeOffset? Until)
    {
        public static Skip Config(ModelRef model, string reason) => new(SkipKind.Config, model, reason, null, null);

        public static Skip Blocked(SkipKind kind, ModelRef model, QuotaState state, DateTimeOffset? until) =>
            new(kind, model, kind == SkipKind.Health ? "its breaker is open" : $"quota state {state}", state, until);

        public string Describe() => $"{Model} skipped: {Reason}";
    }
}
