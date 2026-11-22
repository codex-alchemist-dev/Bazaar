namespace Bazaar.Server.Setup.Constitution;

/// <summary>
/// Makes a default Constitution exist (docs/design/24-company-systems-core.md section 1; docs/design/45-onboarding.md section 1.15).
/// Without one the first-run seed has nothing to accept and the default agent stays <c>created</c>. The step only drafts the
/// document into the Vault. The owner's acceptance stays the owner's own act (<c>firstRun.acceptConstitution</c>), never made here.
/// </summary>
/// <param name="vault">The Vault port.</param>
public sealed class ConstitutionSeedStep(IConstitutionVault vault) : ISetupStep
{
    /// <summary>Stable id, written to the audit row.</summary>
    public const string StepId = "constitution.seed";

    /// <summary>Just before the first-run seed (135), which reads the Constitution to record acceptance.</summary>
    public const int StepOrder = 134;

    /// <inheritdoc />
    public string Id => StepId;

    /// <inheritdoc />
    public int Order => StepOrder;

    /// <inheritdoc />
    public IReadOnlyCollection<string> Covers => ["firstRun"];

    /// <inheritdoc />
    public async Task<SetupStepResult> ApplyAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(context);

        // The Constitution gates activation, so it is only needed when the seed makes an agent or the owner accepts one.
        var section = plan.FirstRun;
        if (!section.SeedDefaultAgent && !section.AcceptConstitution) return SetupStepResult.Ok;

        try
        {
            var result = await vault.EnsureAsync(DefaultConstitution.Markdown, cancellationToken);
            return result.Created
                ? new SetupStepResult(true, "Seeded the default Constitution in the Vault. The owner reviews and edits it, then accepts it.")
                : SetupStepResult.Ok;
        }
        catch (ConstitutionVaultException exception)
        {
            return new SetupStepResult(false, exception.Message);
        }
    }

    /// <summary>The Vault is append-only history and an edited Constitution may exist, so there is nothing to undo.</summary>
    /// <inheritdoc />
    public Task RollbackAsync(SetupPlan plan, SetupApplyContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
