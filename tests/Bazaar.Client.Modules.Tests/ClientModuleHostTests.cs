using Bazaar.Contracts.Modules;

namespace Bazaar.Client.Modules.Tests;

public class ClientModuleHostTests
{
    private static readonly HostFacts _host = new("linux", "x64", new HashSet<string>(StringComparer.Ordinal));

    private static ModuleManifest Manifest(string id, string[]? paneKinds = null, string[]? commands = null, string[]? os = null) => new(
        Id: id,
        Version: "0.1.0",
        ContractVersion: 1,
        PaneKinds: paneKinds ?? [id],
        Commands: commands ?? [$"{id}.open"],
        SettingKeys: [],
        Capabilities: ["ui:pane"],
        Isolation: ModuleIsolation.InProcessCompiled,
        Host: new HostRequirements(1, os ?? ["windows", "linux"], ["x64"], []));

    private sealed class FakeModule(ModuleManifest? manifest, Action<ClientModuleBuilder>? register = null) : IClientModule
    {
        public int RegisterCalls { get; private set; }

        public ModuleManifest Manifest { get; } = manifest!;

        public void Register(ClientModuleBuilder builder)
        {
            RegisterCalls++;
            if (register is not null)
            {
                register(builder);
            }
            else
            {
                foreach (var kind in builder.Manifest.PaneKinds) builder.AddPaneKind(kind);
                foreach (var command in builder.Manifest.Commands) builder.AddCommand(command);
            }
        }
    }

    [Fact]
    public void Valid_modules_register_once_in_order_and_own_their_kinds_and_commands()
    {
        var gc = new FakeModule(Manifest("bazaar.gc"));
        var vault = new FakeModule(Manifest("bazaar.vault"));

        var result = ClientModuleHost.Load([gc, vault], _host);

        Assert.Equal(["bazaar.gc", "bazaar.vault"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.Empty(result.Skipped);
        Assert.Equal(1, gc.RegisterCalls);
        Assert.Equal("bazaar.vault", result.PaneKindOwners["bazaar.vault"]);
        Assert.Equal("bazaar.gc", result.CommandOwners["bazaar.gc.open"]);
    }

    [Fact]
    public void A_module_with_an_invalid_manifest_is_skipped_and_never_registered()
    {
        var bad = new FakeModule(Manifest("bazaar.bad") with { Version = "not-semver" });
        var good = new FakeModule(Manifest("bazaar.good"));
        var lines = new List<string>();

        var result = ClientModuleHost.Load([bad, good], _host, lines.Add);

        Assert.Equal(0, bad.RegisterCalls);
        Assert.Equal("invalid-manifest", Assert.Single(result.Skipped).Code);
        Assert.Equal(["bazaar.good"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.Contains("bazaar.bad", Assert.Single(lines));
    }

    [Fact]
    public void A_module_without_a_manifest_is_skipped()
    {
        var result = ClientModuleHost.Load([new FakeModule(null)], _host);

        Assert.Equal("no-manifest", Assert.Single(result.Skipped).Code);
        Assert.Empty(result.Loaded);
    }

    [Fact]
    public void A_module_the_host_cannot_run_is_skipped()
    {
        var windowsOnly = new FakeModule(Manifest("bazaar.win", os: ["windows"]));

        var result = ClientModuleHost.Load([windowsOnly], _host);

        Assert.Equal("host-incompatible", Assert.Single(result.Skipped).Code);
        Assert.Equal(0, windowsOnly.RegisterCalls);
    }

    [Fact]
    public void A_second_module_with_the_same_id_is_skipped()
    {
        var result = ClientModuleHost.Load([new FakeModule(Manifest("bazaar.gc")), new FakeModule(Manifest("bazaar.gc", paneKinds: ["other.pane"], commands: ["other.open"]))], _host);

        Assert.Equal("duplicate-id", Assert.Single(result.Skipped).Code);
        Assert.Single(result.Loaded);
    }

    [Fact]
    public void A_pane_kind_or_command_already_taken_skips_the_later_module()
    {
        var first = new FakeModule(Manifest("bazaar.a", paneKinds: ["bazaar.shared"], commands: ["bazaar.a.open"]));
        var kindClash = new FakeModule(Manifest("bazaar.b", paneKinds: ["bazaar.shared"], commands: ["bazaar.b.open"]));
        var commandClash = new FakeModule(Manifest("bazaar.c", paneKinds: ["bazaar.c"], commands: ["bazaar.a.open"]));

        var result = ClientModuleHost.Load([first, kindClash, commandClash], _host);

        Assert.Equal(["pane-kind-taken", "command-taken"], result.Skipped.Select(s => s.Code).ToArray());
        Assert.Equal(["bazaar.a"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.Equal(0, kindClash.RegisterCalls);
    }

    [Fact]
    public void A_module_that_throws_in_register_is_skipped_and_leaves_no_claim_on_its_kinds()
    {
        var broken = new FakeModule(Manifest("bazaar.broken", paneKinds: ["bazaar.k"]), _ => throw new InvalidOperationException("boom"));
        var later = new FakeModule(Manifest("bazaar.later", paneKinds: ["bazaar.k"]));

        var result = ClientModuleHost.Load([broken, later], _host);

        Assert.Equal("register-failed", Assert.Single(result.Skipped).Code);
        Assert.Equal(["bazaar.later"], result.Loaded.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void A_module_that_registers_outside_its_manifest_is_skipped()
    {
        var sneaky = new FakeModule(Manifest("bazaar.sneaky"), b => b.AddPaneKind("bazaar.other"));

        var result = ClientModuleHost.Load([sneaky], _host);

        Assert.Equal("register-failed", Assert.Single(result.Skipped).Code);
    }

    [Fact]
    public void Load_rejects_missing_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => ClientModuleHost.Load(null!, _host));
        Assert.Throws<ArgumentNullException>(() => ClientModuleHost.Load([], null!));
    }

    [Fact]
    public void A_module_registers_after_the_modules_it_depends_on()
    {
        var late = new FakeModule(Manifest("bazaar.a") with { DependsOn = ["bazaar.z"] });
        var early = new FakeModule(Manifest("bazaar.z"));

        var result = ClientModuleHost.Load([late, early], _host);

        Assert.Equal(["bazaar.z", "bazaar.a"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void A_module_with_a_missing_hard_dependency_is_skipped_and_so_are_its_dependents()
    {
        var needy = new FakeModule(Manifest("bazaar.needy") with { DependsOn = ["bazaar.absent"] });
        var downstream = new FakeModule(Manifest("bazaar.down") with { DependsOn = ["bazaar.needy"] });
        var fine = new FakeModule(Manifest("bazaar.fine"));

        var result = ClientModuleHost.Load([needy, downstream, fine], _host);

        Assert.Equal(["bazaar.fine"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.All(result.Skipped, skip => Assert.Equal("missing-dependency", skip.Code));
        Assert.Equal(2, result.Skipped.Count);
        Assert.Equal(0, needy.RegisterCalls);
    }

    [Fact]
    public void A_missing_soft_dependency_costs_nothing()
    {
        var module = new FakeModule(Manifest("bazaar.soft") with { SoftDependsOn = ["bazaar.absent"] });

        var result = ClientModuleHost.Load([module], _host);

        Assert.Empty(result.Skipped);
        Assert.Equal(1, module.RegisterCalls);
    }

    [Fact]
    public void A_hard_dependency_cycle_skips_the_modules_in_it_and_the_rest_still_load()
    {
        var a = new FakeModule(Manifest("bazaar.a") with { DependsOn = ["bazaar.b"] });
        var b = new FakeModule(Manifest("bazaar.b") with { DependsOn = ["bazaar.a"] });
        var behind = new FakeModule(Manifest("bazaar.behind") with { DependsOn = ["bazaar.a"] });
        var fine = new FakeModule(Manifest("bazaar.fine"));

        var result = ClientModuleHost.Load([a, b, behind, fine], _host);

        Assert.Equal(["bazaar.fine"], result.Loaded.Select(m => m.Id).ToArray());
        Assert.Equal(3, result.Skipped.Count(skip => skip.Code == "dependency-cycle"));
        Assert.Equal(0, a.RegisterCalls);
    }

    [Fact]
    public void A_soft_dependency_cycle_is_allowed()
    {
        var a = new FakeModule(Manifest("bazaar.a") with { SoftDependsOn = ["bazaar.b"] });
        var b = new FakeModule(Manifest("bazaar.b") with { SoftDependsOn = ["bazaar.a"] });

        var result = ClientModuleHost.Load([a, b], _host);

        Assert.Equal(2, result.Loaded.Count);
        Assert.Empty(result.Skipped);
    }
}

public class ClientModuleLoadResultTests
{
    private sealed class Pane(string id) : IClientModule
    {
        public ModuleManifest Manifest { get; } = new(
            id, "0.1.0", 1, [id], [], [], ["ui:pane"], ModuleIsolation.InProcessCompiled,
            new HostRequirements(1, ["linux"], ["x64"], []));

        public void Register(ClientModuleBuilder builder) => builder.AddPaneKind(Manifest.PaneKinds[0]);
    }

    [Fact]
    public void Only_keeps_the_modules_that_loaded_in_their_original_order()
    {
        var kept1 = new Pane("bazaar.one");
        var skipped = new Pane("bazaar.two");
        var kept2 = new Pane("bazaar.three");
        var wrongHost = new HostFacts("windows", "x64", new HashSet<string>(StringComparer.Ordinal));

        var result = ClientModuleHost.Load([kept1, skipped, kept2], wrongHost);
        var linux = ClientModuleHost.Load([kept1, skipped, kept2], new HostFacts("linux", "x64", new HashSet<string>(StringComparer.Ordinal)));

        Assert.Empty(result.Only<IClientModule>([kept1, skipped, kept2]));
        Assert.Equal([kept1, skipped, kept2], linux.Only<IClientModule>([kept1, skipped, kept2]));
    }

    [Fact]
    public void Only_drops_entries_that_are_not_client_modules()
    {
        var module = new Pane("bazaar.one");
        var result = ClientModuleHost.Load([module], new HostFacts("linux", "x64", new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal<object>([module], result.Only<object>([new object(), module]));
    }

    [Fact]
    public void Current_host_facts_name_a_known_os_and_architecture_on_a_supported_machine()
    {
        var facts = ClientHostFacts.Current();

        Assert.Contains(facts.OperatingSystem, HostCompatibility.KnownOperatingSystems);
        Assert.Contains(facts.Architecture, HostCompatibility.KnownArchitectures);
        Assert.Empty(facts.Features);
    }
}
