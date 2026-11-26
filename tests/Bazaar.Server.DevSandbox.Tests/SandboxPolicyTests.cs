namespace Bazaar.Server.DevSandbox.Tests;

public class SandboxPolicyTests
{
    private static SandboxRunSpec Spec(
        IReadOnlyList<SandboxPermissionGrant>? grants = null,
        IReadOnlyList<string>? allowlist = null) =>
        new(
            RunId: "run-1",
            ModuleId: "mod.test",
            ModuleVersion: "0.1.0",
            Engine: SandboxEngineKind.Wasm,
            Grants: grants ?? [],
            EgressAllowlist: allowlist ?? [],
            Seed: 42UL);

    private static SandboxAction Act(SandboxActionKind kind, string cls, string target) =>
        new(kind, cls, target);

    [Fact]
    public void Constructor_NullSpec_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new SandboxPolicy(null!));

    [Fact]
    public void Decide_NullAction_Throws()
    {
        var policy = new SandboxPolicy(Spec());
        Assert.Throws<ArgumentNullException>(() => policy.Decide(null!, out _));
    }

    [Fact]
    public void Syscall_WithEmptyClass_IsAllowedWithoutGrant()
    {
        var policy = new SandboxPolicy(Spec());

        var decision = policy.Decide(Act(SandboxActionKind.Syscall, "", "clock_gettime"), out var reason);

        Assert.Equal(SandboxDecision.Allowed, decision);
        Assert.Equal("no-permission-needed", reason);
    }

    [Fact]
    public void Syscall_WithClass_RequiresGrant()
    {
        var policy = new SandboxPolicy(Spec());

        var denied = policy.Decide(Act(SandboxActionKind.Syscall, "proc.spawn", "git"), out var reason);
        Assert.Equal(SandboxDecision.Denied, denied);
        Assert.Equal("no-grant", reason);

        var granted = new SandboxPolicy(Spec([new SandboxPermissionGrant("proc.spawn", "*")]))
            .Decide(Act(SandboxActionKind.Syscall, "proc.spawn", "git"), out var grantReason);
        Assert.Equal(SandboxDecision.Allowed, granted);
        Assert.Equal("granted", grantReason);
    }

    [Fact]
    public void HostCall_WithoutGrant_IsDenied()
    {
        var policy = new SandboxPolicy(Spec());

        var decision = policy.Decide(Act(SandboxActionKind.HostCall, "vault.read", "page-1"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void HostCall_ExactGrant_IsAllowed()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("vault.read", "page-1")]));

        var decision = policy.Decide(Act(SandboxActionKind.HostCall, "vault.read", "page-1"), out var reason);

        Assert.Equal(SandboxDecision.Allowed, decision);
        Assert.Equal("granted", reason);
    }

    [Fact]
    public void Grant_ClassMustMatch()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("vault.read", "*")]));

        var decision = policy.Decide(Act(SandboxActionKind.FileAccess, "vault.write", "page-1"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Grant_ClassIsCaseSensitive()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("vault.read", "*")]));

        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.FileAccess, "Vault.Read", "x"), out _));
    }

    [Fact]
    public void Grant_WildcardTarget_MatchesAnyTarget()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("secret.read", "*")]));

        var decision = policy.Decide(Act(SandboxActionKind.SecretRead, "secret.read", "anything/at/all"), out var reason);

        Assert.Equal(SandboxDecision.Allowed, decision);
        Assert.Equal("granted", reason);
    }

    [Fact]
    public void Grant_PrefixTarget_MatchesTargetsStartingWithPrefix()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("file.read", "/workspace/*")]));

        Assert.Equal(SandboxDecision.Allowed, policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/workspace/src/a.cs"), out _));
        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/workspace/"), out _));
    }

    [Fact]
    public void Grant_PrefixTarget_DoesNotMatchOutsidePrefix()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("file.read", "/workspace/*")]));

        var decision = policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/etc/passwd"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Grant_PrefixTarget_IsOrdinalCaseSensitive()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("file.read", "/workspace/*")]));

        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/Workspace/a"), out _));
    }

    [Fact]
    public void Grant_TargetWithoutTrailingSlashStar_CoversNothing()
    {
        // Only "/*" is prefix style. A grant ending in a plain "*" is not a scope, and a target holding "*" is never clean.
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("file.read", "/work*")]));

        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/workspace"), out _));
        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.FileAccess, "file.read", "/work*"), out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Fact]
    public void Grant_ExactTarget_IsOrdinal()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("secret.read", "key-a")]));

        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.SecretRead, "secret.read", "KEY-A"), out _));
        Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(SandboxActionKind.SecretRead, "secret.read", "key-a2"), out _));
    }

    [Fact]
    public void EmptyClass_OnNonSyscall_IsDeniedEvenWithEmptyClassGrant()
    {
        var policy = new SandboxPolicy(Spec([new SandboxPermissionGrant("", "*")]));

        var decision = policy.Decide(Act(SandboxActionKind.HostCall, "", "call"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void PermissionCheck_WithoutGrant_IsDenied()
    {
        var policy = new SandboxPolicy(Spec());

        var decision = policy.Decide(Act(SandboxActionKind.PermissionCheck, "admin.grant", "owner"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Network_HostOnAllowlistWithGrant_IsAllowed()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "svc.example.test")],
            ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Allowed, decision);
        Assert.Equal("granted", reason);
    }

    [Fact]
    public void Network_HostNotOnAllowlist_IsDeniedAsEgress()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "*")],
            ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "evil.example"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("egress-not-allowed", reason);
    }

    [Fact]
    public void Network_HostNotOnAllowlist_IsDeniedAsEgressEvenWithoutGrant()
    {
        var policy = new SandboxPolicy(Spec());

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "evil.example"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("egress-not-allowed", reason);
    }

    [Fact]
    public void Network_HostOnAllowlistWithoutGrant_IsDeniedAsNoGrant()
    {
        var policy = new SandboxPolicy(Spec(allowlist: ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Network_GrantForOtherHost_IsDeniedAsNoGrant()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "other.example")],
            ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Network_WildcardGrant_MatchesAllowlistedHost()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "*")],
            ["svc.example.test"]));

        Assert.Equal(SandboxDecision.Allowed, policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out _));
    }

    [Fact]
    public void Network_PrefixGrant_IsNotApplied()
    {
        // Prefix style applies to non-network kinds only; a network grant must be "*" or the exact host.
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "test.*")],
            ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Network_AllowlistIsCaseInsensitive()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "Test.Example")],
            ["TEST.example"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "test.EXAMPLE"), out var reason);

        Assert.Equal(SandboxDecision.Allowed, decision);
        Assert.Equal("granted", reason);
    }

    [Fact]
    public void Network_EmptyAllowlist_BlocksEverything()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("net.egress", "*")],
            []));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("egress-not-allowed", reason);
    }

    [Fact]
    public void Network_EmptyClass_IsDeniedAsNoGrantWhenHostAllowlisted()
    {
        var policy = new SandboxPolicy(Spec(
            [new SandboxPermissionGrant("", "*")],
            ["svc.example.test"]));

        var decision = policy.Decide(Act(SandboxActionKind.NetworkAttempt, "", "svc.example.test"), out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Decide_NeverAllowsByDefault()
    {
        var policy = new SandboxPolicy(Spec());

        foreach (var kind in Enum.GetValues<SandboxActionKind>())
        {
            if (kind == SandboxActionKind.Syscall)
            {
                continue;
            }

            Assert.Equal(SandboxDecision.Denied, policy.Decide(Act(kind, "some.class", "target"), out _));
        }
    }
}
