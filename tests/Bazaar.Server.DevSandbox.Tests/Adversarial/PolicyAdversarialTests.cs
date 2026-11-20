namespace Bazaar.Server.DevSandbox.Tests.Adversarial;

public class PolicyAdversarialTests
{
    private static SandboxPolicy Policy(string cls, string target, params string[] allowlist) =>
        new(new SandboxRunSpec("run-1", "mod.test", "0.1.0", SandboxEngineKind.Wasm, [new SandboxPermissionGrant(cls, target)], allowlist, 1UL));

    private static SandboxDecision Decide(SandboxPolicy p, SandboxActionKind kind, string cls, string target, out string reason) =>
        p.Decide(new SandboxAction(kind, cls, target), out reason);

    [Theory]
    [InlineData("/data/mod/../../etc/passwd")]
    [InlineData("/data/mod/..")]
    [InlineData("/data/mod/../x")]
    [InlineData("/data/mod/./x")]
    [InlineData("/data/mod//x")]
    [InlineData("/data//mod/x")]
    [InlineData("/data/mod/x\\..\\..\\y")]
    [InlineData("/data/mod/x\0")]
    [InlineData("/data/mod/x\ny")]
    [InlineData("/data/mod/‮x")]
    [InlineData("/data/mod/...")]
    [InlineData("/data/mod/.. ")]
    [InlineData("/data/mod/ ")]
    public void Prefix_grant_refuses_unclean_targets(string target)
    {
        var p = Policy("fs.read", "/data/mod/*");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", target, out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Theory]
    [InlineData("/data/mod/a")]
    [InlineData("/data/mod/a/b.txt")]
    [InlineData("/data/mod/.hidden")]
    [InlineData("/data/mod/a..b")]
    [InlineData("/data/mod/")]
    public void Prefix_grant_allows_clean_targets_below_it(string target)
    {
        var p = Policy("fs.read", "/data/mod/*");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.FileAccess, "fs.read", target, out _));
    }

    [Theory]
    [InlineData("/data/module/x")]
    [InlineData("/data/mod")]
    [InlineData("/data/modx")]
    [InlineData("/Data/mod/x")]
    [InlineData("data/mod/x")]
    public void Prefix_grant_matches_whole_segments_only(string target)
    {
        var p = Policy("fs.read", "/data/mod/*");

        var decision = Decide(p, SandboxActionKind.FileAccess, "fs.read", target, out var reason);

        Assert.Equal(SandboxDecision.Denied, decision);
        Assert.Equal("no-grant", reason);
    }

    [Theory]
    [InlineData("/data/mod/../*")]
    [InlineData("/data/mod//*")]
    [InlineData("/data/./*")]
    [InlineData("/data/mod\\*")]
    public void An_unclean_grant_prefix_never_matches(string grant)
    {
        var p = Policy("fs.read", grant);

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/mod/x", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/x", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/etc/passwd", out _));
    }

    [Fact]
    public void Root_prefix_grant_allows_clean_absolute_paths_only()
    {
        var p = Policy("fs.read", "/*");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/etc/hostname", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "etc/hostname", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/etc/../x", out _));
    }

    [Fact]
    public void Exact_grant_does_not_match_a_trailing_slash_or_dot_variant()
    {
        var p = Policy("fs.read", "/data/ok.txt");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/ok.txt", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/ok.txt/", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/./ok.txt", out var reason));
        Assert.Equal("invalid-target", reason);
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/ok.txt.", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, "fs.read", "/data/OK.TXT", out _));
    }

    [Theory]
    [InlineData("")]
    public void An_empty_target_is_refused_even_under_a_wildcard_grant(string target)
    {
        var p = Policy("vault.read", "*");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.HostCall, "vault.read", target, out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Fact]
    public void A_null_target_is_refused_not_thrown()
    {
        var p = Policy("vault.read", "*");

        Assert.Equal(SandboxDecision.Denied, p.Decide(new SandboxAction(SandboxActionKind.HostCall, "vault.read", null!), out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Fact]
    public void Bare_star_grant_is_honoured_for_every_class_but_never_for_an_unclean_target()
    {
        // Doc 48 section 1 says "*" means every target of the class and 2.1 does not restrict it by class, so it is honoured.
        var p = Policy("secret.read", "*");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.SecretRead, "secret.read", "any/name", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.SecretRead, "secret.read", "any/../name", out var reason));
        Assert.Equal("invalid-target", reason);
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.SecretRead, "secret.write", "any/name", out _));
    }

    [Theory]
    [InlineData("fs.read ")]
    [InlineData(" fs.read")]
    [InlineData("FS.READ")]
    [InlineData("fs.reaаd")]
    [InlineData("fs.read\0")]
    public void Class_comparison_is_exact(string cls)
    {
        var p = Policy("fs.read", "*");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.FileAccess, cls, "/a", out _));
    }

    [Fact]
    public void Opaque_names_are_matched_by_whole_segments_too()
    {
        var p = Policy("data.scope", "customers/*");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.HostCall, "data.scope", "customers/eu", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.HostCall, "data.scope", "customers-all", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.HostCall, "data.scope", "customers//eu", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.HostCall, "data.scope", "customers/../admin", out _));
    }

    [Theory]
    [InlineData("svc.example.test.")]
    [InlineData("svc.example.test:443")]
    [InlineData("user@svc.example.test")]
    [InlineData("user:pw@svc.example.test")]
    [InlineData(" svc.example.test")]
    [InlineData("svc.example.test ")]
    [InlineData("svc.example.test\t")]
    [InlineData("svc.example.test\0")]
    [InlineData("svc.example.test\n")]
    [InlineData("svc.example.test/")]
    [InlineData("http://svc.example.test")]
    [InlineData("svc..example.test")]
    [InlineData("-svc.example.test")]
    [InlineData("sѕvc.example.test")]
    [InlineData("svc.examplé.test")]
    [InlineData("xn--svc.example.test。")]
    [InlineData("[::1]")]
    [InlineData("127.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("2130706433")]
    [InlineData("017.0.0.1")]
    [InlineData("127.0.0.1.")]
    [InlineData("::1%1")]
    [InlineData("0:0:0:0:0:0:0:1")]
    [InlineData("")]
    public void Network_target_that_is_not_a_plain_dns_name_or_canonical_ip_is_invalid(string target)
    {
        // Even a wildcard grant and an allowlist that "looks" like it names the host cannot make such a target allowed.
        var p = Policy("net.egress", "*", "svc.example.test", "localhost");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", target, out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Fact]
    public void Network_ascii_case_is_folded_and_the_plain_name_is_allowed()
    {
        var p = Policy("net.egress", "svc.example.test", "svc.example.test");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "SVC.Example.TEST", out _));
    }

    [Fact]
    public void Network_punycode_name_matches_only_its_punycode_allowlist_entry()
    {
        var p = Policy("net.egress", "*", "xn--bcher-kva.test");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "xn--bcher-kva.test", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "bücher.test", out var reason));
        Assert.Equal("invalid-target", reason);
    }

    [Fact]
    public void Network_canonical_ip_literal_is_valid_but_still_needs_the_allowlist()
    {
        var p = Policy("net.egress", "*", "svc.example.test");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "127.0.0.1", out var v4));
        Assert.Equal("egress-not-allowed", v4);
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "::1", out var v6));
        Assert.Equal("egress-not-allowed", v6);
    }

    [Fact]
    public void A_non_canonical_allowlist_entry_never_matches_anything()
    {
        var p = Policy("net.egress", "*", "svc.example.test.", "svc.example.test:443");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", out var reason));
        Assert.Equal("egress-not-allowed", reason);
    }

    [Fact]
    public void A_non_canonical_grant_host_never_matches()
    {
        var p = Policy("net.egress", "svc.example.test.", "svc.example.test");

        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", out var reason));
        Assert.Equal("no-grant", reason);
    }

    [Fact]
    public void Bare_star_network_grant_still_requires_the_allowlist()
    {
        var p = Policy("net.egress", "*", "svc.example.test");

        Assert.Equal(SandboxDecision.Allowed, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "svc.example.test", out _));
        Assert.Equal(SandboxDecision.Denied, Decide(p, SandboxActionKind.NetworkAttempt, "net.egress", "other.test", out var reason));
        Assert.Equal("egress-not-allowed", reason);
    }

    [Fact]
    public void Syscall_with_empty_class_stays_allowed_whatever_the_target()
    {
        var p = Policy("fs.read", "*");

        Assert.Equal(SandboxDecision.Allowed, p.Decide(new SandboxAction(SandboxActionKind.Syscall, "", "../x"), out var reason));
        Assert.Equal("no-permission-needed", reason);
    }
}
