namespace Bazaar.Server.Setup;

/// <summary>
/// At least one owner second factor is required (doc 45 section 1.4: the owner passkey or hardware key, a second factor for recovery
/// and high-risk actions). The check reads <c>owner.requireSecondFactor</c>. Enrolment of the factor itself is not checked here.
/// </summary>
public sealed class OwnerSecondFactorCheck : ISetupVerificationCheck
{
    /// <inheritdoc />
    public string Id => "owner.secondFactor";

    /// <inheritdoc />
    public string Title => "Owner second factor required";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        SetupCheckResult result = context.Plan.Owner.RequireSecondFactor
            ? SetupCheckResult.Green("An owner second factor is required.")
            : SetupCheckResult.Red(
                "owner.requireSecondFactor is false. At least one owner second factor is required.",
                "Set owner.requireSecondFactor to true.");
        return Task.FromResult(result);
    }
}
