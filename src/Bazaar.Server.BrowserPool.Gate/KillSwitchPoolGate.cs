using Bazaar.Server.BrowserPool.Abstractions;
using Bazaar.Server.Security.KillSwitch;

namespace Bazaar.Server.BrowserPool.Gate;

/// <summary>
/// The pool's kill switch gate (docs/design/59, "Browser pool isolation"; docs/design/15, "Reads for modules"). An agent is blocked
/// while <see cref="IKillSwitchReader.IsFrozenAsync"/> says it is frozen: an agent stop for it, the global stop, or a kill switch
/// that has not loaded its state yet. Nothing else is checked here: no document says that a tainted session may not open a pool
/// browser, and the reader session design (doc 15) has readers browse in the anonymous profile while always hard-tainted.
/// </summary>
/// <remarks>
/// The reader is asynchronous and the pool's <see cref="IPoolGate"/> is not, so the pool prefers <see cref="IAsyncPoolGate"/>,
/// which this class also implements and which is awaited. Every path fails closed: no reader, a reader that throws, a read that
/// takes longer than <see cref="PoolGateOptions.ReadTimeoutSeconds"/>, and an agent id the reader refuses all answer "blocked".
/// Cancellation by the caller is not a verdict and propagates. The synchronous member never waits: it takes the reader's answer
/// only when the read has already completed, and answers "blocked" otherwise.
/// </remarks>
public sealed class KillSwitchPoolGate : IPoolGate, IAsyncPoolGate
{
    private readonly IKillSwitchReader? _reader;
    private readonly TimeProvider _time;
    private readonly PoolGateOptions _options;

    /// <summary>Creates the gate. A <see langword="null"/> reader blocks every agent.</summary>
    public KillSwitchPoolGate(IKillSwitchReader? reader, TimeProvider time, PoolGateOptions options)
    {
        _reader = reader;
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public bool IsBlocked(string agentId)
    {
        if (_reader is null || string.IsNullOrWhiteSpace(agentId))
            return true;
        try
        {
            ValueTask<bool> read = _reader.IsFrozenAsync(agentId, CancellationToken.None);
            if (read.IsCompletedSuccessfully)
                return read.Result;
            // Not answered yet: never wait for it. Observe the task so a late failure is not left unobserved.
            _ = read.AsTask().ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception)
        {
            // Fall through: blocked.
        }

        return true;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsBlockedAsync(string agentId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_reader is null || string.IsNullOrWhiteSpace(agentId))
            return true;
        try
        {
            return await _reader.IsFrozenAsync(agentId, ct).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(_options.ReadTimeoutSeconds), _time, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A reader failure or a timeout: the state is unknown, so the agent is blocked.
            return true;
        }
    }
}
