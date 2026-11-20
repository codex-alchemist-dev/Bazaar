namespace Bazaar.Server.DevSandbox.Tests;

public class ProductionIsolationGuardTests
{
    private static readonly string[] _allowedHosts = ["api.example.test"];

    private static SandboxRunSpec ValidSpec()
    {
        return new SandboxRunSpec(
            RunId: "run-1",
            ModuleId: "sample.module",
            ModuleVersion: "1.2.3",
            Engine: SandboxEngineKind.Wasm,
            Grants: new SandboxPermissionGrant[] { new("net.egress", "api.example.test") },
            EgressAllowlist: _allowedHosts,
            Seed: 7UL,
            TimeoutSeconds: 300,
            MemoryLimitMb: 512,
            TargetCompanyId: null);
    }

    [Fact]
    public void A_valid_spec_is_accepted()
    {
        Assert.Null(ProductionIsolationGuard.ValidateSpec(ValidSpec()));
    }

    [Fact]
    public void A_valid_spec_with_a_sandbox_target_and_empty_allowlist_is_accepted()
    {
        SandboxRunSpec spec = ValidSpec() with { TargetCompanyId = "sandbox-run-1", EgressAllowlist = Array.Empty<string>() };

        Assert.Null(ProductionIsolationGuard.ValidateSpec(spec));
    }

    [Fact]
    public void A_null_spec_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Run-1")]
    [InlineData("run_1")]
    [InlineData("run 1")]
    [InlineData("run/1")]
    [InlineData("run.1")]
    [InlineData("-run")]
    [InlineData("run-")]
    public void A_run_id_with_bad_characters_or_edges_is_refused(string runId)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateRunId(runId));
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { RunId = runId }));
    }

    [Fact]
    public void A_null_run_id_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateRunId(null));
    }

    [Fact]
    public void A_run_id_longer_than_64_characters_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateRunId(new string('a', 65)));
    }

    [Fact]
    public void A_run_id_whose_schema_name_would_exceed_63_characters_is_refused()
    {
        // "sandbox_" is 8 characters, so the longest schema-safe run id is 55 characters.
        Assert.Null(ProductionIsolationGuard.ValidateRunId(new string('a', 55)));
        Assert.NotNull(ProductionIsolationGuard.ValidateRunId(new string('a', 56)));
        Assert.NotNull(ProductionIsolationGuard.ValidateRunId(new string('a', 64)));
    }

    [Fact]
    public void An_empty_module_id_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { ModuleId = " " }));
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0+")]
    [InlineData("1.0.0-01")]
    public void A_module_version_that_is_not_semver_is_refused(string version)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { ModuleVersion = version }));
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.2.3+build.5")]
    public void A_module_version_that_is_semver_is_accepted(string version)
    {
        Assert.Null(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { ModuleVersion = version }));
    }

    [Fact]
    public void Null_grants_are_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { Grants = null! }));
    }

    [Fact]
    public void A_grant_with_an_empty_class_is_refused()
    {
        var grants = new SandboxPermissionGrant[] { new("", "*") };

        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { Grants = grants }));
    }

    [Fact]
    public void A_null_grant_entry_is_refused()
    {
        var grants = new SandboxPermissionGrant?[] { null };

        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { Grants = grants! }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3601)]
    [InlineData(-1)]
    public void A_timeout_outside_1_to_3600_is_refused(int seconds)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { TimeoutSeconds = seconds }));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3600)]
    public void A_timeout_at_the_bounds_is_accepted(int seconds)
    {
        Assert.Null(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { TimeoutSeconds = seconds }));
    }

    [Theory]
    [InlineData(15)]
    [InlineData(8193)]
    [InlineData(0)]
    public void A_memory_limit_outside_16_to_8192_is_refused(int megabytes)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { MemoryLimitMb = megabytes }));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(8192)]
    public void A_memory_limit_at_the_bounds_is_accepted(int megabytes)
    {
        Assert.Null(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { MemoryLimitMb = megabytes }));
    }

    [Theory]
    [InlineData("prod-company")]
    [InlineData("")]
    [InlineData("sandbox_run_1")]
    public void A_target_company_that_is_not_a_sandbox_company_is_refused(string target)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { TargetCompanyId = target }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("api.example.test/path")]
    [InlineData("api.example.test:443")]
    [InlineData("api example.test")]
    [InlineData("*")]
    [InlineData("*.example.test")]
    [InlineData("api.*.test")]
    [InlineData("example.com")]
    [InlineData("api.example.test.com")]
    [InlineData("git.example.org")]
    [InlineData(".test")]
    [InlineData("test")]
    public void An_egress_host_that_is_not_a_test_endpoint_is_refused(string host)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { EgressAllowlist = new[] { host } }));
    }

    [Theory]
    [InlineData("api.example.test")]
    [InlineData("x.test")]
    [InlineData("localhost")]
    public void A_test_egress_host_is_accepted(string host)
    {
        Assert.Null(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { EgressAllowlist = new[] { host } }));
    }

    [Fact]
    public void A_null_egress_allowlist_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { EgressAllowlist = null! }));
    }

    [Fact]
    public void An_unknown_engine_kind_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(ValidSpec() with { Engine = (SandboxEngineKind)99 }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("sandbox-run-1")]
    [InlineData("sandbox_run_1")]
    public void ValidateTarget_accepts_no_target_and_sandbox_names(string? target)
    {
        Assert.Null(ProductionIsolationGuard.ValidateTarget(target));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("production_db")]
    [InlineData("sandbox_run_1; DROP SCHEMA public")]
    [InlineData("Sandbox_run_1")]
    public void ValidateTarget_refuses_production_and_unsafe_names(string target)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateTarget(target));
    }

    [Theory]
    [InlineData("sandbox_x", true)]
    [InlineData("sandbox_", true)]
    [InlineData("public", false)]
    [InlineData("Sandbox_x", false)]
    [InlineData("sandbox-x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSandboxSchema_is_true_only_for_sandbox_prefixed_names(string? name, bool expected)
    {
        Assert.Equal(expected, ProductionIsolationGuard.IsSandboxSchema(name));
    }
}
