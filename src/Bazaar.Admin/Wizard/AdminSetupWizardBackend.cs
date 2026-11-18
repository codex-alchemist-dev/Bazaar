using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// The wizard's dry run and apply, over the same <see cref="SetupCommands"/> that <c>setup --dry-run</c> and <c>setup apply</c>
/// use. Reports are rendered with the same plain-text renderer, so the lines match the flag form.
/// </summary>
internal sealed class AdminSetupWizardBackend(IReadOnlyDictionary<string, string?> environment, ICliApplyGate applyGate) : ISetupWizardBackend
{
    public SetupWizardReview DryRun(IReadOnlyList<string> setArguments) =>
        Render(SetupCommands.DryRun(null, setArguments, environment));

    public async Task<SetupWizardReview> ApplyAsync(
        IReadOnlyList<string> setArguments,
        string planHash,
        SetupEntryPath entryPath,
        CancellationToken cancellationToken) =>
        Render(await SetupCommands.ApplyAsync(null, setArguments, environment, planHash, applyGate, cancellationToken, entryPath));

    private static SetupWizardReview Render(AdminReport report)
    {
        using var writer = new StringWriter();
        AdminTextRenderer.Write(report, writer);
        var lines = writer.ToString().Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        return new SetupWizardReview(report.ExitCode, report.PlanHash, lines);
    }
}
