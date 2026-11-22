using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Core.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Modules;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Rubedo.Playground.Tests;

public class PlaygroundModuleTests
{
    [Fact]
    public void Manifest_is_valid()
    {
        var result = ModuleManifestValidator.Validate(new PlaygroundPaneModule().Manifest);

        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => i.Code)));
    }

    [Fact]
    public void Module_is_optional_and_off_by_default()
    {
        Assert.False(PlaygroundPaneModule.EnabledByDefault);
    }

    [Fact]
    public void Register_adds_the_pane_kind_and_command_the_manifest_declares()
    {
        var module = new PlaygroundPaneModule();
        var builder = new ClientModuleBuilder(module.Manifest);

        module.Register(builder);

        Assert.Equal([PlaygroundViewModel.PaneKind], builder.RegisteredPaneKinds);
        Assert.Equal([PlaygroundPaneModule.OpenCommandId], builder.RegisteredCommands);
        Assert.Equal("rubedoplay", Assert.Single(builder.Contributions).Mnemonic);
    }

    [AvaloniaFact]
    public void Pane_registry_builds_the_playground_view()
    {
        var registry = PaneRegistry.Compose(PaneViews.Instance, [new PlaygroundPaneModule()]);
        var pane = registry.Create(new PaneDescriptor(PlaygroundViewModel.PaneKind, "x", string.Empty, System.Text.Json.JsonElement.Parse("{}")));

        Assert.IsType<PlaygroundViewModel>(pane);
        Assert.IsAssignableFrom<Control>(registry.CreateView(pane));
    }
}
