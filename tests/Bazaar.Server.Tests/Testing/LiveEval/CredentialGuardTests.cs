using Bazaar.Testing.LiveEval;

namespace Bazaar.Server.Tests.Testing.LiveEval;

public class CredentialGuardTests
{
    // Fixture keys are built from obviously fake bodies at run time, so no key-shaped literal lives in the repo.
    private const string _fakeBody = "SentinelFixture0123456789";
    private static readonly string _fakeApiKey = "sk-" + _fakeBody;

    private static readonly string _repoRoot = Path.Combine(Path.GetTempPath(), "bazaar-repo");

    private static TaskSet SetWithPrompt(string prompt) =>
        new("set", 1, [new TaskCase("c1", prompt, [new TaskCheck(TaskCheckKind.Build)])]);

    [Fact]
    public void Key_in_a_prompt_is_flagged_with_its_field_path_and_no_value_is_echoed()
    {
        var set = SetWithPrompt($"Use this key to push: {_fakeApiKey}");

        var findings = LiveEvalCredentialGuard.Scan(set, new Dictionary<string, string?>(), _repoRoot, Path.GetTempPath());

        var finding = Assert.Single(findings);
        Assert.Equal("cases[0].prompt", finding.FieldPath);
        Assert.Equal("task-set-key-prefix:api-key-prefix-sk", finding.Rule);
        Assert.DoesNotContain(_fakeBody, finding.ToString());
        Assert.DoesNotContain(_fakeBody, string.Join(" ", findings));
    }

    [Fact]
    public void Each_key_family_is_recognised()
    {
        var samples = new Dictionary<string, string>
        {
            ["ghp"] = "ghp_" + new string('0', 36),
            ["xox"] = "xoxb-" + new string('0', 12),
            ["akia"] = "AKIA" + new string('0', 16),
        };

        foreach (var (family, sample) in samples)
        {
            var findings = LiveEvalCredentialGuard.Scan(
                SetWithPrompt($"token {sample} here"), new Dictionary<string, string?>(), _repoRoot, Path.GetTempPath());

            Assert.Single(findings);
            Assert.Contains(family, findings[0].Rule);
        }
    }

    [Fact]
    public void Ordinary_prose_with_key_like_fragments_is_not_flagged()
    {
        var set = SetWithPrompt("Review the risk-averse retry policy and the ask-me-anything thread, sk-ip.");

        var findings = LiveEvalCredentialGuard.Scan(set, new Dictionary<string, string?>(), _repoRoot, Path.GetTempPath());

        Assert.Empty(findings);
    }

    [Fact]
    public void Key_in_the_environment_is_refused_when_the_work_dir_is_under_the_repo()
    {
        var set = SetWithPrompt("Fix the bug.");
        var env = new Dictionary<string, string?> { ["GATEWAY_KEY"] = _fakeApiKey, ["HOME_DIR"] = "/home/x" };

        var findings = LiveEvalCredentialGuard.Scan(set, env, _repoRoot, Path.Combine(_repoRoot, "scratch"));

        var finding = Assert.Single(findings);
        Assert.Equal("env:GATEWAY_KEY", finding.FieldPath);
        Assert.Equal(LiveEvalCredentialGuard.EnvironmentRule, finding.Rule);
        Assert.DoesNotContain(_fakeBody, finding.ToString());
    }

    [Fact]
    public void Key_in_the_environment_is_allowed_when_the_work_dir_is_outside_the_repo()
    {
        var set = SetWithPrompt("Fix the bug.");
        var env = new Dictionary<string, string?> { ["GATEWAY_KEY"] = _fakeApiKey };
        var outside = Path.Combine(Path.GetTempPath(), "bazaar-repo-sibling");

        var findings = LiveEvalCredentialGuard.Scan(set, env, _repoRoot, outside);

        Assert.Empty(findings);
    }

    [Fact]
    public void A_clean_task_set_and_environment_produce_no_findings()
    {
        var findings = LiveEvalCredentialGuard.Scan(
            SetWithPrompt("Add a unit test for the clock."),
            new Dictionary<string, string?> { ["PATH"] = "/usr/bin" },
            _repoRoot,
            _repoRoot);

        Assert.Empty(findings);
    }
}
