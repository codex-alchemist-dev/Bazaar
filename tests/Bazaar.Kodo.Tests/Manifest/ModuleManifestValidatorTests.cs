using Bazaar.Kodo.Manifest;

namespace Bazaar.Kodo.Tests.Manifest;

public sealed class ModuleManifestValidatorTests
{
    private static IReadOnlyList<ModuleManifestProblem> Check(ModuleManifest manifest) =>
        ModuleManifestValidator.Validate(manifest, ManifestTestData.Catalogue());

    private static void AssertOnly(ModuleManifest manifest, string cause, string field)
    {
        ModuleManifestProblem problem = Assert.Single(Check(manifest));
        Assert.Equal(cause, problem.Cause);
        Assert.Equal(field, problem.Field);
    }

    [Fact]
    public void TheSketchManifestIsValid()
    {
        Assert.Empty(Check(ManifestTestData.Valid()));
    }

    [Fact]
    public void NoHealthBlockMeansItDoesNotLoad()
    {
        AssertOnly(ManifestTestData.Valid() with { Health = null }, ModuleManifestCauses.HealthMissing, "health");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("GC")]
    [InlineData("1gc")]
    [InlineData("g c")]
    public void BadIdsAreRefused(string? id)
    {
        AssertOnly(ManifestTestData.Valid() with { Id = id }, ModuleManifestCauses.IdInvalid, "id");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("two\nlines")]
    public void BadNamesAreRefused(string? name)
    {
        AssertOnly(ManifestTestData.Valid() with { Name = name }, ModuleManifestCauses.NameInvalid, "name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("second-party")]
    [InlineData("First-Party")]
    public void BadKindsAreRefused(string? kind)
    {
        AssertOnly(ManifestTestData.Valid() with { Kind = kind }, ModuleManifestCauses.KindInvalid, "kind");
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("v1.0.0")]
    [InlineData("")]
    public void BadVersionsAreRefused(string version)
    {
        AssertOnly(ManifestTestData.Valid() with { Version = version }, ModuleManifestCauses.VersionInvalid, "version");
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("1.2.3-beta.1+abc")]
    public void SemVerIsAccepted(string version)
    {
        Assert.Empty(Check(ManifestTestData.Valid() with { Version = version }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    public void OnlyContractOneIsSupported(int? contract)
    {
        AssertOnly(ManifestTestData.Valid() with { KodoContract = contract }, ModuleManifestCauses.ContractUnsupported, "kodo.contract");
    }

    [Fact]
    public void DependencyRules()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        AssertOnly(valid with { Dependencies = new(["audit", "audit"], []) }, ModuleManifestCauses.DependencyDuplicate, "dependencies.hard");
        AssertOnly(valid with { Dependencies = new(["gc"], []) }, ModuleManifestCauses.DependencyInvalid, "dependencies.hard");
        AssertOnly(valid with { Dependencies = new([], ["Bad Id"]) }, ModuleManifestCauses.DependencyInvalid, "dependencies.soft");
        AssertOnly(valid with { Dependencies = new(["audit"], ["audit"]) }, ModuleManifestCauses.DependencyConflict, "dependencies");
        Assert.Empty(Check(valid with { Dependencies = null }));
    }

    [Fact]
    public void HealthBudgetsAreBounded()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        ModuleManifest zero = valid with { Health = valid.Health! with { SelfTest = new(0, 30000, "daily") } };
        ModuleManifest tooLong = valid with { Health = valid.Health! with { SelfTest = new(2000, 60001, "daily") } };
        ModuleManifest inverted = valid with { Health = valid.Health! with { SelfTest = new(5000, 2000, "daily") } };
        ModuleManifest edge = valid with { Health = valid.Health! with { SelfTest = new(60000, 60000, "off") } };

        AssertOnly(zero, ModuleManifestCauses.HealthInvalid, "health.selfTest.quickBudgetMs");
        AssertOnly(tooLong, ModuleManifestCauses.HealthInvalid, "health.selfTest.fullBudgetMs");
        AssertOnly(inverted, ModuleManifestCauses.HealthInvalid, "health.selfTest.fullBudgetMs");
        Assert.Empty(Check(edge));
    }

    [Fact]
    public void HealthFieldsAreChecked()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        AssertOnly(valid with { Health = valid.Health! with { Liveness = "magic" } }, ModuleManifestCauses.HealthInvalid, "health.liveness");
        AssertOnly(valid with { Health = valid.Health! with { Readiness = null } }, ModuleManifestCauses.HealthInvalid, "health.readiness");
        AssertOnly(valid with { Health = valid.Health! with { SelfTest = null } }, ModuleManifestCauses.HealthInvalid, "health.selfTest");
        AssertOnly(valid with { Health = valid.Health! with { SelfTest = new(1, 2, "yearly") } }, ModuleManifestCauses.HealthInvalid, "health.selfTest.scheduled");
    }

    [Fact]
    public void ProbesMustExistInTheCatalogue()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        AssertOnly(valid with { Probes = ["gc.not_in_catalogue"] }, ModuleManifestCauses.ProbeUnknown, "probes");
        AssertOnly(valid with { Probes = ["gc.notify_listener", "gc.notify_listener"] }, ModuleManifestCauses.ProbeDuplicate, "probes");
        AssertOnly(valid with { Probes = ["GC Listener"] }, ModuleManifestCauses.ProbeInvalid, "probes");
        Assert.Empty(Check(valid with { Probes = ["gc.shard.main"] }));
        AssertOnly(valid with { Probes = ["gc.shard."] }, ModuleManifestCauses.ProbeInvalid, "probes");
    }

    [Fact]
    public void ProbeIdsWithInstanceLikeHexAreInvalid()
    {
        AssertOnly(ManifestTestData.Valid() with { Probes = ["gc.0123456789abcdef0"] }, ModuleManifestCauses.ProbeInvalid, "probes");
    }

    [Theory]
    [InlineData("runbooks/gc-notify-restart.json")]
    [InlineData("runbooks/sub/restart_2.json")]
    public void ConfinedRunbookPathsAreAccepted(string path)
    {
        Assert.True(ModuleManifestValidator.IsConfinedRunbookPath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/runbooks/a.json")]
    [InlineData("runbooks/../secrets.json")]
    [InlineData("runbooks/a/../../x.json")]
    [InlineData("runbooks/./a.json")]
    [InlineData("runbooks//a.json")]
    [InlineData("runbooks\\a.json")]
    [InlineData("runbooks/a\\..\\b.json")]
    [InlineData("C:/runbooks/a.json")]
    [InlineData("runbooks/c:evil.json")]
    [InlineData("runbooks/a.md")]
    [InlineData("runbooks/.json")]
    [InlineData("runbooks/.hidden.json")]
    [InlineData("runbooks/a b.json")]
    [InlineData("runbooks/con.json")]
    [InlineData("runbooks/NUL.json")]
    [InlineData("other/a.json")]
    [InlineData("runbooks/a.json/")]
    [InlineData("runbooks/a.json\0")]
    [InlineData("runbooks/%2e%2e/a.json")]
    public void EscapingOrOddRunbookPathsAreRefused(string? path)
    {
        Assert.False(ModuleManifestValidator.IsConfinedRunbookPath(path));
    }

    [Fact]
    public void RunbookFindingsAreReported()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        AssertOnly(valid with { Runbooks = ["runbooks/../x.json"] }, ModuleManifestCauses.RunbookPathInvalid, "runbooks");
        AssertOnly(valid with { Runbooks = ["runbooks/a.json", "runbooks/A.json"] }, ModuleManifestCauses.RunbookDuplicate, "runbooks");
    }

    [Theory]
    [InlineData("db.read:gc")]
    [InlineData("kodo.observe")]
    [InlineData("db.write:gc.shard")]
    public void WellFormedPermissionsAreAccepted(string permission)
    {
        Assert.Empty(Check(ManifestTestData.Valid() with { Permissions = [permission] }));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("db.*")]
    [InlineData("db.read:*")]
    [InlineData("DB.read")]
    [InlineData("observe")]
    [InlineData("db.read:")]
    [InlineData("db.read gc")]
    [InlineData("")]
    public void MalformedPermissionsAreRefused(string permission)
    {
        AssertOnly(ManifestTestData.Valid() with { Permissions = [permission] }, ModuleManifestCauses.PermissionInvalid, "permissions");
    }

    [Fact]
    public void DuplicatePermissionsAreRefused()
    {
        AssertOnly(ManifestTestData.Valid() with { Permissions = ["kodo.observe", "kodo.observe"] }, ModuleManifestCauses.PermissionDuplicate, "permissions");
    }

    [Fact]
    public void ListSizeCapsAreEnforced()
    {
        ModuleManifest valid = ManifestTestData.Valid();
        string[] manyPermissions = [.. Enumerable.Range(0, ModuleManifestValidator.MaxPermissions + 1).Select(i => $"db.read:t{i}")];

        AssertOnly(valid with { Permissions = manyPermissions }, ModuleManifestCauses.TooManyEntries, "permissions");
    }

    [Fact]
    public void NullEntriesAreInvalidNotCrashes()
    {
        ModuleManifest valid = ManifestTestData.Valid();

        Assert.Contains(Check(valid with { Permissions = [null!] }), p => p.Cause == ModuleManifestCauses.PermissionInvalid);
        Assert.Contains(Check(valid with { Probes = [null!] }), p => p.Cause == ModuleManifestCauses.ProbeInvalid);
        Assert.Contains(Check(valid with { Runbooks = [null!] }), p => p.Cause == ModuleManifestCauses.RunbookPathInvalid);
    }

    [Fact]
    public void ProbeCatalogueMatchesExactAndFamilies()
    {
        ModuleProbeCatalogue catalogue = ManifestTestData.Catalogue();

        Assert.True(catalogue.Contains("gc.event_gaps"));
        Assert.True(catalogue.Contains("gc.shard.7"));
        Assert.False(catalogue.Contains("gc.shard."));
        Assert.False(catalogue.Contains("gc.event"));
    }
}
