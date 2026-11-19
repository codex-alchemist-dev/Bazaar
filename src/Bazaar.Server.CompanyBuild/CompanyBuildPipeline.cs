using Bazaar.CompanyIdentity;
using Bazaar.Server.CompanyBuild.Abstractions;

namespace Bazaar.Server.CompanyBuild;

/// <summary>
/// Builds a company's whole client set (docs/design/37 section 4a). Each target runs through <see cref="CompanyBuildRunner"/> with
/// the company's identity baked in. A failed target leaves the other outputs in place and is reported in plain text. A set with any
/// failed target is marked INCOMPLETE and never presented as finished. Every run adds a line to the build history.
/// </summary>
public static class CompanyBuildPipeline
{
    /// <param name="plan">The composed module plan.</param>
    /// <param name="identity">The company server's identity, baked into every target.</param>
    /// <param name="targets">The artifacts to build. Ids must be unique and safe folder names.</param>
    /// <param name="outputRoot">The root of all company output folders, for example <c>out</c>.</param>
    /// <param name="identitySourceDirectory">The client source folder that receives the generated <c>CompanyBuildIdentity.cs</c>.</param>
    /// <param name="buildTarget">Builds one target into the folder it is given. Throwing fails that attempt.</param>
    /// <param name="isSigned">Whether the company supplied signing settings. Unsigned sets are labelled.</param>
    /// <param name="maxAttempts">Attempts per target, from 1 to <see cref="CompanyBuildRunner.MaxAllowedAttempts"/>.</param>
    /// <param name="timeProvider">Clock for the report, so tests can fix the time.</param>
    /// <param name="cancellationToken">Cancels the run. Cancellation is not a failed target.</param>
    /// <exception cref="CompanyBuildException">The company id, a target id or the target list is invalid.</exception>
    public static async Task<CompanyBuildSetReport> RunAsync(
        CompanyBuildPlan plan,
        CompanyServerIdentity identity,
        IReadOnlyList<CompanyBuildTarget> targets,
        string outputRoot,
        string identitySourceDirectory,
        Func<CompanyBuildTarget, string, CancellationToken, Task> buildTarget,
        bool isSigned = false,
        int maxAttempts = CompanyBuildRunner.DefaultMaxAttempts,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(buildTarget);

        if (targets.Count == 0)
        {
            throw new CompanyBuildException("A company build needs at least one target.");
        }

        var layout = new CompanyBuildOutputLayout(outputRoot, plan.CompanyId);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            _ = layout.TargetDirectory(target.Id);
            if (!seen.Add(target.Id))
            {
                throw new CompanyBuildException($"Target id '{target.Id}' appears more than once.");
            }
        }

        Directory.CreateDirectory(layout.CompanyDirectory);
        File.Delete(layout.IncompleteMarkerPath);

        var reports = new List<CompanyBuildTargetReport>(targets.Count);
        foreach (var target in targets)
        {
            var directory = layout.TargetDirectory(target.Id);
            var report = await CompanyBuildRunner.RunAsync(
                plan,
                identity,
                identitySourceDirectory,
                async (_, token) =>
                {
                    // Each attempt starts from an empty folder, so a failed attempt never leaks partial output into the next.
                    ResetDirectory(directory);
                    await buildTarget(target, directory, token).ConfigureAwait(false);
                },
                maxAttempts,
                cancellationToken).ConfigureAwait(false);

            if (!report.Succeeded)
            {
                // A failed target's partial output is removed; the other targets' outputs stay in place.
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            reports.Add(new CompanyBuildTargetReport(target.Id, report));
        }

        var complete = reports.All(r => r.Report.Succeeded);
        var finished = new CompanyBuildSetReport(
            plan.CompanyId,
            (timeProvider ?? TimeProvider.System).GetUtcNow(),
            reports,
            complete,
            isSigned,
            layout.CompanyDirectory);

        await CompanyBuildFiles.WriteChecksumsAsync(layout, cancellationToken).ConfigureAwait(false);
        CompanyBuildFiles.WriteAtomic(layout.ReadmePath, CompanyBuildFiles.RenderReadme(plan, finished));
        CompanyBuildFiles.WriteAtomic(layout.ReportPath, CompanyBuildFiles.RenderReport(finished));
        CompanyBuildFiles.AppendHistory(layout, finished);
        if (!complete)
        {
            CompanyBuildFiles.WriteAtomic(layout.IncompleteMarkerPath, "At least one target failed. See BUILD-REPORT.txt.\n");
        }

        return finished;
    }

    private static void ResetDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }
}
