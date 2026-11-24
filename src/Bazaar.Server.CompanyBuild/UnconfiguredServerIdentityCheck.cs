using Bazaar.Server.Setup;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The <c>server.identity</c> check when the host has not supplied the company's output folder or the server's identity. It reports
/// not evaluated, so the report says so honestly, and never green.
/// </summary>
public sealed class UnconfiguredServerIdentityCheck : ISetupVerificationCheck
{
    /// <inheritdoc />
    public string Id => ServerIdentityCheck.CheckId;

    /// <inheritdoc />
    public string Title => "Server identity matches the company build";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(SetupCheckResult.NotEvaluated("The host has not supplied the company build output folder and the server identity."));
    }
}
