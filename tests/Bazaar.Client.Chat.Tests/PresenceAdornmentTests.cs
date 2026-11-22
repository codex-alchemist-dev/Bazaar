using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Chat.Abstractions;

namespace Bazaar.Client.Chat.Tests;

public class PresenceAdornmentTests
{
    private static readonly Guid _agent = Guid.Parse("0196a6f0-0000-7000-8000-0000000000a1");

    private sealed class FakeAdornment(string id, int order, Func<Guid, string?> text) : IPresenceChipAdornment
    {
        public string Id { get; } = id;

        public int Order { get; } = order;

        public event Action<Guid>? Changed;

        public Control? Create(Guid agentId) => text(agentId) is { } t ? new TextBlock { Text = t } : null;

        public void Raise(Guid agentId) => Changed?.Invoke(agentId);
    }

    private static string[] Texts(PresenceChipAdornments chip) =>
        [.. chip.Children.OfType<TextBlock>().Select(t => t.Text!)];

    [Fact]
    public void Registry_orders_by_order_then_registration_and_refuses_a_duplicate_id()
    {
        var registry = new PresenceAdornmentRegistry();
        registry.Add(new FakeAdornment("b", 5, _ => "b"));
        registry.Add(new FakeAdornment("a", 1, _ => "a"));
        registry.Add(new FakeAdornment("c", 5, _ => "c"));

        Assert.Equal(["a", "b", "c"], registry.Adornments.Select(a => a.Id));
        Assert.Throws<InvalidOperationException>(() => registry.Add(new FakeAdornment("a", 9, _ => "x")));
    }

    [AvaloniaFact]
    public void Chip_renders_in_order_and_gives_a_null_adornment_no_slot()
    {
        var registry = new PresenceAdornmentRegistry();
        registry.Add(new FakeAdornment("late", 2, _ => "two"));
        registry.Add(new FakeAdornment("none", 1, _ => null));
        registry.Add(new FakeAdornment("first", 0, _ => "zero"));

        using var chip = new PresenceChipAdornments(registry, _agent);

        Assert.Equal(["zero", "two"], Texts(chip));
        Assert.True(chip.IsVisible);
    }

    [AvaloniaFact]
    public void Chip_with_nothing_to_show_is_hidden()
    {
        using var chip = new PresenceChipAdornments(new PresenceAdornmentRegistry(), _agent);

        Assert.Empty(chip.Children);
        Assert.False(chip.IsVisible);
    }

    [AvaloniaFact]
    public void Chip_asks_again_when_its_agent_changes_and_ignores_other_agents()
    {
        var value = "depth 0";
        var registry = new PresenceAdornmentRegistry();
        var adornment = new FakeAdornment("interrupt-stack", 0, _ => value);
        registry.Add(adornment);
        using var chip = new PresenceChipAdornments(registry, _agent);

        value = "depth 1";
        adornment.Raise(Guid.NewGuid());
        Assert.Equal(["depth 0"], Texts(chip));

        adornment.Raise(_agent);
        Assert.Equal(["depth 1"], Texts(chip));
    }

    [AvaloniaFact]
    public void Chip_picks_up_an_adornment_registered_later_and_survives_a_throwing_one()
    {
        var registry = new PresenceAdornmentRegistry();
        using var chip = new PresenceChipAdornments(registry, _agent);

        registry.Add(new FakeAdornment("broken", 0, _ => throw new InvalidOperationException("boom")));
        registry.Add(new FakeAdornment("ok", 1, _ => "ok"));

        Assert.Equal(["ok"], Texts(chip));
    }

    [AvaloniaFact]
    public void Disposed_chip_stops_listening()
    {
        var value = "a";
        var registry = new PresenceAdornmentRegistry();
        var adornment = new FakeAdornment("x", 0, _ => value);
        registry.Add(adornment);
        var chip = new PresenceChipAdornments(registry, _agent);
        chip.Dispose();

        value = "b";
        adornment.Raise(_agent);

        Assert.Equal(["a"], Texts(chip));
    }
}
