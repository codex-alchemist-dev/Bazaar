using System.Text.Json;
using Bazaar.Client.Commands;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.TimeMachine.Tests;

public class TimeMachineModuleTests
{
    private static PaneDescriptor Descriptor(string model = """{"gcId":"0196a6f0-0000-7000-8000-0000000000aa","headSeq":5}""") =>
        new(ReplayScrubberViewModel.PaneKind, "Replay", string.Empty, JsonElement.Parse(model));

    [Fact]
    public void Manifest_is_valid()
    {
        var result = ModuleManifestValidator.Validate(new TimeMachineModule().Manifest);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Code)));
    }

    [Fact]
    public void Register_adds_exactly_the_pane_kind_and_command_the_manifest_declares()
    {
        var module = new TimeMachineModule();
        var builder = new ClientModuleBuilder(module.Manifest);

        module.Register(builder);

        Assert.Equal([ReplayScrubberViewModel.PaneKind], builder.RegisteredPaneKinds);
        Assert.Equal([TimeMachineModule.OpenCommandId], builder.RegisteredCommands);
    }

    [Fact]
    public void Palette_command_is_keyboard_first_and_loads_into_the_registry()
    {
        var command = TimeMachineModule.OpenCommand;

        Assert.Equal(TimeMachineModule.OpenCommandId, command.Id);
        Assert.Equal("replay", command.Mnemonic);
        Assert.All(command.Mnemonic, c => Assert.True(char.IsLetterOrDigit(c)));
        var loaded = CommandRegistry.Load([command], []);
        Assert.Same(command, loaded.Registry.FindByMnemonic("replay"));
    }

    [Fact]
    public void Pane_module_registers_the_kind_and_builds_the_view_model_with_the_head_supplied_client()
    {
        var client = new InMemoryReplayClient(5);
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext().With<IReplayClient>(client));

        var pane = registry.Create(Descriptor());

        Assert.Equal([ReplayScrubberViewModel.PaneKind], registry.Kinds);
        var vm = Assert.IsType<ReplayScrubberViewModel>(pane);
        Assert.True(vm.HasClient);
        Assert.Equal(5, vm.HeadSeq);
    }

    [Fact]
    public void A_bad_model_falls_back_to_the_generic_pane_and_reports_once()
    {
        var reports = new List<string>();
        var registry = PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, [TimeMachineModule.Pane], new PaneModuleContext(Report: reports.Add));

        var pane = registry.Create(Descriptor("""{"headSeq":5}"""));

        Assert.IsType<GenericPaneViewModel>(pane);
        Assert.Single(reports);
    }
}
