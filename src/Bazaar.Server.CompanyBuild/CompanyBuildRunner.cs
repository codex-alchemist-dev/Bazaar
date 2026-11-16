using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.CompanyIdentity;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// Runs one company build with its identity baked in (docs/design/37 section 4a, doc 45 section 2.1). The identity source is
/// written before each attempt. A failed attempt is retried up to <c>maxAttempts</c> in total, then the run stops and reports.
/// Every failure and retry is reported, including retries that later succeed.
/// </summary>
public static class CompanyBuildRunner
{
    /// <summary>The default number of attempts in total (the first try plus two retries).</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>The highest attempt count an operator may set.</summary>
    public const int MaxAllowedAttempts = 10;

    /// <param name="plan">The composed plan from <see cref="CompanyBuildComposer"/>.</param>
    /// <param name="identity">The company server's identity. Required: a build without one is never produced.</param>
    /// <param name="sourceDirectory">The client build's source folder, where <c>CompanyBuildIdentity.cs</c> is written.</param>
    /// <param name="buildStep">The build itself. It receives the 1-based attempt number. Throwing marks the attempt failed.</param>
    /// <param name="maxAttempts">Attempts in total, between 1 and <see cref="MaxAllowedAttempts"/>.</param>
    /// <param name="cancellationToken">Cancels the run. Cancellation is never counted as a failed attempt.</param>
    public static async Task<CompanyBuildReport> RunAsync(
        CompanyBuildPlan plan,
        CompanyServerIdentity identity,
        string sourceDirectory,
        Func<int, CancellationToken, Task> buildStep,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentNullException.ThrowIfNull(buildStep);
        if (maxAttempts is < 1 or > MaxAllowedAttempts)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), $"Attempts must be between 1 and {MaxAllowedAttempts}.");
        }

        var identityPath = CompanyBuildIdentityGenerator.WriteSource(identity, sourceDirectory);
        var failures = new List<string>();

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await buildStep(attempt, cancellationToken).ConfigureAwait(false);
                return new CompanyBuildReport(true, attempt, attempt - 1, failures, identityPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"Attempt {attempt} of {maxAttempts} for '{plan.CompanyId}' failed: {ex.Message}");
            }
        }

        return new CompanyBuildReport(false, maxAttempts, maxAttempts - 1, failures, identityPath);
    }
}
