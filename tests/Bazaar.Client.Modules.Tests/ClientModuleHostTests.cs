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
}
