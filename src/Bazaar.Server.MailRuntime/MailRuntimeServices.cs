using Bazaar.Server.MailInbound;
using Bazaar.Server.MailOutbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.MailRuntime;

/// <summary>Which required ports are still fail-closed defaults. A loop with any of them does no work.</summary>
public static class MailRuntimeReadiness
{
    private static readonly Type[] _inboundPorts =
        [typeof(IInboundMailboxDirectory), typeof(IMailBodySealer), typeof(IMailReaderSessions), typeof(IReaderSessionTaint), typeof(IMailTriageReader)];

    private static readonly Type[] _outboundPorts = [typeof(IMailApprovalGateway), typeof(IMailBodyProtector)];

    /// <summary>Names of the fail-closed inbound ports.</summary>
    public static IReadOnlyList<string> MissingInbound(IServiceProvider services) => Missing(services, _inboundPorts);

    /// <summary>Names of the fail-closed outbound ports.</summary>
    public static IReadOnlyList<string> MissingOutbound(IServiceProvider services) => Missing(services, _outboundPorts);

    private static List<string> Missing(IServiceProvider services, Type[] ports)
    {
        var missing = new List<string>();
        foreach (var port in ports)
        {
            if (services.GetService(port) is IFailClosedMailPort closed)
            {
                missing.Add(closed.PortName);
            }
        }

        return missing;
    }
}

/// <summary>
/// The shared loop. Each iteration runs one pass, then waits the interval read at that moment. A pass that throws is logged by exception type and the
/// loop goes on; only cancellation of the host ends it. Logs carry counts and error kinds, never mail content.
/// </summary>
internal static class MailLoop
{
    public static async Task RunAsync(
        Func<MailRuntimeOptions, TimeSpan> interval,
        IMailRuntimeOptionsSource options,
        TimeProvider time,
        Func<MailRuntimeOptions, CancellationToken, Task> pass,
        Action<Exception> onFailure,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var current = options.Current;
            try
            {
                if (current.Enabled)
                {
                    await pass(current, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                onFailure(ex);
            }

            try
            {
                await Task.Delay(interval(options.Current), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}

/// <summary>Calls <see cref="IInboundMailWorker.RunPassAsync"/> on the configured interval (<c>mail.runtime.inbound_interval_seconds</c>).</summary>
public sealed partial class InboundMailHostedService(
    IServiceProvider services,
    IMailRuntimeOptionsSource options,
    ILogger<InboundMailHostedService> logger,
    TimeProvider? time = null) : BackgroundService
{
    private bool _loggedBlocked;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        MailLoop.RunAsync(o => o.EffectiveInboundInterval, options, time ?? TimeProvider.System, PassAsync, ex => PassFailed(logger, ex.GetType().Name), stoppingToken);

    private async Task PassAsync(MailRuntimeOptions current, CancellationToken ct)
    {
        var missing = MailRuntimeReadiness.MissingInbound(services);
        if (missing.Count > 0)
        {
            // Pulling now would mark mail seen with no safe place to store it, so nothing is pulled. Said once.
            if (!_loggedBlocked)
            {
                _loggedBlocked = true;
                Blocked(logger, string.Join(',', missing));
            }

            return;
        }

        _loggedBlocked = false;
        var result = await services.GetRequiredService<IInboundMailWorker>().RunPassAsync(ct).ConfigureAwait(false);
        var pulled = 0;
        var delivered = 0;
        var quarantined = 0;
        foreach (var box in result.Mailboxes)
        {
            pulled += box.Pulled;
            delivered += box.Delivered;
            quarantined += box.Quarantined;
        }

        Completed(logger, result.Mailboxes.Count, pulled, delivered, quarantined, result.Failures);
        if (result.Failures > 0)
        {
            var kinds = string.Join(',', result.Mailboxes.SelectMany(m => m.Failures).Select(f => f.Code).Distinct(StringComparer.Ordinal).Take(10));
            FailureKinds(logger, kinds);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound mail pass failed: {ErrorKind}.")]
    private static partial void PassFailed(ILogger logger, string errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound mail is paused: these ports are not wired yet: {Ports}.")]
    private static partial void Blocked(ILogger logger, string ports);

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbound mail pass: {Mailboxes} mailboxes, {Pulled} pulled, {Delivered} delivered, {Quarantined} quarantined, {Failures} failures.")]
    private static partial void Completed(ILogger logger, int mailboxes, int pulled, int delivered, int quarantined, int failures);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbound mail failure kinds: {Kinds}.")]
    private static partial void FailureKinds(ILogger logger, string kinds);
}

/// <summary>
/// Refreshes the back end catalog and calls <see cref="IOutboundMailPipeline.ResumeDueAsync"/> on the configured interval
/// (<c>mail.runtime.outbound_interval_seconds</c>, batch <c>mail.runtime.outbound_resume_batch</c>).
/// </summary>
public sealed partial class OutboundMailResumeHostedService(
    IServiceProvider services,
    IMailRuntimeOptionsSource options,
    ILogger<OutboundMailResumeHostedService> logger,
    TimeProvider? time = null) : BackgroundService
{
    private bool _loggedBlocked;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        MailLoop.RunAsync(o => o.EffectiveOutboundInterval, options, time ?? TimeProvider.System, PassAsync, ex => PassFailed(logger, ex.GetType().Name), stoppingToken);

    private async Task PassAsync(MailRuntimeOptions current, CancellationToken ct)
    {
        var missing = MailRuntimeReadiness.MissingOutbound(services);
        if (missing.Count > 0)
        {
            if (!_loggedBlocked)
            {
                _loggedBlocked = true;
                Blocked(logger, string.Join(',', missing));
            }

            return;
        }

        _loggedBlocked = false;
        if (services.GetService<IMailBackendCatalog>() is { } catalog)
        {
            // A failed refresh keeps the previous snapshot; the resume below still runs with it.
            try
            {
                var refreshed = await catalog.RefreshAsync(ct).ConfigureAwait(false);
                if (refreshed.ErrorKinds.Count > 0)
                {
                    RefreshErrors(logger, refreshed.Built, refreshed.Skipped, string.Join(',', refreshed.ErrorKinds.Distinct(StringComparer.Ordinal)));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RefreshFailed(logger, ex.GetType().Name);
            }
        }

        var advanced = await services.GetRequiredService<IOutboundMailPipeline>().ResumeDueAsync(current.EffectiveOutboundResumeBatch, ct).ConfigureAwait(false);
        if (advanced > 0)
        {
            Completed(logger, advanced);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound mail resume failed: {ErrorKind}.")]
    private static partial void PassFailed(ILogger logger, string errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbound mail resume is paused: these ports are not wired yet: {Ports}.")]
    private static partial void Blocked(ILogger logger, string ports);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail back end refresh failed: {ErrorKind}.")]
    private static partial void RefreshFailed(ILogger logger, string errorKind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mail back end refresh: {Built} built, {Skipped} skipped, error kinds: {Kinds}.")]
    private static partial void RefreshErrors(ILogger logger, int built, int skipped, string kinds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Outbound mail resume advanced {Advanced} sends.")]
    private static partial void Completed(ILogger logger, int advanced);
}
