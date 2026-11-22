using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Bazaar.Client.Theme;
using Bazaar.Client.Views.Tests.Fixtures;

namespace Bazaar.Client.Views.Tests;

/// <summary>
/// A badge whose variant is a Kodo health state takes its colours from that state's theme tokens (doc 46 sections 8.1 and 12.1;
/// doc 41c section 3). The view names the state; the theme owns the colour. Each state here gets its own colour, so a badge
/// that picked the wrong state's token would be caught.
/// </summary>
public class HealthBadgeTests
{
    private static readonly (string State, string Background, string Text)[] _states =
    [
        ("healthy", "#102030", "#F0F1F2"),
        ("degraded", "#112131", "#F1F2F3"),
        ("failing", "#122232", "#F2F3F4"),
        ("recovering", "#132333", "#F3F4F5"),
        ("frozen", "#142434", "#F4F5F6"),
        ("unobserved", "#152535", "#F5F6F7"),
    ];

    private static ThemeDefinition ThemeWithDistinctStates()
    {
        ThemeDefinition baseline = ViewFixtures.Theme();
        var tokens = new Dictionary<string, string>(baseline.Tokens, StringComparer.Ordinal);
        foreach ((string state, string background, string text) in _states)
        {
            tokens[$"color.health.{state}.background"] = background;
            tokens[$"color.health.{state}.text"] = text;
        }

        return baseline with { Tokens = tokens };
    }

    [AvaloniaTheory]
    [InlineData("healthy")]
    [InlineData("degraded")]
    [InlineData("failing")]
    [InlineData("recovering")]
    [InlineData("frozen")]
    [InlineData("unobserved")]
    public void A_health_badge_uses_that_states_tokens(string state)
    {
        var harness = new Harness();
        (_, string background, string text) = _states.Single(s => s.State == state);
        var document = ViewFixtures.Document(ViewFixtures.Node("badge", [], ("text", state), ("variant", state)));

        Control control = harness.Build(document, ThemeWithDistinctStates());

        Assert.Null(ViewFixtures.AsPlaceholder(control));
        var badge = Assert.IsType<Border>(control);
        Assert.Equal(ViewFixtures.Parse(background), ViewFixtures.ColorOf(badge.Background));
        var label = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal(state, label.Text);
        Assert.Equal(ViewFixtures.Parse(text), ViewFixtures.ColorOf(label.Foreground));
    }

    [AvaloniaFact]
    public void An_unknown_variant_still_fails_closed()
    {
        var harness = new Harness();
        var document = ViewFixtures.Document(ViewFixtures.Node("badge", [], ("text", "x"), ("variant", "exploding")));

        Control control = harness.Build(document);

        Assert.Contains("not a variant token", ViewFixtures.PlaceholderText(control));
    }
}
