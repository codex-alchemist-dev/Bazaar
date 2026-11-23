using Bazaar.Server.Setup.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.Setup.Host;

/// <summary>
/// The setup plan the operator named at start (doc 45 section 1.1, the config-path door). Read from <c>--setup-plan &lt;file&gt;</c>
/// or the environment variable <c>BAZAAR_SETUP_PLAN</c>; the flag wins. <c>--setup-only</c> (or <c>BAZAAR_SETUP_ONLY</c>) makes the
/// process exit after the apply instead of serving.
/// </summary>
/// <param name="PlanPath">The plan file, or null when none was named (the feature is then off).</param>
/// <param name="SetupOnly">True when the process exits after the apply, with the plan's exit code.</param>
public sealed record SetupPlanStartOptions(string? PlanPath, bool SetupOnly)
{
    /// <summary>Configuration key for <c>--setup-plan</c>.</summary>
    public const string PlanKey = "setup-plan";

    /// <summary>Configuration key for <c>--setup-only</c>.</summary>
    public const string SetupOnlyKey = "setup-only";

    /// <summary>The environment variable form of the plan path.</summary>
    public const string PlanEnvironmentName = "BAZAAR_SETUP_PLAN";

    /// <summary>The environment variable form of the setup-only switch.</summary>
    public const string SetupOnlyEnvironmentName = "BAZAAR_SETUP_ONLY";

    /// <summary>
    /// Reads the options from host configuration, which holds the command line and the (unprefixed) environment. A blank path is
    /// unset. A bare <c>--setup-only</c> reads as true.
    /// </summary>
    public static SetupPlanStartOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = First(configuration[PlanKey], configuration[PlanEnvironmentName]);
        var only = First(configuration[SetupOnlyKey], configuration[SetupOnlyEnvironmentName]);
        return new SetupPlanStartOptions(path, only is not null && (bool.TryParse(only, out var flag) ? flag : only == "1"));
    }

    private static string? First(string? preferred, string? fallback) =>
        !string.IsNullOrWhiteSpace(preferred) ? preferred : !string.IsNullOrWhiteSpace(fallback) ? fallback : null;
}

/// <summary>What the start-time apply did. <c>Program.cs</c> reads <see cref="ExitCode"/> after the host stops.</summary>
public sealed class SetupPlanStartOutcome
{
    /// <summary>The process exit code asked for by <c>--setup-only</c>: the plan's exit code, 0 when it applied. Always 0 when serving.</summary>
    public int ExitCode { get; internal set; }

    /// <summary>True when <c>--setup-only</c> was given, so the process stops after the apply and must not announce a bootstrap code.</summary>
    public bool StopRequested { get; internal set; }

    /// <summary>The hash of the plan that was applied or attempted. Null when no plan was named or it did not load.</summary>
    public string? PlanHash { get; internal set; }
}

/// <summary>
/// Applies the named setup plan in-process before the server serves (doc 45, the config-path door). The operator started the process,
/// so the file is local operator input and needs no bootstrap code: the single-use code and the HTTP routes are untouched. The plan
/// goes through <see cref="SetupPlanLoader"/> and <see cref="SetupApplier"/>, so validation, floors, holds, the instance lock and the
/// <c>setup.plan.applied</c> audit row are the same as over HTTP; the entry path recorded is <see cref="SetupEntryPath.File"/>.
/// </summary>
/// <remarks>
/// Idempotent across restarts: a plan hash already stored as applied (exit 0) is not applied again. A stored failure is retried.
/// A held apply is not stored, as over HTTP. When the apply does not succeed the server still serves unless <c>--setup-only</c>
/// was given, in which case the process exits with the plan's exit code (1 invalid, 2 held, 3 failed and rolled back; an unreadable
/// file is 1). Registered before <see cref="SetupBootstrapCodeIssuer"/> so a plan that creates the owner retires the code.
/// Errors are logged by kind only: no path text from the plan and no exception message reaches the log.
/// </remarks>
public sealed partial class SetupPlanOnStartService(
    SetupPlanStartOptions options,
    SetupApplier applier,
    IApplyResultStore results,
    ISetupSettingCatalog catalog,
    SetupPlanStartOutcome outcome,
    IHostApplicationLifetime lifetime,
    ILogger<SetupPlanOnStartService> logger) : IHostedService
{
    /// <summary>The operator identity written to the apply row for a start-time apply.</summary>
    public const string OperatorIdentity = "host:setup-plan";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.PlanPath is null)
        {
            return;
        }

        outcome.StopRequested = options.SetupOnly;
        var code = await RunAsync(cancellationToken).ConfigureAwait(false);
        if (options.SetupOnly)
        {
            outcome.ExitCode = code;
            lifetime.StopApplication();
        }
        else if (code != 0)
        {
            LogContinuing(logger, code);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(options.PlanPath!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogUnreadable(logger, exception.GetType().Name);
            return (int)SetupExitCode.InvalidInput;
        }

        var loaded = SetupPlanLoader.Load(text, catalog);
        if (loaded.Plan is null || loaded.PlanHash is null)
        {
            LogInvalid(logger, (int)loaded.ExitCode, string.Join("; ", loaded.Issues.Select(Describe)));
            return (int)loaded.ExitCode;
        }

        outcome.PlanHash = loaded.PlanHash;
        var stored = await results.TryGetAsync(loaded.PlanHash, cancellationToken).ConfigureAwait(false);
        if (stored is { ExitCode: SetupExitCode.Ok })
        {
            LogAlreadyApplied(logger, loaded.PlanHash);
            return 0;
        }

        SetupApplyResult result;
        try
        {
            // An apply runs to completion once it starts, as over HTTP: shutdown does not cancel it halfway through a step.
            result = await applier.ApplyAsync(
                new SetupApplyRequest(loaded.Plan, loaded.PlanHash, SetupEntryPath.File, OperatorIdentity),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogApplyThrew(logger, exception.GetType().Name);
            return (int)SetupExitCode.ApplyFailedRolledBack;
        }

        if (result.ExitCode != SetupExitCode.Held)
        {
            await results.SaveAsync(loaded.PlanHash, result, CancellationToken.None).ConfigureAwait(false);
        }

        if (result.ExitCode == SetupExitCode.Ok)
        {
            LogApplied(logger, result.PlanHash, result.AppliedStepIds.Count);
        }
        else
        {
            LogNotApplied(logger, (int)result.ExitCode, string.Join("; ", result.Issues.Select(Describe)));
        }

        return (int)result.ExitCode;
    }

    // Issues name a key path and a rule, never a value (doc 45 section 1.7), so they are safe to log.
    private static string Describe(SetupValidationIssue issue) => $"{issue.Path}: {issue.Message}";

    [LoggerMessage(Level = LogLevel.Error, Message = "Setup plan file cannot be read ({ExceptionType}). Check the path and that this user can read it.")]
    private static partial void LogUnreadable(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setup plan refused (exit {ExitCode}): {Issues}")]
    private static partial void LogInvalid(ILogger logger, int exitCode, string issues);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup plan {PlanHash} was already applied; not applying it again.")]
    private static partial void LogAlreadyApplied(ILogger logger, string planHash);

    [LoggerMessage(Level = LogLevel.Information, Message = "Setup plan {PlanHash} applied ({Steps} steps).")]
    private static partial void LogApplied(ILogger logger, string planHash, int steps);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setup plan not applied (exit {ExitCode}): {Issues}")]
    private static partial void LogNotApplied(ILogger logger, int exitCode, string issues);

    [LoggerMessage(Level = LogLevel.Error, Message = "Setup plan apply failed ({ExceptionType}).")]
    private static partial void LogApplyThrew(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Setup plan was not applied (exit {ExitCode}); serving anyway because --setup-only was not given.")]
    private static partial void LogContinuing(ILogger logger, int exitCode);
}
