using Bazaar.Server.CompanyBuild.Abstractions;
using Bazaar.Server.Setup;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// The real <c>server.identity</c> setup check (docs/design/45 section 1.12): the server's identity and key hash match the identity
/// baked into the company build. It replaces the not-evaluated placeholder of the same id when the Host adapter registers it.
/// </summary>
public sealed class ServerIdentityCheck(CompanyBuildOutputLayout layout, IServerIdentitySource server) : ISetupVerificationCheck
{
    /// <summary>The id of the placeholder this check replaces.</summary>
    public const string CheckId = "server.identity";

    private const string _rebuildAdvice = "Rebuild the company clients from the server so their baked identity matches it.";

    private readonly CompanyBuildOutputLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));
    private readonly IServerIdentitySource _server = server ?? throw new ArgumentNullException(nameof(server));

    /// <inheritdoc />
    public string Id => CheckId;

    /// <inheritdoc />
    public string Title => "Server identity matches the company build";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var issues = CompanyBuildIdentityCheck.Check(CompanyBuildIdentityCheck.ReadLastBuild(_layout), _server.Address, _server.KeyHashPin);
        if (issues.Count == 0)
        {
            return Task.FromResult(SetupCheckResult.Green("The last company build carries this server's address and key pin."));
        }

        var message = string.Join(" ", issues.Select(i => i.Message));
        var mismatch = issues.Any(i => i.Code is "address-mismatch" or "key-pin-mismatch");
        return Task.FromResult(mismatch
            ? SetupCheckResult.Red(message, _rebuildAdvice)
            : SetupCheckResult.Amber(message, _rebuildAdvice));
    }
}
