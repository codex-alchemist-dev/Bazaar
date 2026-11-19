using System.Text.RegularExpressions;
using Bazaar.Contracts.Kodo;

namespace Bazaar.Client.Kodo.Tests.Kodo;

public class KodoStateTokensTests
{
    [Fact]
    public void Every_display_state_has_exactly_one_token()
    {
        foreach (var state in Enum.GetValues<KodoDisplayState>())
        {
            Assert.Single(KodoStateTokens.All, t => t.State == state);
            Assert.Equal(
                KodoStateTokens.Prefix + state.ToString().ToLowerInvariant(),
                KodoStateTokens.For(state).Name);
        }

        Assert.Equal(Enum.GetValues<KodoDisplayState>().Length, KodoStateTokens.All.Count);
    }

    [Fact]
    public void Every_health_state_maps_to_a_token()
    {
        foreach (var state in Enum.GetValues<KodoHealthState>())
        {
            Assert.EndsWith(state.ToString().ToLowerInvariant(), KodoStateTokens.For(state).Name, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Values_are_opaque_hex_and_light_differs_from_dark()
    {
        foreach (var token in KodoStateTokens.All)
        {
            Assert.Matches(new Regex("^#[0-9A-F]{6}$"), token.Light);
            Assert.Matches(new Regex("^#[0-9A-F]{6}$"), token.Dark);
            Assert.NotEqual(token.Light, token.Dark);
        }
    }

    [Fact]
    public void Values_meet_text_contrast_on_default_surfaces()
    {
        foreach (var token in KodoStateTokens.All)
        {
            Assert.True(Contrast(token.Light, "#FFFFFF") >= 4.5, token.Name + " light");
            Assert.True(Contrast(token.Dark, "#121212") >= 4.5, token.Name + " dark");
        }
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int shift)
        {
            var c = ((Convert.ToInt32(hex[1..], 16) >> shift) & 0xFF) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(16)) + (0.7152 * Channel(8)) + (0.0722 * Channel(0));
    }
}
