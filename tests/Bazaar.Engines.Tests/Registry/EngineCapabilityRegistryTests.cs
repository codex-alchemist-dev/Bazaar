using Bazaar.Contracts.Engines;
using Bazaar.Engines.Registry;

namespace Bazaar.Engines.Tests.Registry;

public class EngineCapabilityRegistryTests
{
    private static EngineManifest Manifest(string id, params EngineCapability[] capabilities) =>
        new(id, "4.7.2", capabilities, NeedsGuiHost: false);

    [Fact]
    public void Adapters_with_a_capability_are_returned_in_ordinal_order()
    {
        var registry = new EngineCapabilityRegistry();
        registry.Register(Manifest("roblox", EngineCapability.Scenes, EngineCapability.ScriptCheck));
        registry.Register(Manifest("godot", EngineCapability.Scenes, EngineCapability.Export));

        Assert.Equal(["godot", "roblox"], registry.AdaptersWithCapability(EngineCapability.Scenes));
        Assert.Equal(["roblox"], registry.AdaptersWithCapability(EngineCapability.ScriptCheck));
    }

    [Fact]
    public void Capability_no_adapter_declares_yields_an_empty_list()
    {
        var registry = new EngineCapabilityRegistry();
        registry.Register(Manifest("godot", EngineCapability.Scenes));

        Assert.Empty(registry.AdaptersWithCapability(EngineCapability.Playtest));
    }

    [Fact]
    public void Query_on_an_empty_registry_is_empty()
    {
        Assert.Empty(new EngineCapabilityRegistry().AdaptersWithCapability(EngineCapability.Scenes));
    }

    [Fact]
    public void Registering_the_same_adapter_twice_throws_and_keeps_the_first_manifest()
    {
        var registry = new EngineCapabilityRegistry();
        registry.Register(Manifest("godot", EngineCapability.Scenes));

        Assert.Throws<InvalidOperationException>(() => registry.Register(Manifest("godot", EngineCapability.Export)));
        Assert.Equal(["godot"], registry.AdaptersWithCapability(EngineCapability.Scenes));
        Assert.Empty(registry.AdaptersWithCapability(EngineCapability.Export));
    }

    [Fact]
    public void Changing_the_callers_capability_list_after_register_does_not_change_answers()
    {
        var capabilities = new List<EngineCapability> { EngineCapability.Scenes };
        var registry = new EngineCapabilityRegistry();
        registry.Register(new EngineManifest("godot", "4.7.2", capabilities, NeedsGuiHost: false));

        capabilities.Add(EngineCapability.Export);

        Assert.Empty(registry.AdaptersWithCapability(EngineCapability.Export));
    }

    [Fact]
    public void Find_returns_the_registered_manifest_or_null()
    {
        var registry = new EngineCapabilityRegistry();
        var godot = Manifest("godot", EngineCapability.Scenes);
        registry.Register(godot);

        Assert.Same(godot, registry.Find("godot"));
        Assert.Null(registry.Find("roblox"));
    }

    [Fact]
    public void Registering_a_manifest_without_an_engine_id_throws()
    {
        Assert.Throws<ArgumentException>(() => new EngineCapabilityRegistry().Register(Manifest(" ")));
    }
}
