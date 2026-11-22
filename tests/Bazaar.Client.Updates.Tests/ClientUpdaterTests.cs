using System.Security.Cryptography;
using Bazaar.Updates;

namespace Bazaar.Client.Updates.Tests;

public sealed class ClientUpdaterTests : IDisposable
{
    private static readonly byte[] _package = [1, 2, 3, 4, 5];
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bazaar-updater-{Guid.NewGuid():N}.nupkg");
    private readonly FakeAdapter _adapter = new();
    private readonly FakeManifests _manifests = new();
    private readonly FakeSignatures _signatures = new();
    private readonly ClientUpdateOptions _options = new() { FeedBaseUrl = new Uri("https://updates.example/feed/") };

    public ClientUpdaterTests()
    {
        File.WriteAllBytes(_file, _package);
        _adapter.PackagePath = _file;
        _adapter.Installed = "0.1.0+abc1234";
        _adapter.Offered = "0.2.0";
        _manifests.Manifest = Manifest("0.2.0", _package);
    }

    public void Dispose() => File.Delete(_file);

    private ClientUpdater Create() => new(_options, _adapter, _manifests, _signatures);

    private static UpdateManifest Manifest(string version, byte[] package) =>
        new(version, new Uri("https://updates.example/p.nupkg"), Convert.ToHexStringLower(SHA256.HashData(package)), [9]);

    [Fact]
    public async Task Not_an_installed_build_is_unsupported()
    {
        _adapter.Installed = null;

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Unsupported, state.Phase);
        Assert.Equal(0, _adapter.Checks);
    }

    [Fact]
    public async Task No_offer_is_up_to_date()
    {
        _adapter.Offered = null;

        Assert.Equal(ClientUpdatePhase.UpToDate, (await Create().CheckAsync(CancellationToken.None)).Phase);
    }

    [Fact]
    public async Task Check_uses_the_channel_feed_and_velopack_channel()
    {
        _options.ChannelFeeds[UpdateChannel.Beta] = new Uri("https://beta.example/f/");
        using var updater = Create();
        updater.SetChannel(UpdateChannel.Beta);

        await updater.CheckAsync(CancellationToken.None);

        Assert.Equal("https://beta.example/f/", _adapter.LastFeed?.AbsoluteUri);
        Assert.Equal("win-x64-beta", _adapter.LastChannel);
    }

    [Fact]
    public async Task Newer_signed_offer_is_available()
    {
        using var updater = Create();

        var state = await updater.CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Available, state.Phase);
        Assert.Equal("0.2.0", state.OfferedVersion);
    }

    [Fact]
    public async Task Offer_that_is_not_newer_is_up_to_date()
    {
        _adapter.Offered = "0.1.0";
        _manifests.Manifest = Manifest("0.1.0", _package);

        Assert.Equal(ClientUpdatePhase.UpToDate, (await Create().CheckAsync(CancellationToken.None)).Phase);
    }

    [Fact]
    public async Task Offer_without_a_signed_manifest_fails_closed()
    {
        _manifests.Manifest = null;

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Failed, state.Phase);
        Assert.Equal(ClientUpdateFailure.ManifestMissing, state.Failure);
    }

    [Fact]
    public async Task Feed_and_manifest_versions_must_agree()
    {
        _manifests.Manifest = Manifest("0.3.0", _package);

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdateFailure.VersionMismatch, state.Failure);
    }

    [Fact]
    public async Task Insecure_feed_is_refused_without_a_network_call()
    {
        _options.FeedBaseUrl = new Uri("http://updates.example/feed/");

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Refused, state.Phase);
        Assert.Equal(0, _adapter.Checks);
    }

    [Fact]
    public async Task Missing_feed_fails()
    {
        _options.FeedBaseUrl = null;

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Failed, state.Phase);
    }

    [Fact]
    public async Task Feed_error_lands_in_state_instead_of_throwing()
    {
        _adapter.CheckThrows = new HttpRequestException("down");

        var state = await Create().CheckAsync(CancellationToken.None);

        Assert.Equal(ClientUpdateFailure.Error, state.Failure);
        Assert.Equal("down", state.Detail);
    }

    [Fact]
    public async Task Download_verifies_then_becomes_ready()
    {
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);

        var state = await updater.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Ready, state.Phase);
        Assert.Equal(100, state.DownloadPercent);
    }

    [Fact]
    public async Task Tampered_package_fails_verification_and_cannot_be_applied()
    {
        File.WriteAllBytes(_file, [1, 2, 3, 4, 6]);
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);

        var state = await updater.DownloadAsync(null, CancellationToken.None);
        await updater.ApplyAsync(CancellationToken.None);

        Assert.Equal(ClientUpdateFailure.VerificationFailed, state.Failure);
        Assert.Equal(0, _adapter.Applies);
    }

    [Fact]
    public async Task Bad_signature_fails_verification()
    {
        _signatures.Valid = false;
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);

        var state = await updater.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(ClientUpdateFailure.VerificationFailed, state.Failure);
    }

    [Fact]
    public async Task Download_before_a_check_does_nothing()
    {
        using var updater = Create();

        var state = await updater.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Idle, state.Phase);
        Assert.Equal(0, _adapter.Downloads);
    }

    [Fact]
    public async Task Apply_before_ready_does_not_restart()
    {
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);

        await updater.ApplyAsync(CancellationToken.None);

        Assert.Equal(0, _adapter.Applies);
    }

    [Fact]
    public async Task Apply_after_a_verified_download_restarts_once()
    {
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);
        await updater.DownloadAsync(null, CancellationToken.None);

        await updater.ApplyAsync(CancellationToken.None);

        Assert.Equal(1, _adapter.Applies);
    }

    [Fact]
    public async Task Switching_channel_discards_a_pending_offer()
    {
        using var updater = Create();
        await updater.CheckAsync(CancellationToken.None);

        updater.SetChannel(UpdateChannel.Lts);
        var state = await updater.DownloadAsync(null, CancellationToken.None);

        Assert.Equal(ClientUpdatePhase.Idle, state.Phase);
        Assert.Equal(UpdateChannel.Lts, state.Channel);
    }

    private sealed class FakeAdapter : IVelopackAdapter
    {
        public string? Installed { get; set; }

        public string? Offered { get; set; }

        public string PackagePath { get; set; } = string.Empty;

        public Exception? CheckThrows { get; set; }

        public Uri? LastFeed { get; private set; }

        public string? LastChannel { get; private set; }

        public int Checks { get; private set; }

        public int Downloads { get; private set; }

        public int Applies { get; private set; }

        public string? InstalledVersion => Installed;

        public Task<string?> CheckAsync(Uri feed, string velopackChannel, CancellationToken ct)
        {
            Checks++;
            LastFeed = feed;
            LastChannel = velopackChannel;
            return CheckThrows is null ? Task.FromResult(Offered) : Task.FromException<string?>(CheckThrows);
        }

        public Task<string> DownloadAsync(IProgress<int>? progress, CancellationToken ct)
        {
            Downloads++;
            progress?.Report(100);
            return Task.FromResult(PackagePath);
        }

        public void ApplyAndRestart() => Applies++;
    }

    private sealed class FakeManifests : IClientUpdateManifestSource
    {
        public UpdateManifest? Manifest { get; set; }

        public Task<UpdateManifest?> GetManifestAsync(UpdateChannel channel, CancellationToken ct) => Task.FromResult(Manifest);
    }

    private sealed class FakeSignatures : IUpdateSignatureVerifier
    {
        public bool Valid { get; set; } = true;

        public bool Verify(ReadOnlySpan<byte> package, ReadOnlySpan<byte> signature) => Valid;
    }
}
