using Bazaar.Server.VmDisplay.Abstractions;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.VmDisplay.GuestChannel;

/// <summary>
/// <see cref="IDisplayGuestExec"/> over the open guest channel of exactly one VM (docs/design/59 section 5 rule 4, docs/design/16 "Channel
/// directory"; PROPOSED). Every call looks the channel up by the target's VM id and uses no other. A missing directory, a missing or ended
/// channel, a slow lookup, a refused or timed-out exec, truncated output and a failed file fetch all fail the call (closed) with
/// <see cref="DisplayFailure.SourceFailed"/>. The argv goes to the guest as an array and no shell is involved. Exec output is discarded.
/// </summary>
public sealed class GuestChannelDisplayExec : IDisplayGuestExec
{
    // GuestSource writes captures here; a file fetch outside it is refused whatever the exec directory is set to.
    private const string _fetchPrefix = "/run/bazaar/display/";

    private static readonly IReadOnlyList<string> _noEnvironment = [];

    private readonly IGuestChannelDirectory? _directory;
    private readonly TimeProvider _time;
    private readonly GuestChannelDisplayOptions _options;

    /// <summary>Creates the adapter. A null <paramref name="directory"/> means no channel exists, so every call fails.</summary>
    public GuestChannelDisplayExec(GuestChannelDisplayOptions options, TimeProvider time, IGuestChannelDirectory? directory = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
        _directory = directory;
    }

    /// <inheritdoc />
    public bool Supports(DisplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Os == DisplayGuestOs.Linux
            && string.IsNullOrEmpty(target.BrowserSessionId)
            && target.VmId != Guid.Empty;
    }

    /// <inheritdoc />
    public async Task<int> ExecAsync(Guid vmId, IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0 || string.IsNullOrEmpty(argv[0]) || argv.Any(a => a is null || a.Contains('\0', StringComparison.Ordinal)))
            throw new DisplayException(DisplayFailure.InvalidInput, "The guest command is empty or malformed.");
        if (timeout <= TimeSpan.Zero)
            throw new DisplayException(DisplayFailure.InvalidInput, "The guest command timeout must be positive.");

        TimeSpan cap = TimeSpan.FromSeconds(_options.MaxCallTimeoutSeconds);
        var request = new ExecRequest([.. argv], _options.ExecWorkingDirectory, _noEnvironment, timeout > cap ? cap : timeout);
        IGuestChannel channel = await GetChannelAsync(vmId, ct).ConfigureAwait(false);

        ExecResult result;
        try
        {
            result = await channel.ExecAsync(request, _ => Task.CompletedTask, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!IsCallerCancel(e, ct))
        {
            throw Failed(e, "The guest command could not be run");
        }

        if (result.Cancelled)
        {
            ct.ThrowIfCancellationRequested();
            throw new DisplayException(DisplayFailure.SourceFailed, "The guest command was cancelled.");
        }
        if (result.TimedOut)
            throw new DisplayException(DisplayFailure.SourceFailed, "The guest command timed out.");
        if (result.OutputTruncated)
            throw new DisplayException(DisplayFailure.SourceFailed, "The guest command produced more output than the channel keeps.");
        return result.ExitCode;
    }

    /// <inheritdoc />
    public async Task GetFileAsync(Guid vmId, string guestPath, Stream sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!IsFetchablePath(guestPath))
            throw new DisplayException(DisplayFailure.InvalidInput, "The guest file path is not one the display module may fetch.");

        IGuestChannel channel = await GetChannelAsync(vmId, ct).ConfigureAwait(false);
        try
        {
            await channel.GetFileAsync(guestPath, sink, ct).ConfigureAwait(false);
        }
        catch (DisplayException)
        {
            // The sink's own refusal (a frame over the size cap) keeps its reason.
            throw;
        }
        catch (Exception e) when (!IsCallerCancel(e, ct))
        {
            throw Failed(e, "The guest file could not be fetched");
        }
    }

    private async Task<IGuestChannel> GetChannelAsync(Guid vmId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_directory is null)
            throw new DisplayException(DisplayFailure.SourceFailed, "No guest channel directory is available.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.LookupTimeoutSeconds), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        IGuestChannel? channel;
        try
        {
            channel = await _directory.TryGetByVmAsync(vmId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The guest channel lookup timed out.", e);
        }
        catch (Exception e)
        {
            throw Failed(e, "The guest channel lookup failed");
        }
        return channel ?? throw new DisplayException(DisplayFailure.SourceFailed, "The VM has no live guest channel.");
    }

    private static bool IsCallerCancel(Exception e, CancellationToken ct) => e is OperationCanceledException && ct.IsCancellationRequested;

    private static DisplayException Failed(Exception e, string what) => e switch
    {
        GuestExecRefusedException refused => new DisplayException(DisplayFailure.SourceFailed, $"{what}: the guest refused it ({refused.Code}).", e),
        _ => new DisplayException(DisplayFailure.SourceFailed, $"{what}: {e.GetType().Name}.", e),
    };

    private static bool IsFetchablePath(string? path) =>
        path is not null
        && path.StartsWith(_fetchPrefix, StringComparison.Ordinal)
        && !path.Contains('\0', StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal)
        && path[_fetchPrefix.Length..].Split('/') is { } parts
        && parts.All(s => s.Length > 0 && s is not "." and not "..");
}
