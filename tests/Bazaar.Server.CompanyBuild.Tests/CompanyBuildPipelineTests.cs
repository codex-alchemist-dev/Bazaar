using System.Security.Cryptography;
using System.Text;
using Bazaar.CompanyIdentity;
using Bazaar.Server.CompanyBuild;
using Bazaar.Server.CompanyBuild.Abstractions;
using Cdax.Core;

namespace Bazaar.Server.CompanyBuild.Tests;

public sealed class CompanyBuildPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-pipeline-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static CompanyBuildPlan Plan(string company = "acme") =>
        new(company, [new ComponentManifest("gc", "0.1.0", ComponentKind.Feature, [], [])], ["vault"]);

    private static CompanyServerIdentity Identity() =>
        CompanyServerIdentity.Create(new Uri("https://company.example/"), new string('b', 64));

    private static IReadOnlyList<CompanyBuildTarget> Targets() =>
        [new("windows-msix", "client/win.csproj"), new("android-apk", "client/android.csproj")];

    private static async Task WriteArtifact(CompanyBuildTarget target, string directory, CancellationToken token) =>
        await File.WriteAllTextAsync(Path.Combine(directory, target.Id + ".bin"), "payload-" + target.Id, token);

    private Task<CompanyBuildSetReport> Run(
        Func<CompanyBuildTarget, string, CancellationToken, Task> build,
        bool signed = false,
        IReadOnlyList<CompanyBuildTarget>? targets = null,
        string company = "acme") =>
        CompanyBuildPipeline.RunAsync(
            Plan(company),
            Identity(),
            targets ?? Targets(),
            Path.Combine(_root, "out"),
            Path.Combine(_root, "src"),
            build,
            isSigned: signed,
            cancellationToken: TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_clean_set_is_complete_with_checksums_report_and_history()
    {
        var report = await Run(WriteArtifact);

        Assert.True(report.Complete);
        var layout = new CompanyBuildOutputLayout(Path.Combine(_root, "out"), "acme");
        Assert.False(File.Exists(layout.IncompleteMarkerPath));

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("payload-windows-msix")));
        var sums = await File.ReadAllTextAsync(layout.ChecksumsPath, TestContext.Current.CancellationToken);
        Assert.Contains($"{expected}  windows-msix/windows-msix.bin", sums);
        Assert.Contains("android-apk/android-apk.bin", sums);
        Assert.DoesNotContain("README.txt", sums);

        Assert.Single(File.ReadAllLines(layout.HistoryPath));
        Assert.Contains("Added as hard dependencies: vault", File.ReadAllText(layout.ReadmePath));
    }

    [Fact]
    public async Task An_unsigned_set_is_labelled_and_a_signed_set_is_not()
    {
        await Run(WriteArtifact);
        var layout = new CompanyBuildOutputLayout(Path.Combine(_root, "out"), "acme");
        Assert.Contains("UNSIGNED BUILD", File.ReadAllText(layout.ReadmePath));

        await Run(WriteArtifact, signed: true);
        Assert.DoesNotContain("UNSIGNED BUILD", File.ReadAllText(layout.ReadmePath));
    }

    [Fact]
    public async Task A_failed_target_leaves_the_others_and_marks_the_set_incomplete()
    {
        var report = await Run((target, directory, token) =>
            target.Id == "android-apk"
                ? throw new InvalidOperationException("sdk missing")
                : WriteArtifact(target, directory, token));

        Assert.False(report.Complete);
        var layout = new CompanyBuildOutputLayout(Path.Combine(_root, "out"), "acme");
        Assert.True(File.Exists(layout.IncompleteMarkerPath));
        Assert.True(File.Exists(Path.Combine(layout.TargetDirectory("windows-msix"), "windows-msix.bin")));
        Assert.False(Directory.Exists(layout.TargetDirectory("android-apk")));

        var text = File.ReadAllText(layout.ReportPath);
        Assert.Contains("Target android-apk: FAILED", text);
        Assert.Contains("sdk missing", text);
        Assert.Contains("INCOMPLETE", File.ReadAllText(layout.ReadmePath));
        var sums = File.ReadAllText(layout.ChecksumsPath);
        Assert.DoesNotContain("android-apk", sums);
    }

    [Fact]
    public async Task Retries_are_reported_even_when_the_target_later_succeeds()
    {
        var calls = 0;
        var report = await Run((target, directory, token) =>
        {
            if (target.Id == "windows-msix" && ++calls == 1)
            {
                File.WriteAllText(Path.Combine(directory, "partial.tmp"), "half");
                throw new InvalidOperationException("toolchain busy");
            }

            return WriteArtifact(target, directory, token);
        });

        Assert.True(report.Complete);
        var windows = report.Targets.Single(t => t.TargetId == "windows-msix").Report;
        Assert.Equal(2, windows.Attempts);
        Assert.Equal(1, windows.Retries);
        var layout = new CompanyBuildOutputLayout(Path.Combine(_root, "out"), "acme");
        Assert.Contains("Retries: 1", File.ReadAllText(layout.ReportPath));
        Assert.False(File.Exists(Path.Combine(layout.TargetDirectory("windows-msix"), "partial.tmp")));
    }

    [Fact]
    public async Task Unsafe_company_and_target_ids_are_refused()
    {
        await Assert.ThrowsAsync<CompanyBuildException>(() => Run(WriteArtifact, company: "../escape"));
        await Assert.ThrowsAsync<CompanyBuildException>(() =>
            Run(WriteArtifact, targets: [new CompanyBuildTarget("a/b", "p.csproj")]));
        await Assert.ThrowsAsync<CompanyBuildException>(() =>
            Run(WriteArtifact, targets: [new CompanyBuildTarget("INCOMPLETE", "p.csproj")]));
    }

    [Fact]
    public async Task Duplicate_or_missing_targets_are_refused()
    {
        await Assert.ThrowsAsync<CompanyBuildException>(() =>
            Run(WriteArtifact, targets: [new CompanyBuildTarget("x", "a"), new CompanyBuildTarget("X", "b")]));
        await Assert.ThrowsAsync<CompanyBuildException>(() => Run(WriteArtifact, targets: []));
    }

    [Fact]
    public async Task A_rebuild_keeps_history_and_clears_the_incomplete_marker()
    {
        await Run((_, _, _) => throw new InvalidOperationException("boom"));
        var layout = new CompanyBuildOutputLayout(Path.Combine(_root, "out"), "acme");
        Assert.True(File.Exists(layout.IncompleteMarkerPath));

        var report = await Run(WriteArtifact);

        Assert.True(report.Complete);
        Assert.False(File.Exists(layout.IncompleteMarkerPath));
        Assert.Equal(2, File.ReadAllLines(layout.HistoryPath).Length);
    }
}
