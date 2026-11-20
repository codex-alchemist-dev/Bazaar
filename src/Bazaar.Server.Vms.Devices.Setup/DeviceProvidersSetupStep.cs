using Bazaar.Server.Setup;

namespace Bazaar.Server.Vms.Devices.Setup;

/// <summary>
/// Setup step for the optional device-guest providers (doc 62 section 7). It validates the plan's device settings and changes
/// nothing: the settings themselves are written by the applier, and the modules are installed by the module step.
/// </summary>
public sealed class DeviceProvidersSetupStep : ISetupStep
{
    public const string StepId = "vms.devices";

    /// <summary>After the modules step (doc 45 section 1.3 step 14), before status and network.</summary>
    public const int StepOrder = 1450;

    public string Id => StepId;

    public int Order => StepOrder;

    public Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        var issues = DeviceSetupValidator.Validate(plan);
        if (issues.Count == 0)
        {
            return Task.FromResult(SetupStepResult.Ok);
        }

        var message = string.Join(" ", issues.Select(i => $"{i.Path} {i.Message}"));
        return Task.FromResult(new SetupStepResult(false, message));
    }
}
