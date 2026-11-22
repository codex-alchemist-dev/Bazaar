namespace Bazaar.Server.Setup;

/// <summary>
/// The shared dry-run and apply backend of the setup wizard (doc 45 section 1.1). It runs the same <see cref="SetupOrchestrator"/>
/// that <c>setup --dry-run</c> and <c>setup apply</c> use, with no file, so a wizard run and a flag run with the same answers give
/// the same plan hash. The CLI and a server terminal UI both use it; each supplies only a renderer for the report lines.
/// </summary>
public sealed class SetupWizardBackend(
    IReadOnlyDictionary<string, string?> environment,
    ICliApplyGate applyGate,
    ISetupReportRenderer renderer,
    ISetupSettingCatalog settings) : ISetupWizardBackend
{
    private readonly IReadOnlyDictionary<string, string?> _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    private readonly ICliApplyGate _applyGate = applyGate ?? throw new ArgumentNullException(nameof(applyGate));
    private readonly ISetupSettingCatalog _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly ISetupReportRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    public SetupWizardReview DryRun(IReadOnlyList<string> setArguments) =>
        Review(SetupOrchestrator.DryRun(null, setArguments, _environment, _settings, _applyGate.CoveredSections));

    public async Task<SetupWizardReview> ApplyAsync(
        IReadOnlyList<string> setArguments,
        string planHash,
        SetupEntryPath entryPath,
        CancellationToken cancellationToken) =>
        Review(await SetupOrchestrator.ApplyAsync(null, setArguments, _environment, planHash, _applyGate, _settings, cancellationToken, entryPath));

    private SetupWizardReview Review(SetupReport report) =>
        new(report.ExitCode, report.PlanHash, _renderer.RenderLines(report), report);
}
