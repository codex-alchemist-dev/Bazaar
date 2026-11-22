using System.Text.Json;

namespace Bazaar.Server.Setup;

/// <summary>
/// The identity KEK custody warning (doc 14 "Linux TPM-sealed KEK", DECIDED 2026-11-19, DECISIONS-LOG round 30). A plan that picks
/// <c>custody.identityKek = tpm</c> and also opts in to a virtual TPM (<c>identity.tpm.allowVirtual = true</c>) gets an amber result,
/// because a vTPM is weaker custody: the hypervisor operator can read its state and it never counts as hardware custody for the doc 48
/// floors. Every other choice is green. The check reads the plan only; whether this host really is a VM is decided by the TPM provider
/// at start-up, which refuses a VM host without the opt-in and records the custody class in the audit log.
/// </summary>
public sealed class IdentityKekCustodyCheck : ISetupVerificationCheck
{
    /// <summary>The setting key that opts a virtual-machine host in to a vTPM.</summary>
    public const string AllowVirtualKey = "identity.tpm.allowVirtual";

    /// <inheritdoc />
    public string Id => "identity.kekCustody";

    /// <inheritdoc />
    public string Title => "Identity KEK custody";

    /// <inheritdoc />
    public Task<SetupCheckResult> RunAsync(SetupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plan = context.Plan;
        SetupCheckResult result;
        if (!string.Equals(plan.Custody.IdentityKek, "tpm", StringComparison.Ordinal))
        {
            result = SetupCheckResult.Green("The identity KEK is not TPM-sealed, so there is no TPM custody class to warn about.");
        }
        else if (plan.Settings.TryGetValue(AllowVirtualKey, out var allow) && allow.ValueKind == JsonValueKind.True)
        {
            result = SetupCheckResult.Amber(
                "The identity KEK is TPM-sealed and a virtual TPM is allowed. A vTPM is weaker custody than a hardware TPM.",
                "Use a hardware TPM, or accept the weaker custody. The custody class is recorded in the audit log at start-up.");
        }
        else
        {
            result = SetupCheckResult.Green(
                "The identity KEK is TPM-sealed. A host that is a virtual machine is refused unless identity.tpm.allowVirtual is true.");
        }

        return Task.FromResult(result);
    }
}
