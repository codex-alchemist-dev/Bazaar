using System.Text.RegularExpressions;

namespace Bazaar.Server.Verification;

/// <summary>
/// Server-run verification gate (doc 34). Clones the head ref fresh, builds it, and compares it with base.
/// Agent self-reports never count: the only exit codes trusted are the ones this class observes.
/// </summary>
public sealed partial class VerificationGate(IProcessRunner runner)
{
    public async Task<GateReport> RunAsync(GateRequest request, CancellationToken ct = default)
    {
        var findings = new List<GateFinding>();
        var commands = new List<CommandRecord>();

        var cloneDir = Path.Combine(request.WorkDirectory, "clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(request.WorkDirectory);
        var work = new GitRepo(runner, request.WorkDirectory);
        await work.RunAsync("clone", "--quiet", "--no-hardlinks", request.RepoPath, cloneDir);
        var clone = new GitRepo(runner, cloneDir);
        await clone.RunAsync("checkout", "--quiet", "--detach", request.HeadRef);

        // Resolve to commits so refs from the submitter cannot shift under us.
        var baseSha = (await clone.RunAsync("rev-parse", $"{request.BaseRef}^{{commit}}")).Trim();
        var headSha = (await clone.RunAsync("rev-parse", "HEAD")).Trim();

        var changed = await clone.ChangedFilesAsync(baseSha, headSha);
        var added = await clone.AddedLinesAsync(baseSha, headSha);

        await CheckBuildAsync(request, cloneDir, findings, commands, ct);
        var (baseCount, headCount) = await CheckTestCountAsync(clone, baseSha, headSha, findings);
        CheckTestEdits(request, changed, findings);
        CheckSkips(added, findings);
        CheckTimeouts(request, added, commands, findings);
        CheckUiChange(changed, findings);
        CheckBehaviourChange(changed, findings);

        return new GateReport(findings, commands, baseCount, headCount);
    }

    private async Task CheckBuildAsync(GateRequest request, string cloneDir, List<GateFinding> findings, List<CommandRecord> commands, CancellationToken ct)
    {
        var display = $"{request.BuildExecutable} {string.Join(' ', request.BuildArguments)}";
        var result = await runner.RunAsync(request.BuildExecutable, request.BuildArguments, cloneDir, request.BuildTimeout, ct);
        var ok = result.ExitCode == 0 && !result.TimedOut;
        commands.Add(new CommandRecord(display, result.ExitCode, result.TimedOut, ok, result.Output));
        if (result.TimedOut)
        {
            findings.Add(new GateFinding(GateCheckKind.CleanBuild, GateSeverity.Fail, $"Clean build timed out after {request.BuildTimeout}: {display}"));
        }
        else if (!ok)
        {
            findings.Add(new GateFinding(GateCheckKind.CleanBuild, GateSeverity.Fail, $"Clean build failed with exit code {result.ExitCode}: {display}"));
        }
        else
        {
            findings.Add(new GateFinding(GateCheckKind.CleanBuild, GateSeverity.Pass, "Clean build succeeded."));
        }
    }

    private static async Task<(int Base, int Head)> CheckTestCountAsync(GitRepo clone, string baseSha, string headSha, List<GateFinding> findings)
    {
        var baseCount = await CountAtAsync(clone, baseSha);
        var headCount = await CountAtAsync(clone, headSha);
        findings.Add(headCount < baseCount
            ? new GateFinding(GateCheckKind.TestCount, GateSeverity.Fail, $"Test count dropped from {baseCount} to {headCount}.")
            : new GateFinding(GateCheckKind.TestCount, GateSeverity.Pass, $"Test count {baseCount} -> {headCount}."));
        return (baseCount, headCount);
    }

    private static async Task<int> CountAtAsync(GitRepo repo, string gitRef)
    {
        var total = 0;
        foreach (var file in (await repo.ListFilesAsync(gitRef)).Where(PathRules.IsTestFile))
        {
            total += TestCounter.Count(await repo.ShowAsync(gitRef, file));
        }

        return total;
    }

    private static void CheckTestEdits(GateRequest request, IReadOnlyList<ChangedFile> changed, List<GateFinding> findings)
    {
        // Adding a test file is fine; changing or deleting an existing one needs a reviewer flag.
        var edited = changed.Where(c => c.Status != 'A' && PathRules.IsTestFile(c.Path)).Select(c => c.Path).ToList();
        if (edited.Count > 0 && !request.ReviewerApprovedTestEdits)
        {
            findings.Add(new GateFinding(GateCheckKind.TestEdits, GateSeverity.Fail, $"Existing test files changed without a reviewer flag: {string.Join(", ", edited)}"));
        }
        else
        {
            findings.Add(new GateFinding(GateCheckKind.TestEdits, GateSeverity.Pass, "No unflagged edits to existing tests."));
        }
    }

    private static void CheckSkips(IReadOnlyList<AddedLine> added, List<GateFinding> findings)
    {
        var hits = added.Where(l => l.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && SkipOrExplicit().IsMatch(l.Text)).ToList();
        findings.Add(hits.Count > 0
            ? new GateFinding(GateCheckKind.Skips, GateSeverity.Fail, $"New Skip or Explicit: {string.Join("; ", hits.Select(h => $"{h.Path}: {h.Text.Trim()}"))}")
            : new GateFinding(GateCheckKind.Skips, GateSeverity.Pass, "No new Skip or Explicit."));
    }

    private static void CheckTimeouts(GateRequest request, IReadOnlyList<AddedLine> added, IReadOnlyList<CommandRecord> commands, List<GateFinding> findings)
    {
        var problems = new List<string>();
        foreach (var record in commands.Concat(request.SubmittedCommandLog ?? []))
        {
            if (record.TimedOut && (record.RecordedSuccess || record.ExitCode == 0))
            {
                problems.Add($"timed-out command recorded as success: {record.Command}");
            }
        }

        foreach (var line in added.Where(l => PathRules.IsBuildScript(l.Path) && MaskedFailure().IsMatch(l.Text)))
        {
            problems.Add($"failure or timeout masked in {line.Path}: {line.Text.Trim()}");
        }

        findings.Add(problems.Count > 0
            ? new GateFinding(GateCheckKind.Timeouts, GateSeverity.Fail, string.Join("; ", problems))
            : new GateFinding(GateCheckKind.Timeouts, GateSeverity.Pass, "No timeout recorded or masked as success."));
    }

    private static void CheckUiChange(IReadOnlyList<ChangedFile> changed, List<GateFinding> findings)
    {
        var ui = changed.Where(c => PathRules.IsUiFile(c.Path)).Select(c => c.Path).ToList();
        var hasHeadless = changed.Any(c => c.Status != 'D' && PathRules.IsHeadlessTestFile(c.Path));
        findings.Add(ui.Count > 0 && !hasHeadless
            ? new GateFinding(GateCheckKind.UiChange, GateSeverity.Fail, $"UI files changed with no Headless test change: {string.Join(", ", ui)}")
            : new GateFinding(GateCheckKind.UiChange, GateSeverity.Pass, "No UI change without a Headless test."));
    }

    private static void CheckBehaviourChange(IReadOnlyList<ChangedFile> changed, List<GateFinding> findings)
    {
        var source = changed.Any(c => PathRules.IsSourceFile(c.Path));
        var tests = changed.Any(c => PathRules.IsTestFile(c.Path));
        findings.Add(source && !tests
            ? new GateFinding(GateCheckKind.BehaviourChange, GateSeverity.Warning, "Source changed with no test change (warning, not a gate).")
            : new GateFinding(GateCheckKind.BehaviourChange, GateSeverity.Pass, "Source and test changes are consistent."));
    }

    [GeneratedRegex(@"\bSkip\s*=|\bExplicit\b")]
    private static partial Regex SkipOrExplicit();

    [GeneratedRegex(@"\|\|\s*true\b|continue-on-error:\s*true|;\s*exit\s+0\b|-ErrorAction\s+SilentlyContinue|\|\|\s*exit\s+0\b")]
    private static partial Regex MaskedFailure();
}
