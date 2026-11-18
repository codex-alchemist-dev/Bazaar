using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The interactive setup wizard of <c>bazaar-admin setup</c> (doc 45 section 1.1). A thin wrapper: the questions, the validation,
/// the plan hash confirmation and the apply all live in <see cref="SetupWizardEngine"/>. This class only gives the engine the
/// prompter and the dry-run and apply path of this command (<see cref="AdminSetupWizardBackend"/>).
/// </summary>
internal sealed class SetupWizard(
    ISetupPrompter prompter,
    IReadOnlyDictionary<string, string?> environment,
    ICliApplyGate applyGate,
    ISetupSecretWriter? secretWriter = null)
{
    public Task<SetupWizardResult> RunAsync(CancellationToken cancellationToken) =>
        new SetupWizardEngine(prompter, new AdminSetupWizardBackend(environment, applyGate), secretWriter).RunAsync(cancellationToken);
}
