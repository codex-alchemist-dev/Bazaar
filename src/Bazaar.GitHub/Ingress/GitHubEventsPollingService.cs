using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.GitHub.Ingress;

/// <summary>
/// Hosted loop around <see cref="GitHubEventsPoller"/> (doc 21, Ingress). Disabled, with one log line, until an installation id and a
/// repository list are configured and a real <see cref="IGitHubEventSink"/> is registered. Each iteration sleeps at least
/// <see cref="GitHubIngressOptions.MinLoopDelay"/>; a failure inside an iteration is logged and the loop continues.
/// </summary>
public sealed class GitHubEventsPollingService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> _logDisabled = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(20, "GitHubPollerDisabled"), "GitHub events poller is disabled: {Reason}.");

    private static readonly Action<ILogger, Exception?> _logLoopError = LoggerMessage.Define(
        LogLevel.Error, new EventId(21, "GitHubPollerLoopError"), "GitHub events poller iteration failed unexpectedly; continuing.");

    private readonly GitHubEventsPoller _poller;
    private readonly GitHubIngressOptions _options;
    private readonly bool _sinkWired;
    private readonly ILogger<GitHubEventsPollingService> _logger;
    private readonly TimeProvider _time;

    public GitHubEventsPollingService(
        GitHubEventsPoller poller,
        GitHubIngressOptions options,
        IGitHubEventSink sink,
        ILogger<GitHubEventsPollingService> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(poller);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        _poller = poller;
        _options = options;
        _sinkWired = sink is not UnwiredGitHubEventSink;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Why the loop will not run, or null when it will. Exposed for tests and the status page.</summary>
    public string? DisabledReason => _options.DisabledReason()
        ?? (_sinkWired ? null : "no event sink is registered (the GC and inbox wiring is a seam)");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (DisabledReason is { } reason)
        {
            _logDisabled(_logger, reason, null);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = _options.MinLoopDelay;
            try
            {
                wait = await _poller.PollDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // PollDueAsync handles per-repo failures itself; this only guards against a bug so the host never crashes.
                _logLoopError(_logger, ex);
                wait = _options.FailureBackoffBase;
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
