using Bazaar.Client.Linux.Updates;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Linux.Tests;

public class UpdateSettingsReaderTests
{
    private const string _fingerprint = "0123456789abcdef0123456789abcdef01234567";
    private static readonly string _pin = new('a', 64);
    private static readonly UpdatePaths _paths = UpdatePaths.FromEnvironment(_ => null, "/home/ada");

    private static UpdateConfiguration Read(
        Dictionary<string, string> env,
        UpdateBuildIdentity? build = null,
        Func<string, bool>? fileExists = null) =>
        UpdateSettingsReader.Read(
            name => env.GetValueOrDefault(name),
            _paths,
            build ?? new UpdateBuildIdentity(null, null),
            "0.1.0",
            "linux-x64",
            fileExists ?? (_ => true));

    private static Dictionary<string, string> Good() => new()
    {
        [UpdateSettingsReader.ManifestUrlVariable] = "https://bazaar.example/updates/latest",
        [UpdateSettingsReader.ServerPinVariable] = _pin,
        [UpdateSettingsReader.KeyFingerprintVariable] = _fingerprint,
    };

    [Fact]
    public void Paths_follow_the_xdg_homes()
    {
        var paths = UpdatePaths.FromEnvironment(n => n == "XDG_DATA_HOME" ? "/data" : "relative", "/home/ada");

        Assert.Equal("/data/bazaar/client", paths.InstallRoot.Replace('\\', '/'));
        Assert.Equal("/home/ada/.config/bazaar/update-key.asc", paths.DefaultKeyFile.Replace('\\', '/'));
    }

    [Fact]
    public void Nothing_configured_turns_updates_off()
    {
        var config = Read([]);

        Assert.False(config.Enabled);
        Assert.Null(config.Settings);
        Assert.Contains("No update feed", config.DisabledReason);
    }

    [Fact]
    public void Complete_environment_turns_updates_on_with_safe_defaults()
    {
        var config = Read(Good());

        Assert.True(config.Enabled);
        Assert.Equal(ReleaseChannels.Stable, config.Settings!.Channel);
        Assert.Equal(_fingerprint, config.Settings.PinnedKeyFingerprint);
        Assert.Equal(_pin, config.ServerKeyHashPin);
        Assert.Equal("https://bazaar.example/updates/latest", config.ManifestUri!.ToString());
        Assert.EndsWith("bazaar/update-key.asc", config.Settings.PinnedKeyPath.Replace('\\', '/'));
    }

    [Fact]
    public void Build_identity_supplies_the_feed_and_the_pin()
    {
        var env = Good();
        env.Remove(UpdateSettingsReader.ManifestUrlVariable);
        env.Remove(UpdateSettingsReader.ServerPinVariable);

        var config = Read(env, new UpdateBuildIdentity(new Uri("https://company.example/"), _pin));

        Assert.True(config.Enabled);
        Assert.Equal("https://company.example/updates/latest", config.ManifestUri!.ToString());
    }

    [Fact]
    public void Missing_signing_key_fingerprint_fails_closed()
    {
        var env = Good();
        env.Remove(UpdateSettingsReader.KeyFingerprintVariable);

        Assert.False(Read(env).Enabled);
    }

    [Theory]
    [InlineData("0123")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Malformed_fingerprint_fails_closed(string fingerprint)
    {
        var env = Good();
        env[UpdateSettingsReader.KeyFingerprintVariable] = fingerprint;

        Assert.False(Read(env).Enabled);
    }

    [Fact]
    public void Missing_key_file_fails_closed()
    {
        var config = Read(Good(), fileExists: _ => false);

        Assert.False(config.Enabled);
        Assert.Contains("key file is missing", config.DisabledReason);
    }

    [Fact]
    public void Https_feed_without_a_valid_pin_fails_closed()
    {
        var env = Good();
        env.Remove(UpdateSettingsReader.ServerPinVariable);

        Assert.False(Read(env).Enabled);
    }

    [Theory]
    [InlineData("http://bazaar.example/updates/latest")]
    [InlineData("ftp://bazaar.example/updates/latest")]
    [InlineData("https://user:pw@bazaar.example/updates/latest")]
    [InlineData("not a url")]
    public void Unsafe_feed_addresses_fail_closed(string url)
    {
        var env = Good();
        env[UpdateSettingsReader.ManifestUrlVariable] = url;

        Assert.False(Read(env).Enabled);
    }

    [Fact]
    public void Loopback_http_feed_needs_no_pin()
    {
        var env = Good();
        env[UpdateSettingsReader.ManifestUrlVariable] = "http://127.0.0.1:8080/updates/latest";
        env.Remove(UpdateSettingsReader.ServerPinVariable);

        var config = Read(env);

        Assert.True(config.Enabled);
        Assert.Null(config.ServerKeyHashPin);
    }

    [Fact]
    public void Disabled_flag_turns_updates_off()
    {
        var env = Good();
        env[UpdateSettingsReader.EnabledVariable] = "false";

        Assert.False(Read(env).Enabled);
    }

    [Fact]
    public void Unknown_channel_fails_closed_and_a_known_one_is_used()
    {
        var env = Good();
        env[UpdateSettingsReader.ChannelVariable] = "Nightly";
        Assert.False(Read(env).Enabled);

        env[UpdateSettingsReader.ChannelVariable] = ReleaseChannels.Beta;
        Assert.Equal(ReleaseChannels.Beta, Read(env).Settings!.Channel);
    }

    [Fact]
    public void Limits_may_tighten_and_never_loosen()
    {
        var env = Good();
        env[UpdateSettingsReader.MaxArchiveEntriesVariable] = "500";
        Assert.Equal(500, Read(env).Settings!.MaxArchiveEntries);

        env[UpdateSettingsReader.MaxArchiveEntriesVariable] = "100001";
        Assert.False(Read(env).Enabled);

        env[UpdateSettingsReader.MaxArchiveEntriesVariable] = "-3";
        Assert.False(Read(env).Enabled);
    }
}
