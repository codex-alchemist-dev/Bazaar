namespace Bazaar.Client.Updates.Tests;

public class ChannelTests
{
    [Fact]
    public void Default_channel_is_stable() => Assert.Equal(UpdateChannel.Stable, new ClientUpdateOptions().Channel);

    [Theory]
    [InlineData("stable", UpdateChannel.Stable)]
    [InlineData("Beta", UpdateChannel.Beta)]
    [InlineData(" NIGHTLY ", UpdateChannel.Nightly)]
    [InlineData("lts", UpdateChannel.Lts)]
    [InlineData("alpha", UpdateChannel.Nightly)]
    public void Parses_setting_values_and_maps_alpha_to_nightly(string text, UpdateChannel expected)
    {
        Assert.True(UpdateChannels.TryParse(text, out var channel));
        Assert.Equal(expected, channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ltsc")]
    [InlineData("latest-commit")]
    public void Unknown_values_fail_and_fall_back_to_the_default(string? text)
    {
        Assert.False(UpdateChannels.TryParse(text, out var channel));
        Assert.Equal(UpdateChannels.Default, channel);
    }

    [Theory]
    [InlineData(UpdateChannel.Stable, "win-x64-stable")]
    [InlineData(UpdateChannel.Beta, "win-x64-beta")]
    [InlineData(UpdateChannel.Nightly, "win-x64-nightly")]
    [InlineData(UpdateChannel.Lts, "win-x64-lts")]
    public void Velopack_channel_matches_the_pack_script_naming(UpdateChannel channel, string expected) =>
        Assert.Equal(expected, new ClientUpdateOptions().VelopackChannel(channel));

    [Fact]
    public void Runtime_is_part_of_the_velopack_channel() =>
        Assert.Equal("win-arm64-beta", new ClientUpdateOptions { Runtime = "win-arm64" }.VelopackChannel(UpdateChannel.Beta));

    [Fact]
    public void Invalid_runtime_is_rejected() =>
        Assert.Throws<InvalidOperationException>(() => new ClientUpdateOptions { Runtime = "../x" }.VelopackChannel(UpdateChannel.Stable));

    [Fact]
    public void Channel_feed_override_wins_over_the_shared_base()
    {
        var options = new ClientUpdateOptions { FeedBaseUrl = new Uri("https://updates.example/feed/") };
        options.ChannelFeeds[UpdateChannel.Nightly] = new Uri("https://nightly.example/feed/");

        Assert.Equal("https://updates.example/feed/", options.ResolveFeed(UpdateChannel.Stable)?.AbsoluteUri);
        Assert.Equal("https://nightly.example/feed/", options.ResolveFeed(UpdateChannel.Nightly)?.AbsoluteUri);
    }

    [Fact]
    public void No_feed_is_configured_by_default() => Assert.Null(new ClientUpdateOptions().ResolveFeed(UpdateChannel.Stable));
}
