namespace Bazaar.Server.DevSandbox.Tests.Adversarial;

public class GuardAdversarialTests
{
    [Theory]
    [InlineData("svc.example.test\0")]
    [InlineData("svc\0.test")]
    [InlineData("svc.example.test.")]
    [InlineData("SVC.example.test")]
    [InlineData("svc.examplé.test")]
    [InlineData("sѕvc.test")]
    [InlineData("svc..test")]
    [InlineData("-svc.test")]
    [InlineData("svc​.test")]
    [InlineData("svc.test。")]
    [InlineData("localhost.")]
    [InlineData("LOCALHOST")]
    public void Egress_entries_that_are_not_plain_lower_case_dns_names_are_refused(string host)
    {
        var spec = Specs.Valid() with { EgressAllowlist = [host] };

        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(spec));
    }

    [Theory]
    [InlineData("sandbox-x ")]
    [InlineData("sandbox-x\n")]
    [InlineData("sandbox-x\0")]
    [InlineData("sandbox-")]
    [InlineData("sandbox-а")]
    [InlineData("sandbox-x/../y")]
    public void Unclean_sandbox_company_ids_are_refused(string id)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(Specs.Valid(targetCompany: id)));
    }

    [Fact]
    public void A_grant_without_a_target_is_refused()
    {
        var spec = Specs.Valid() with { Grants = [new SandboxPermissionGrant("fs.read", null!)] };

        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(spec));
    }

    [Fact]
    public void Role_and_schema_names_equal_to_production_ones_are_not_sandbox_names()
    {
        foreach (var name in new[] { "public", "postgres", "bazaar", "bazaar_app", "pg_catalog", "sandbox", "Sandbox_x", " sandbox_x", "sandbox-x" })
        {
            Assert.False(ProductionIsolationGuard.IsSandboxSchema(name));
        }

        Assert.NotNull(ProductionIsolationGuard.ValidateTarget("public"));
        Assert.NotNull(ProductionIsolationGuard.ValidateTarget("postgres"));
        Assert.NotNull(ProductionIsolationGuard.ValidateTarget("sandbox_x\n"));
        Assert.NotNull(ProductionIsolationGuard.ValidateTarget("sandbox_а"));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("mod..x")]
    [InlineData("mod\n")]
    [InlineData("mod\0")]
    [InlineData("-mod")]
    [InlineData(".mod")]
    [InlineData("m\u043Ed")]
    [InlineData("mod name")]
    public void A_module_id_that_could_name_a_path_is_refused(string moduleId)
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(Specs.Valid() with { ModuleId = moduleId }));
    }

    [Fact]
    public void A_module_id_longer_than_128_characters_is_refused()
    {
        Assert.NotNull(ProductionIsolationGuard.ValidateSpec(Specs.Valid() with { ModuleId = new string('a', 129) }));
        Assert.Null(ProductionIsolationGuard.ValidateSpec(Specs.Valid() with { ModuleId = new string('a', 128) }));
    }
}
