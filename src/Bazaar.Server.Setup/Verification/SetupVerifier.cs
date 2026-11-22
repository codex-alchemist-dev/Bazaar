namespace Bazaar.Server.Setup;

/// <summary>
/// Runs the registered checks and builds the report (doc 45 section 1.12). The verifier has one dependency, the list of checks, so
/// it cannot write: no audit log, no step, no store. Each check runs on its own. A check that throws, or returns no result, becomes
/// a red row for that check, and the message names only the exception type, because exception text can carry a value.
/// </summary>
public sealed class SetupVerifier : ISetupVerifier
{
    private readonly List<ISetupVerificationCheck> _checks;

    /// <param name="checks">The checks to run, in order. There must be at least one, and every id must be distinct.</param>
    public SetupVerifier(IEnumerable<ISetupVerificationCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var list = checks.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("A verifier needs at least one check; an empty set would read as verified.", nameof(checks));
        }

        var duplicate = list.GroupBy(check => check.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"Two verification checks share the id '{duplicate.Key}'.", nameof(checks));
        }

        _checks = list;
    }

    /// <inheritdoc />
    public async Task<SetupVerificationReport> VerifyAsync(SetupVerificationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var rows = new List<SetupCheckReport>(_checks.Count);
        foreach (var check in _checks)
        {
            rows.Add(await RunOneAsync(check, context, cancellationToken));
        }

        var statuses = rows.Select(row => row.Status).ToList();
        var overall = SetupVerificationRules.Overall(statuses);
        var score = SetupVerificationRules.Score(statuses);
        var amberItems = rows.Where(row => row.Status is SetupCheckStatus.Amber or SetupCheckStatus.NotEvaluated).ToList();
        return new SetupVerificationReport(overall, score, rows, amberItems);
    }

    private static async Task<SetupCheckReport> RunOneAsync(
        ISetupVerificationCheck check,
        SetupVerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await check.RunAsync(context, cancellationToken);
            if (result is null)
            {
                return Failed(check, "returned no result");
            }

            return new SetupCheckReport(check.Id, check.Title, result.Status, result.Message, result.Remediation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop, so the stop is not a check failure.
            throw;
        }
        catch (Exception exception)
        {
            return Failed(check, $"threw {exception.GetType().Name}; no detail is shown");
        }
    }

    private static SetupCheckReport Failed(ISetupVerificationCheck check, string reason) =>
        new(check.Id, check.Title, SetupCheckStatus.Red, $"The check did not complete: it {reason}.", null);
}
