using Bazaar.Contracts.Modules;
using Bazaar.Core;
using Bazaar.Server.Modules.Deps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Bazaar.Server.Modules.Tests.Chaos;

/// <summary>
/// Module load chaos (doc 46 section 17.1 and 17.4, doc 42, doc 48): malformed, huge and hostile manifests,
/// dependency graph faults, and a component that fails halfway through registering. Deterministic seeds, no waits.
/// </summary>
public sealed class ModuleManifestChaosTests
{
    private static readonly IReadOnlyList<string> _tags = ["Deps"];

    private static DependencyManifest Dep(string id = "postgresql", string line = "19", string version = "19.0.0") =>
        new(
            id,
            "PostgreSQL",
            _tags,
            [new DependencyLine(line, version, DependencyLineRole.Newest)],
            [],
            "PostgreSQL",
            DependencyLicenceVerdict.Compatible,
            DependencyOrigin.FirstParty);

    private static ModuleManifest Client(string id = "bazaar.gc", string version = "1.0.0") =>
        new(
            id,
            version,
            1,
            [],
            [],
            [],
            [],
            ModuleIsolation.InProcessCompiled,
            new HostRequirements(1, ["windows"], ["x64"], []));

    private static ComponentManifest M(string id, string[]? dependsOn = null, string[]? soft = null) =>
        new(id, "0.1.0", ComponentKind.Feature, dependsOn ?? [], [], soft);

    [Theory]
    [InlineData("postgresql\n")]
    [InlineData("postgresql\r\n")]
    [InlineData("post gresql")]
    [InlineData("POSTGRESQL")]
    [InlineData("postgresql\0")]
    [InlineData("../postgresql")]
    [InlineData("")]
    public void DependencyIdWithControlCharactersOrTrailingNewlineIsRefused(string id)
    {
        Assert.False(DependencyManifestValidator.IsSafeSlug(id));
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(Dep(id))));
    }

    [Fact]
    public void DependencyLineAndVersionWithATrailingNewlineAreRefused()
    {
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(Dep(line: "19\n"))));
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(Dep(version: "19.0.0\n"))));
    }

    [Theory]
    [InlineData("bazaar.gc\n")]
    [InlineData("bazaar.gc\r\n")]
    [InlineData("bazaar.gc\0")]
    [InlineData("Bazaar.gc")]
    public void ClientModuleIdWithAnInjectedNewlineIsRefused(string id)
    {
        Assert.False(ModuleManifestValidator.Validate(Client(id: id)).IsValid);
    }

    [Fact]
    public void ClientModuleVersionWithATrailingNewlineIsRefused()
    {
        Assert.False(ModuleManifestValidator.Validate(Client(version: "1.0.0\n")).IsValid);
    }

    [Fact]
    public void HugeAndHostileDependencyManifestsAreRefusedWithoutThrowing()
    {
        string huge = new('a', 2_000_000);
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(Dep(id: huge))));

        DependencyManifest manyLines = Dep() with
        {
            Lines = [.. Enumerable.Range(0, 50_000).Select(i => new DependencyLine((i % 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture), "1.0.0", DependencyLineRole.Retired))],
        };
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(manyLines)));

        // The same id many times in a catalogue reports once per id, not once per copy.
        IReadOnlyList<DependencyManifestIssue> issues = DependencyManifestValidator.ValidateCatalogue(Enumerable.Repeat(Dep(), 10_000));
        Assert.Single(issues, i => i.Code == DependencyManifestIssueCode.DuplicateModuleId);
    }

    [Fact]
    public void NullFieldsInADependencyManifestAreIssuesNotCrashes()
    {
        // A manifest that came off the wire can hold nulls whatever the declared types say.
        DependencyManifest bad = Dep() with { Tags = null!, Lines = null!, Extensions = null!, DisplayName = null!, LicenceSpdx = null! };
        IReadOnlyList<DependencyManifestIssue> issues = DependencyManifestValidator.Validate(bad);
        Assert.False(DependencyManifestValidator.CanInstall(issues));

        DependencyManifest nullLine = Dep() with { Lines = [new DependencyLine(null!, null!, DependencyLineRole.Newest)] };
        Assert.False(DependencyManifestValidator.CanInstall(DependencyManifestValidator.Validate(nullLine)));
    }

    [Fact]
    public void ClientManifestFuzzOnlyReturnsIssues()
    {
#pragma warning disable CA5394
        Random random = new(31337);
#pragma warning restore CA5394
        string[] pieces = ["", "a", "bazaar.gc", "bazaar.", ".gc", "\n", "\0", "ÿ", "1.0.0", "1.0", "-", "+", new string('x', 70), "wire:gc.post", "storage:local"];
        for (int round = 0; round < 5_000; round++)
        {
            string Pick() => pieces[random.Next(pieces.Length)] + pieces[random.Next(pieces.Length)];
            ModuleManifest manifest = new(
                Pick(),
                Pick(),
                random.Next(-5, 2000),
                [.. Enumerable.Range(0, random.Next(0, 70)).Select(_ => Pick())],
                [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => Pick())],
                [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => Pick())],
                [.. Enumerable.Range(0, random.Next(0, 5)).Select(_ => Pick())],
                (ModuleIsolation)random.Next(-1, 4),
                random.Next(4) == 0 ? null! : new HostRequirements(random.Next(0, 3), [Pick()], [Pick()], [Pick()]));

            ModuleManifestResult result = ModuleManifestValidator.Validate(manifest);
            Assert.Equal(result.IsValid, result.Manifest is not null);
        }
    }

    [Fact]
    public void GraphRefusesEveryMalformedShapeWithGraphExceptions()
    {
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a"), M("a")]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a", ["a"])]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a", ["missing"])]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a", ["b"]), M("b", ["a"])]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a", ["b"]), M("b", ["c"]), M("c", ["a"])]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M("a", soft: ["a"])]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("new\nline")]
    [InlineData("under_score")]
    [InlineData("-lead")]
    public void GraphRefusesIdsThatAreNotKebabCase(string id)
    {
        // The migration namespace maps '-' to '_' one-to-one only for lowercase kebab-case ids (ComponentManifest.MigrationNamespace).
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([M(id)]));
    }

    [Fact]
    public void GraphRefusesNullIdsAndNullListsWithGraphExceptions()
    {
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([new ComponentManifest(null!, "0.1.0", ComponentKind.Feature, [], [])]));
        Assert.Throws<ComponentGraphException>(() => ComponentGraph.Order([new ComponentManifest("a", "0.1.0", ComponentKind.Feature, null!, [])]));
    }

    [Fact]
    public void MutualSoftCyclesDegradeAndNeverFailTheGraph()
    {
        IReadOnlyList<ComponentManifest> ordered = ComponentGraph.Order([M("a", soft: ["b"]), M("b", soft: ["c"]), M("c", soft: ["a"]), M("d", soft: ["ghost"])]);
        Assert.Equal(["a", "b", "c", "d"], ordered.Select(m => m.Id));
    }

    [Fact]
    public void RandomGraphsOrderTopologicallyAndIdenticallyForEveryInputOrder()
    {
#pragma warning disable CA5394
        Random random = new(2024);
#pragma warning restore CA5394
        for (int round = 0; round < 300; round++)
        {
            int count = random.Next(1, 40);
            string[] ids = [.. Enumerable.Range(0, count).Select(i => "m" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))];
            // Hard edges only go from a higher to a lower index, so the graph is acyclic; soft edges go anywhere.
            List<ComponentManifest> manifests = [];
            for (int i = 0; i < count; i++)
            {
                string[] hard = [.. ids.Take(i).Where(_ => random.Next(4) == 0)];
                string[] soft = [.. ids.Where(_ => random.Next(6) == 0)];
                manifests.Add(M(ids[i], hard, [.. soft.Where(s => s != ids[i])]));
            }

            IReadOnlyList<ComponentManifest> first = ComponentGraph.Order(manifests);
            IReadOnlyList<ComponentManifest> second = ComponentGraph.Order([.. manifests.OrderBy(_ => random.Next())]);
            Assert.Equal(first.Select(m => m.Id), second.Select(m => m.Id));

            HashSet<string> seen = [];
            foreach (ComponentManifest manifest in first)
            {
                Assert.All(manifest.DependsOn, d => Assert.Contains(d, seen));
                seen.Add(manifest.Id);
            }
        }
    }

    [Fact]
    public void ADeepDependencyChainOrdersWithoutBlowingUp()
    {
        const int depth = 3_000;
        List<ComponentManifest> chain = [.. Enumerable.Range(0, depth).Select(i =>
            M("c" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), i == 0 ? [] : ["c" + (i - 1).ToString("D5", System.Globalization.CultureInfo.InvariantCulture)]))];
        chain.Reverse();

        IReadOnlyList<ComponentManifest> ordered = ComponentGraph.Order(chain);

        Assert.Equal(depth, ordered.Count);
        Assert.Equal("c00000", ordered[0].Id);
    }

    [Fact]
    public void AComponentThatThrowsWhileRegisteringLeavesTheServiceCollectionUntouched()
    {
        ServiceCollection services = new();
        services.AddSingleton("pre-existing");
        int before = services.Count;
        IConfiguration configuration = new ConfigurationBuilder().Build();

        Component good = new(M("aaa"), s => s.AddSingleton<IGood, Good>());
        Component bad = new(M("bbb"), s =>
        {
            s.AddSingleton<IHalf, Half>();
            throw new InvalidOperationException("initializer failed half way");
        });
        Component later = new(M("ccc"), s => s.AddSingleton<ILater, Later>());

        // The component that fails is named, and nothing it or its predecessors registered stays behind.
        Exception error = Assert.ThrowsAny<Exception>(() => services.AddBazaarComponents(configuration, [good, bad, later]));

        Assert.Equal("bbb", error.Data[BazaarComponentRegistration.FailedComponentIdKey]);
        Assert.Equal(before, services.Count);
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHalf));
    }

    [Fact]
    public void ADisabledComponentThatWouldFailIsNeverInitialised()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Bazaar:Components:bbb:Enabled"] = "false" })
            .Build();
        Component bad = new(M("bbb"), _ => throw new InvalidOperationException("must not run"));

        services.AddBazaarComponents(configuration, [new Component(M("aaa"), s => s.AddSingleton<IGood, Good>()), bad]);

        Assert.Contains(services, d => d.ServiceType == typeof(IGood));
    }

    [Fact]
    public void ARepeatedStartAfterAFailureRegistersCleanly()
    {
        // "Throws only on the second call": the first start fails, the retry on the same collection must not see leftovers.
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();
        int calls = 0;
        Component flaky = new(M("aaa"), s =>
        {
            s.AddSingleton<IHalf, Half>();
            if (++calls == 1)
            {
                throw new InvalidOperationException("first call fails");
            }
        });

        Assert.ThrowsAny<Exception>(() => services.AddBazaarComponents(configuration, [flaky]));
        services.AddBazaarComponents(configuration, [flaky]);

        Assert.Single(services, d => d.ServiceType == typeof(IHalf));
    }

    private interface IGood;

    private sealed class Good : IGood;

    private interface IHalf;

    private sealed class Half : IHalf;

    private interface ILater;

    private sealed class Later : ILater;

    private sealed class Component(ComponentManifest manifest, Action<IServiceCollection> register) : IBazaarComponent
    {
        public ComponentManifest Manifest { get; } = manifest;

        public void AddServices(IServiceCollection services, IConfiguration configuration) => register(services);
    }
}
