using System.Collections.Concurrent;
using System.Text.Json;
using Bazaar.Client.Studio.Abstractions;
using Bazaar.Contracts.Engines;

namespace Bazaar.Client.Studio;

/// <summary>Settings for <see cref="StudioRelayWireClient"/>.</summary>
public sealed class StudioRelayWireOptions
{
    /// <summary>How many relay calls run at once. The loop stops reading events while all slots are busy.</summary>
    public int MaxConcurrent { get; init; } = 4;

    /// <summary>Called with failures the loop swallowed (a failed send, a broken event source). Must not throw; if it does the exception is dropped.</summary>
    public Action<Exception>? OnError { get; init; }
}

/// <summary>
/// Client half of the relay-request channel [PROPOSED] (doc 02, doc 20, doc 28): reads <see cref="EngineRelayRequestEvent"/>s, runs each
/// through <see cref="IStudioRelay"/> and answers with an <see cref="EngineRelayResultRequest"/>. Runs events concurrently up to a
/// bound, drops events past their deadline (the server stopped waiting), answers every accepted event (failures become an
/// <c>ok=false</c> error payload), and never throws out of <see cref="RunAsync"/>.
/// </summary>
public sealed class StudioRelayWireClient
{
    private readonly IRelayEventSource _events;
    private readonly IRelayResultSender _sender;
    private readonly IStudioRelay _relay;
    private readonly StudioRelayWireOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    public StudioRelayWireClient(
        IRelayEventSource events,
        IRelayResultSender sender,
        IStudioRelay relay,
        StudioRelayWireOptions? options = null,
        TimeProvider? time = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _relay = relay ?? throw new ArgumentNullException(nameof(relay));
        _options = options ?? new StudioRelayWireOptions();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Processes events until the source ends or <paramref name="cancellationToken"/> is cancelled, then waits for calls in flight.
    /// Returns normally in both cases; a broken source ends the loop and is reported through <see cref="StudioRelayWireOptions.OnError"/>.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var slots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrent));
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            await foreach (var evt in _events.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (evt is null || evt.Deadline <= _time.GetUtcNow() || !_inFlight.TryAdd(evt.RelayId, 0))
                {
                    continue;
                }

                await slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                var task = Task.Run(() => HandleAsync(evt, slots, cancellationToken), CancellationToken.None);
                running[task] = 0;
                _ = task.ContinueWith(t => running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Report(ex);
        }

        await Task.WhenAll(running.Keys).ConfigureAwait(false);
    }

    private async Task HandleAsync(EngineRelayRequestEvent evt, SemaphoreSlim slots, CancellationToken stopping)
    {
        try
        {
            var result = await RunCallAsync(evt, stopping).ConfigureAwait(false);
            if (result is null)
            {
                return;
            }

            try
            {
                await _sender.SendAsync(new EngineRelayResultRequest(evt.RelayId, result), stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Report(ex);
            }
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            _inFlight.TryRemove(evt.RelayId, out _);
            slots.Release();
        }
    }

    /// <summary>Runs the call. Null means the loop is stopping and no answer is owed.</summary>
    private async Task<EngineRelayCallResult?> RunCallAsync(EngineRelayRequestEvent evt, CancellationToken stopping)
    {
        var grantId = evt.Call?.GrantId ?? "";
        if (evt.Call is null)
        {
            return Fail(grantId, StudioRelayErrorCode.ToolNotAllowed, "the relay request carried no call");
        }

        var remaining = evt.Deadline - _time.GetUtcNow();
        using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, deadline.Token);
        try
        {
            var request = new StudioRelayRequest(evt.Call.GrantId, evt.Call.StudioId, evt.Call.Tool, evt.Call.Args);
            var result = await _relay.RelayAsync(request, linked.Token).ConfigureAwait(false);
            return new EngineRelayCallResult(result.GrantId, result.Ok, result.Payload.ValueKind == JsonValueKind.Undefined ? Empty() : result.Payload);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return Fail(grantId, StudioRelayErrorCode.Timeout, "the relay deadline passed before Studio answered");
        }
        catch (Exception)
        {
            // The message could carry Studio content; the answer keeps a fixed reason.
            return Fail(grantId, StudioRelayErrorCode.Unavailable, "the relay failed unexpectedly");
        }
    }

    private static EngineRelayCallResult Fail(string grantId, StudioRelayErrorCode code, string reason)
        => new(grantId, false, new StudioRelayError(code, reason).ToPayload());

    private static JsonElement Empty() => JsonElement.Parse("{}");

    private void Report(Exception ex)
    {
        try
        {
            _options.OnError?.Invoke(ex);
        }
        catch (Exception)
        {
        }
    }
}
