namespace Bazaar.Server.Setup;

/// <summary>The outcome of one wizard run: the exit code, the <c>path=value</c> answers, and the plan hash once a review ran.</summary>
public sealed record SetupWizardResult(int ExitCode, IReadOnlyList<string> SetArguments, string? PlanHash);
