using Bazaar.GitHub.Ingress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.GitHub.Processing;

/// <summary>
/// Hosted loop around <see cref="GitHubWebhookProcessor"/> (doc 21, Ingress). Idle, with one log line, unless a real
/// <see cref="IGitHubEventSink"/> and a database queue exist. A failure inside a pass is logged and the loop continues after
/// <see cref="GitHubWebhookProcessorOptions.IdleDelay"/>; a busy pass loops again at once.
/// </summary>
public sealed class GitHubWebhookProcessingService : BackgroundService
{
    private static readonly Action<ILogger, string, Exception?> _logDisabled = LoggerMessage.Define<string>(
        LogLevel.Information, new EventId(32, "GitHubWebhookProcessorDisabled"), "GitHub webhook processor is disabled: {Reason}.");

    private static readonly Action<ILogger, Exception?> _logLoopError = LoggerMessage.Define(
        LogLevel.Error, new EventId(33, "GitHubWebhookProcessorLoopError"), "GitHub webhook processor pass failed unexpectedly; continuing.");

    private readonly GitHubWebhookProcessor? _processor;
    private readonly GitHubWebhookProcessorOptions _options;
    private readonly ILogger<GitHubWebhookProcessingService> _logger;
    private readonly TimeProvider _time;

    /// <param name="processor">Null when no database queue is registered.</param>
    public GitHubWebhookProcessingService(
        GitHubWebhookProcessor? processor,
        GitHubWebhookProcessorOptions options,
        IGitHubEventSink sink,
        ILogger<GitHubWebhookProcessingService> logger,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _processor = sink is UnwiredGitHubEventSink ? null : processor;
        DisabledReason = sink is UnwiredGitHubEventSink ? "no event sink is registered (the GC and inbox wiring is a seam)"
            : processor is null ? "no database is registered"
            : null;
    }

    /// <summary>Why the loop will not run, or null when it will.</summary>
    public string? DisabledReason { get; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_processor is null)
        {
            _logDisabled(_logger, DisabledReason ?? "unknown", null);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = _options.IdleDelay;
            try
            {
                if (await _processor.ProcessBatchAsync(stoppingToken).ConfigureAwait(false) > 0)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logLoopError(_logger, ex);
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }
}
