using Bazaar.Install.Trust;

namespace Bazaar.Server.DevSandbox.Tests;

public class VerifiedTagSandboxCheckTests
{
    private static SandboxRunResult R(SandboxVerdict v, string runId = "run-1", int violations = 0)
    {
        var ev = Enumerable.Range(0, violations)
            .Select(i => new SandboxAuditEvent(i, DateTimeOffset.UnixEpoch, new SandboxAction(SandboxActionKind.HostCall, "x", "y"), SandboxDecision.Denied, "no-grant", "0", "1"))
            .ToArray();
        return new SandboxRunResult(runId, v, string.Empty, ev, violations, "0");
    }

    [Theory]
    [InlineData(SandboxVerdict.Clean, CheckState.Passed)]
    [InlineData(SandboxVerdict.UndeclaredAccess, CheckState.Failed)]
    [InlineData(SandboxVerdict.CanaryTriggered, CheckState.Failed)]
    [InlineData(SandboxVerdict.ReplayMismatch, CheckState.Failed)]
    [InlineData(SandboxVerdict.Refused, CheckState.Pending)]
    [InlineData(SandboxVerdict.TimedOut, CheckState.Pending)]
    [InlineData(SandboxVerdict.Error, CheckState.Pending)]
    public void Verdict_maps_to_check_state(SandboxVerdict verdict, CheckState expected) =>
        Assert.Equal(expected, VerifiedTagSandboxCheck.Evaluate(Specs.Valid(), R(verdict), Specs.Valid().ModuleVersion));

    [Fact]
    public void Missing_result_is_pending() =>
        Assert.Equal(CheckState.Pending, VerifiedTagSandboxCheck.Evaluate(Specs.Valid(), null, "1.0.0"));

    [Fact]
    public void Result_for_another_version_never_passes() =>
        Assert.Equal(CheckState.Pending, VerifiedTagSandboxCheck.Evaluate(Specs.Valid(), R(SandboxVerdict.Clean), "2.0.0"));

    [Fact]
    public void Result_for_another_run_never_passes() =>
        Assert.Equal(CheckState.Pending, VerifiedTagSandboxCheck.Evaluate(Specs.Valid(), R(SandboxVerdict.Clean, "other"), "1.0.0"));

    [Fact]
    public void Clean_with_violations_is_not_passed() =>
        Assert.NotEqual(CheckState.Passed, VerifiedTagSandboxCheck.Evaluate(Specs.Valid(), R(SandboxVerdict.Clean, violations: 1), "1.0.0"));
}
