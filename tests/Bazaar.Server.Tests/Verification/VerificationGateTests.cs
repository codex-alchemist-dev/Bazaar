using Bazaar.Server.Verification;

namespace Bazaar.Server.Tests.Verification;

/// <summary>Gate self-tests (doc 34): each seeded bad change must fail its check; the clean control must pass.</summary>
public sealed class VerificationGateTests : IDisposable
{
    private const string _marker = "BREAK_THE_BUILD";
    private const string _hangMarker = "HANG_THE_BUILD";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-gate-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly ProcessRunner _git = new();
    private static readonly string[] _gitIdentity = ["-c", "user.name=Gate Test", "-c", "user.email=gate@example.test", "-c", "commit.gpgsign=false"];

    public VerificationGateTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "--quiet", "-b", "main");
        Write("src/Bazaar.Server/Thing.cs", "namespace X; public class Thing { }\n");
        Write("src/Bazaar.Client.Core/MainWindow.axaml", "<Window />\n");
        Write("build.sh", "#!/usr/bin/env bash\nset -e\ndotnet build\n");
        Write("tests/Bazaar.Server.Tests/ThingTests.cs", "public class ThingTests\n{\n    [Fact]\n    public void A() { }\n\n    [Fact]\n    public void B() { }\n\n    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void C(int x) { }\n}\n");
        Write("tests/Bazaar.Client.Core.Tests/WindowTests.cs", "public class WindowTests\n{\n    [Fact]\n    public void D() { }\n}\n");
        Commit("base");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task CleanControlPasses()
    {
        Write("src/Bazaar.Server/Thing.cs", "namespace X; public class Thing { public int N => 1; }\n");
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("clean change");

        var report = await RunGateAsync();

        Assert.True(report.Passed, Describe(report));
        Assert.Equal(5, report.BaseTestCount);
        Assert.Equal(6, report.HeadTestCount);
        Assert.DoesNotContain(report.Findings, f => f.Severity == GateSeverity.Warning);
    }

    [Fact]
    public async Task BrokenBuildFails()
    {
        Write("src/Bazaar.Server/Thing.cs", $"// {_marker}\n");
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("breaks build");

        AssertFails(await RunGateAsync(), GateCheckKind.CleanBuild);
    }

    [Fact]
    public async Task BuildDependingOnUncommittedArtifactsFailsInCleanClone()
    {
        // Works in the agent's tree because obj/ exists there; the clean clone has no such file.
        Write("obj/generated.txt", "x");
        Write("build.sh", "#!/usr/bin/env bash\nset -e\ndotnet build\n");
        Write(".gitignore", "obj/\n");
        Commit("ignore obj");
        File.WriteAllText(Path.Combine(_repo, "obj", "generated.txt"), "x");

        var report = await RunGateAsync(requireArtifact: true);

        AssertFails(report, GateCheckKind.CleanBuild);
    }

    [Fact]
    public async Task DeletedTestDropsCountAndIsAnUnflaggedEdit()
    {
        File.Delete(Path.Combine(_repo, "tests", "Bazaar.Server.Tests", "ThingTests.cs"));
        Commit("delete test");

        var report = await RunGateAsync();

        AssertFails(report, GateCheckKind.TestCount, GateCheckKind.TestEdits);
        Assert.Equal(1, report.HeadTestCount);
    }

    [Fact]
    public async Task AddedSkipFails()
    {
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact(Skip = \"flaky\")]\n    public void E() { }\n}\n");
        Commit("skip");

        AssertFails(await RunGateAsync(), GateCheckKind.Skips);
    }

    [Fact]
    public async Task AddedExplicitFails()
    {
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact, Explicit]\n    public void E() { }\n}\n");
        Commit("explicit");

        AssertFails(await RunGateAsync(), GateCheckKind.Skips);
    }

    [Fact]
    public async Task EditedTestFileFailsWithoutReviewerFlagAndPassesWithIt()
    {
        Write("tests/Bazaar.Server.Tests/ThingTests.cs", "public class ThingTests\n{\n    [Fact]\n    public void A() { }\n\n    [Fact]\n    public void B() { Assert.True(true); }\n\n    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void C(int x) { }\n}\n");
        Commit("edit test");

        AssertFails(await RunGateAsync(), GateCheckKind.TestEdits);
        Assert.True((await RunGateAsync(reviewerFlag: true)).Passed);
    }

    [Fact]
    public async Task DroppedTheoryRowDropsCount()
    {
        Write("tests/Bazaar.Server.Tests/ThingTests.cs", "public class ThingTests\n{\n    [Fact]\n    public void A() { }\n\n    [Fact]\n    public void B() { }\n\n    [Theory]\n    [InlineData(1)]\n    public void C(int x) { }\n}\n");
        Commit("drop row");

        AssertFails(await RunGateAsync(reviewerFlag: true), GateCheckKind.TestCount);
    }

    [Fact]
    public async Task TimedOutBuildIsAFailureNotASuccess()
    {
        Write("src/Bazaar.Server/Thing.cs", $"// {_hangMarker}\n");
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("hangs");

        var report = await RunGateAsync();

        AssertFails(report, GateCheckKind.CleanBuild);
        Assert.True(report.Commands[0].TimedOut);
        Assert.False(report.Commands[0].RecordedSuccess);
    }

    [Fact]
    public async Task SubmittedLogWithTimeoutRecordedAsSuccessFails()
    {
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("clean");
        var log = new[] { new CommandRecord("dotnet test", ExitCode: 0, TimedOut: true, RecordedSuccess: true) };

        AssertFails(await RunGateAsync(submittedLog: log), GateCheckKind.Timeouts);
    }

    [Fact]
    public async Task BuildScriptMaskingFailureFails()
    {
        Write("build.sh", "#!/usr/bin/env bash\ndotnet test || true\n");
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("mask");

        AssertFails(await RunGateAsync(), GateCheckKind.Timeouts);
    }

    [Fact]
    public async Task UiChangeWithoutHeadlessTestFails()
    {
        Write("src/Bazaar.Client.Core/MainWindow.axaml", "<Window Title=\"new\" />\n");
        Write("tests/Bazaar.Server.Tests/NewTests.cs", "public class NewTests\n{\n    [Fact]\n    public void E() { }\n}\n");
        Commit("ui without headless");

        AssertFails(await RunGateAsync(), GateCheckKind.UiChange);
    }

    [Fact]
    public async Task UiChangeWithHeadlessTestPasses()
    {
        Write("src/Bazaar.Client.Core/MainWindow.axaml", "<Window Title=\"new\" />\n");
        Write("tests/Bazaar.Client.Core.Tests/TitleTests.cs", "public class TitleTests\n{\n    [Fact]\n    public void F() { }\n}\n");
        Commit("ui with headless");

        Assert.True((await RunGateAsync()).Passed);
    }

    [Fact]
    public async Task SourceChangeWithoutTestsWarnsButDoesNotGate()
    {
        Write("src/Bazaar.Server/Thing.cs", "namespace X; public class Thing { public int N => 2; }\n");
        Commit("no tests");

        var report = await RunGateAsync();

        Assert.True(report.Passed, Describe(report));
        Assert.Contains(report.Findings, f => f is { Check: GateCheckKind.BehaviourChange, Severity: GateSeverity.Warning });
    }

    [Theory]
    [InlineData("[Fact]\npublic void A() { }", 1)]
    [InlineData("[Theory]\npublic void A() { }", 1)]
    [InlineData("[Theory]\n[InlineData(1)]\n[InlineData(2)]\n[InlineData(3)]\npublic void A(int x) { }", 3)]
    [InlineData("// no tests here", 0)]
    public void TestCounterCountsFactsAndTheoryRows(string source, int expected) =>
        Assert.Equal(expected, TestCounter.Count(source));

    private static void AssertFails(GateReport report, params GateCheckKind[] expected)
    {
        Assert.False(report.Passed, Describe(report));
        var failed = report.Failures.Select(f => f.Check).ToHashSet();
        foreach (var kind in expected)
        {
            Assert.Contains(kind, failed);
        }
    }

    private static string Describe(GateReport report) =>
        string.Join(Environment.NewLine, report.Findings.Select(f => $"{f.Severity} {f.Check}: {f.Message}"));

    private Task<GateReport> RunGateAsync(bool reviewerFlag = false, IReadOnlyList<CommandRecord>? submittedLog = null, bool requireArtifact = false)
    {
        var gate = new VerificationGate(new FakeBuildRunner(_git, requireArtifact));
        var request = new GateRequest(_repo, "main~1", "main", Path.Combine(_root, "work"), "fake-build", [], TimeSpan.FromSeconds(30), reviewerFlag, submittedLog);
        return gate.RunAsync(request);
    }

    private void Git(params string[] args)
    {
        var all = _gitIdentity.Concat(args).ToArray();
        var result = _git.RunAsync("git", all, _repo, TimeSpan.FromMinutes(1)).GetAwaiter().GetResult();
        Assert.True(result.ExitCode == 0, result.Output);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Commit(string message)
    {
        Git("add", "-A");
        Git("commit", "--quiet", "-m", message);
    }

    /// <summary>Real git, fake build: the build "fails" on a marker in any source, "hangs" on another, and needs obj/ when asked.</summary>
    private sealed class FakeBuildRunner(IProcessRunner git, bool requireArtifact) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct = default)
        {
            if (executable == "git")
            {
                return git.RunAsync(executable, arguments, workingDirectory, timeout, ct);
            }

            var text = string.Concat(Directory.EnumerateFiles(workingDirectory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
            if (text.Contains(_hangMarker, StringComparison.Ordinal))
            {
                return Task.FromResult(new ProcessResult(-1, true, "timed out"));
            }

            if (text.Contains(_marker, StringComparison.Ordinal) || (requireArtifact && !Directory.Exists(Path.Combine(workingDirectory, "obj"))))
            {
                return Task.FromResult(new ProcessResult(1, false, "build error"));
            }

            return Task.FromResult(new ProcessResult(0, false, "ok"));
        }
    }
}
