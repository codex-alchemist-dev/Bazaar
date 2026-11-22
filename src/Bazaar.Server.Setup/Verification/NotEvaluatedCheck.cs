namespace Bazaar.Server.Setup;

/// <summary>
/// A placeholder for a check that needs a real service (doc 45 section 1.12: schema state, PostgreSQL, providers, the backup target and
/// so on). It reports <see cref="SetupCheckStatus.NotEvaluated"/> with its reason, so the report lists it honestly. It never reports
/// green, and it never fakes a result. The Host adapter replaces each placeholder with a real check.
/// </summary>
public sealed class NotEvaluatedCheck(string id, string title, string reason = "needs the Host adapter") : ISetupVerificationCheck
{
    private readonly string _reason = reason;

    /// <inheritdoc />
    public string Id { get; } = id;

    /// <inheritdoc />
    public string Title { get; } = title;

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(SetupCheckResult.NotEvaluated(_reason));
    }
}
