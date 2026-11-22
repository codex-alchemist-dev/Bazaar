using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Bazaar.Client.Abstractions.Panes;
using Bazaar.Client.Interrupts.Panes;
using Bazaar.Client.Panes.Abstractions;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Interrupts.Tests;

public class InterruptStackTests
{
    private const string _model = """
        {"agents":[
          {"agentId":"a1","name":"Charles","frames":[
            {"id":"f1","source":"system_build","reason":"build broke","state":"suspended","pushedAt":"2026-11-18T09:00:00Z"},
            {"id":"f2","source":"owner","reason":"look at the board","state":"active","pushedAt":"2026-11-18T10:00:00Z"},
            {"id":"f0","source":"owner","reason":"old","state":"done","pushedAt":"2026-11-17T10:00:00Z"}]},
          {"agentId":"a2","name":"Idle","frames":[
            {"id":"g1","source":"owner","reason":"x","state":"cancelled","pushedAt":"2026-11-18T10:00:00Z"}]}
        ]}
        """;

    [Fact]
    public void Lists_only_agents_with_live_frames_top_first()
    {
        var pane = new InterruptStackPaneViewModel(Descriptor(_model));

        var agent = Assert.Single(pane.Agents);
        Assert.Equal("Charles", agent.Name);
        Assert.Equal(2, agent.Indicator.Depth);
        Assert.Equal(["owner", "system build"], agent.Frames.Select(f => f.Source));
        Assert.True(agent.Frames[0].IsTop);
        Assert.Equal("look at the board", agent.Frames[0].Reason);
        Assert.Equal("2026-11-18 10:00 UTC", agent.Frames[0].PushedAt);
        Assert.Null(agent.OverLimitNote);
    }

    [Fact]
    public void Indicator_text_and_accessible_name_carry_depth_and_source()
    {
        var indicator = StackIndicatorViewModel.From(new AgentInterruptStack(
            "a",
            "A",
            [new InterruptFrameSummary("1", InterruptSource.Owner, "r", InterruptFrameState.Active, DateTimeOffset.UnixEpoch)]));

        Assert.Equal("Interrupt stack depth 1, top frame owner", indicator.AccessibleName);
        Assert.Contains("owner", indicator.Text);
        Assert.False(StackIndicatorViewModel.Empty.IsVisible);
        Assert.Equal(string.Empty, StackIndicatorViewModel.Empty.Text);
    }

    [Fact]
    public void More_than_two_live_frames_is_capped_with_a_note_and_never_throws()
    {
        var pane = new InterruptStackPaneViewModel(Descriptor("""
            {"agents":[{"agentId":"a","name":"Busy","frames":[
              {"id":"1","source":"owner","reason":"a","state":"suspended","pushedAt":"2026-11-18T08:00:00Z"},
              {"id":"2","source":"system_build","reason":"b","state":"suspended","pushedAt":"2026-11-18T09:00:00Z"},
              {"id":"3","source":"system_security","reason":"c","state":"active","pushedAt":"2026-11-18T10:00:00Z"}]}]}
            """));

        var agent = Assert.Single(pane.Agents);
        Assert.Equal(2, agent.Indicator.Depth);
        Assert.True(agent.Indicator.IsOverLimit);
        Assert.Contains("over limit", agent.Indicator.Text);
        Assert.Equal(2, agent.Frames.Count);
        Assert.Contains("over limit", agent.OverLimitNote);
        Assert.Equal("system security", agent.Frames[0].Source);
    }

    [Theory]
    [InlineData("""{"agents":[{"agentId":"a","name":"n","frames":[{"id":"1","source":"agent","reason":"r","state":"active","pushedAt":"2026-11-18T10:00:00Z"}]}]}""")]
    [InlineData("""{"agents":[{"agentId":"a","name":"n","frames":[{"id":"1","source":"owner","reason":"r","state":"paused","pushedAt":"2026-11-18T10:00:00Z"}]}]}""")]
    [InlineData("""{"agents":[{"agentId":"a","name":"n","frames":[{"id":"1","source":"owner","reason":"r","state":"active","pushedAt":"yesterday"}]}]}""")]
    [InlineData("""{"agents":[{"agentId":"a","name":"n"}]}""")]
    [InlineData("""{}""")]
    public void Malformed_models_raise_PaneModelException(string model) =>
        Assert.Throws<PaneModelException>(() => new InterruptStackPaneViewModel(Descriptor(model)));

    [Fact]
    public void Registry_builds_the_pane_and_falls_back_on_bad_data()
    {
        var registry = Registry();

        Assert.IsType<InterruptStackPaneViewModel>(registry.Create(Descriptor(_model)));
        Assert.IsType<GenericPaneViewModel>(registry.Create(Descriptor(
            """{"agents":[{"agentId":"a","name":"n","frames":[{"id":"1","source":"x","reason":"r","state":"active","pushedAt":"2026-11-18T10:00:00Z"}]}]}""")));
    }

    [AvaloniaFact]
    public void Pane_view_shows_frames_and_the_indicator_names_depth_and_source()
    {
        var registry = Registry();
        var view = registry.CreateView(registry.Create(Descriptor(_model)));

        var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Charles", texts);
        Assert.Contains("look at the board", texts);
        Assert.Contains("Top · owner · 2026-11-18 10:00 UTC", texts);

        var indicator = Assert.Single(view.GetLogicalDescendants().OfType<StackIndicator>());
        Assert.True(indicator.IsVisible);
        Assert.Equal("Interrupt stack depth 2, top frame owner", AutomationProperties.GetName(indicator));
    }

    [AvaloniaFact]
    public void Indicator_is_hidden_at_depth_zero_and_pane_says_nobody_is_interrupted()
    {
        var indicator = new StackIndicator { Model = StackIndicatorViewModel.Empty };
        Assert.False(indicator.IsVisible);
        Assert.Null(indicator.Child);

        var registry = Registry();
        var view = registry.CreateView(registry.Create(Descriptor("""{"agents":[]}""")));
        Assert.Contains("No agent is interrupted.", view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));
    }

    private static PaneDescriptor Descriptor(string model) =>
        new(InterruptPanes.StackKind, "Interrupts", "view", JsonElement.Parse(model));

    private static PaneRegistry Registry() => PaneRegistry.Compose(Bazaar.Client.Core.Panes.PaneViews.Instance, InterruptPanes.Modules);
}
