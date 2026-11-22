namespace Bazaar.Server.Setup;

/// <summary>
/// The status listener is inside its floor (doc 45 section 1.12, doc 03): loopback, unless an owner approval is present. The plan
/// carries no approval field, so any non-loopback bind is outside the floor. The validator already holds such a plan; this check
/// reports it red, because verification must not call an unapproved public bind complete.
/// </summary>
public sealed class StatusBindCheck : ISetupVerificationCheck
{
    private const string _loopback = "loopback";

    /// <inheritdoc />
    public string Id => "network.statusBind";

    /// <inheritdoc />
    public string Title => "Status listener bind inside its floor";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var bind = context.Plan.Network.StatusBind;
        SetupCheckResult result = string.Equals(bind, _loopback, StringComparison.Ordinal)
            ? SetupCheckResult.Green("The status listener binds to loopback.")
            : SetupCheckResult.Red(
                "network.statusBind is outside its floor. A non-loopback bind needs a Tier 2 owner approval, and this plan records none.",
                "Set network.statusBind to loopback. A non-loopback bind needs an owner approval first (doc 03).");
        return Task.FromResult(result);
    }
}
