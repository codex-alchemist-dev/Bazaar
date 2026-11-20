using System.Text;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux.Tests;

public class LinuxUpdaterTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    // ---- channel mapping (doc 30 section 5, Testing) ----

    [Theory]
    [InlineData(ReleaseChannels.Ltsc, "0.2.0")]
    [InlineData(ReleaseChannels.Stable, "0.2.0")]
    [InlineData(ReleaseChannels.Beta, "0.2.0-beta.1")]
    [InlineData(ReleaseChannels.Beta, "0.2.0-rc.1")]
    [InlineData(ReleaseChannels.Alpha, "0.2.0-alpha.1")]
    public async Task Each_channel_accepts_a_release_of_its_own_channel(string channel, string version)
    {
        var rig = new Rig(channel: channel);
        rig.Publish(version, channel: channel);

        var result = await rig.Updater.CheckAsync(_ct);

        Assert.Equal(UpdateStatus.Available, result.Status);
        Assert.Equal(version, result.Version);
    }

    [Theory]
    [InlineData(ReleaseChannels.Stable, ReleaseChannels.Beta, "0.2.0-beta.1")]
    [InlineData(ReleaseChannels.Beta, ReleaseChannels.Stable, "0.2.0")]
    [InlineData(ReleaseChannels.Alpha, ReleaseChannels.Beta, "0.2.0-beta.1")]
    [InlineData(ReleaseChannels.Ltsc, ReleaseChannels.Stable, "0.2.0")]
    public async Task A_release_of_another_channel_is_refused(string configured, string offered, string version)
    {
        var rig = new Rig(channel: configured);
        rig.Publish(version, channel: offered);

        var result = await rig.Updater.CheckAsync(_ct);

        Assert.Equal(UpdateStatus.ChannelMismatch, result.Status);
    }

    [Fact]
    public async Task Latest_commit_is_refused_for_a_non_owner_install_without_reading_the_feed()
    {
        var rig = new Rig(channel: ReleaseChannels.LatestCommit, owner: false);
        rig.Publish("0.2.0-dev.7", channel: ReleaseChannels.LatestCommit);

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.ChannelRefused, result.Status);
        Assert.Equal(0, rig.Source.ManifestReads);
        Assert.Empty(rig.Source.Opened);
    }

    [Fact]
    public async Task Latest_commit_is_offered_on_the_owner_install()
    {
        var rig = new Rig(channel: ReleaseChannels.LatestCommit, owner: true);
        rig.Publish("0.2.0-dev.7", channel: ReleaseChannels.LatestCommit);

        var result = await rig.Updater.CheckAsync(_ct);

        Assert.Equal(UpdateStatus.Available, result.Status);
    }

    [Fact]
    public async Task An_unknown_channel_setting_is_refused()
    {
        var rig = new Rig();
        rig.Settings = rig.Settings with { Channel = "stable" }; // names are case-sensitive
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.ChannelRefused, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    // ---- manifest and versions ----

    [Fact]
    public async Task No_manifest_leaves_the_install_alone()
    {
        var rig = new Rig();

        Assert.Equal(UpdateStatus.NoManifest, (await rig.Updater.ApplyAsync(_ct)).Status);
        Assert.Null(rig.Current);
    }

    [Fact]
    public async Task An_unreadable_feed_is_reported_as_no_manifest()
    {
        var rig = new Rig();
        rig.Source.ManifestError = new IOException("connection reset");

        Assert.Equal(UpdateStatus.NoManifest, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Theory]
    [InlineData("0.1.0", "0.0.9", UpdateStatus.DowngradeRefused)]
    [InlineData("0.2.0", "0.1.9", UpdateStatus.DowngradeRefused)]
    [InlineData("0.1.0", "0.1.0", UpdateStatus.UpToDate)]
    [InlineData("0.1.0+aaaaaaa", "0.1.0+bbbbbbb", UpdateStatus.UpToDate)]
    [InlineData("0.1.0", "0.1.1", UpdateStatus.Available)]
    [InlineData("0.9.0", "0.10.0", UpdateStatus.Available)]
    [InlineData("0.2.0-rc.1", "0.2.0", UpdateStatus.Available)]
    public async Task Versions_compare_by_semver_precedence(string installed, string offered, UpdateStatus expected)
    {
        var rig = new Rig(installed: installed);
        rig.Publish(offered);

        Assert.Equal(expected, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Theory]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.1", UpdateStatus.DowngradeRefused)]
    [InlineData("0.2.0-beta.1", "0.2.0-beta.2", UpdateStatus.Available)]
    [InlineData("0.2.0-beta.2", "0.2.0-beta.10", UpdateStatus.Available)]
    public async Task Pre_release_numbers_compare_as_numbers(string installed, string offered, UpdateStatus expected)
    {
        var rig = new Rig(installed: installed, channel: ReleaseChannels.Beta);
        rig.Publish(offered, channel: ReleaseChannels.Beta);

        Assert.Equal(expected, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_downgrade_downloads_nothing()
    {
        var rig = new Rig(installed: "0.3.0");
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.DowngradeRefused, result.Status);
        Assert.Empty(rig.Source.Opened);
    }

    [Fact]
    public async Task A_release_without_a_tarball_for_this_architecture_is_refused()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", architecture: ReleaseArtifact.ArchLinuxArm64);

        Assert.Equal(UpdateStatus.NoArtifact, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task An_appimage_only_release_is_not_a_tarball_update()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", format: ReleaseArtifact.FormatAppImage);

        Assert.Equal(UpdateStatus.NoArtifact, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    // ---- size bound ----

    [Fact]
    public async Task A_package_over_the_operator_ceiling_is_refused_before_download()
    {
        var rig = new Rig();
        rig.Settings = rig.Settings with { MaxPackageBytes = 10 };
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.SizeExceeded, result.Status);
        Assert.Empty(rig.Source.Opened);
    }

    [Fact]
    public async Task A_download_longer_than_the_declared_size_is_aborted()
    {
        var rig = new Rig();
        var real = TarBuilder.Release("2");
        rig.Publish("0.2.0", tarball: real, declaredSize: real.Length - 5);

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.SizeExceeded, result.Status);
        Assert.Null(rig.Current);
        Assert.Empty(rig.Gpg.Calls);
    }

    [Fact]
    public async Task An_oversized_checksum_file_is_refused()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", shaFileText: new string('a', 5000));

        Assert.Equal(UpdateStatus.SizeExceeded, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_missing_file_is_a_failed_download()
    {
        var rig = new Rig();
        var manifest = rig.Publish("0.2.0");
        rig.Source.Files.Remove(manifest.Artifacts[0].SignatureUrl!);

        Assert.Equal(UpdateStatus.DownloadFailed, (await rig.Updater.ApplyAsync(_ct)).Status);
        Assert.Null(rig.Current);
    }

    // ---- checksum ----

    [Fact]
    public async Task The_inline_hash_and_the_sha256_file_must_agree()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", shaFileText: new string('0', 64) + "  file\n");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.ChecksumMismatch, result.Status);
        Assert.Empty(rig.Gpg.Calls);
    }

    [Fact]
    public async Task Tampered_tarball_bytes_fail_the_checksum()
    {
        var rig = new Rig();
        var real = TarBuilder.Release("0.2.0");
        var tampered = (byte[])real.Clone();
        tampered[real.Length / 2] ^= 0xFF;
        rig.Publish("0.2.0", tarball: real, servedTarball: tampered);

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.ChecksumMismatch, result.Status);
        Assert.Null(rig.Current);
        Assert.Empty(rig.Gpg.Calls);
    }

    [Fact]
    public async Task A_matching_hash_file_in_uppercase_is_accepted()
    {
        var rig = new Rig();
        var tar = TarBuilder.Release("2");
        rig.Publish("0.2.0", tarball: tar, shaFileText: Rig.Hex(tar).ToUpperInvariant() + "  x\n");

        Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    // ---- signature and key pin ----

    [Fact]
    public async Task A_bad_signature_is_refused_and_nothing_is_installed()
    {
        var rig = new Rig();
        rig.Gpg.VerifyExit = 1;
        rig.Gpg.VerifyOutput = "[GNUPG:] BADSIG AAAA Bazaar\n";
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.SignatureInvalid, result.Status);
        Assert.Null(rig.Current);
        Assert.DoesNotContain(rig.Fs.Paths, p => p.StartsWith(Rig.Root + "/releases/0.2.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_signature_from_another_key_is_refused()
    {
        var rig = new Rig();
        string other = new('c', 40);
        rig.Gpg.VerifyOutput = $"[GNUPG:] GOODSIG CCCC Other\n[GNUPG:] VALIDSIG {other} 2026-11-19 1 0 4 0 22 10 00 {other}\n";
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.SignatureInvalid, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_good_exit_code_without_a_goodsig_line_is_not_enough()
    {
        var rig = new Rig();
        rig.Gpg.VerifyOutput = string.Empty;
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.SignatureInvalid, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Theory]
    [InlineData("EXPKEYSIG")]
    [InlineData("REVKEYSIG")]
    [InlineData("ERRSIG")]
    [InlineData("NO_PUBKEY")]
    public async Task An_expired_or_revoked_key_is_refused_even_with_exit_code_zero(string status)
    {
        var rig = new Rig();
        string fp = Rig.Fingerprint.ToUpperInvariant();
        rig.Gpg.VerifyOutput = $"[GNUPG:] GOODSIG AAAA Bazaar\n[GNUPG:] VALIDSIG {fp} 1 1 4 0 22 10 00 {fp}\n[GNUPG:] {status} AAAA\n";
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.SignatureInvalid, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_manifest_naming_another_signing_key_is_refused_before_any_download()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", fingerprint: new string('d', 40));

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.FingerprintMismatch, result.Status);
        Assert.Empty(rig.Source.Opened);
    }

    [Fact]
    public async Task A_key_file_that_is_not_the_pinned_key_is_refused()
    {
        var rig = new Rig();
        rig.Gpg.ListOutput = $"pub:-:255:22:CCCC:1:::-:::scESC:::::ed25519:::0:\nfpr:::::::::{new string('C', 40)}:\n";
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.FingerprintMismatch, result.Status);
        Assert.DoesNotContain(rig.Gpg.Calls, c => c.Arguments.Contains("--verify"));
    }

    [Fact]
    public async Task A_key_file_holding_two_keys_is_refused()
    {
        var rig = new Rig();
        string fp = Rig.Fingerprint.ToUpperInvariant();
        rig.Gpg.ListOutput = $"pub:-:255:22:AAAA:1:::-:::scESC:::::ed25519:::0:\nfpr:::::::::{fp}:\npub:-:255:22:CCCC:1:::-:::scESC:::::ed25519:::0:\nfpr:::::::::{new string('C', 40)}:\n";
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.FingerprintMismatch, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_missing_gpg_is_a_tool_failure()
    {
        var rig = new Rig();
        rig.Gpg.Throws = new IOException("gpg: not found");
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.SignatureToolFailed, result.Status);
        Assert.Null(rig.Current);
    }

    [Fact]
    public async Task A_gpg_timeout_is_a_tool_failure()
    {
        var rig = new Rig();
        rig.Gpg.Throws = new TimeoutException("too slow");
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.SignatureToolFailed, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task Gpg_runs_with_an_isolated_owner_only_home_and_an_argument_list()
    {
        var rig = new Rig();
        rig.Publish("0.2.0");

        await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(3, rig.Gpg.Calls.Count);
        foreach (var call in rig.Gpg.Calls)
        {
            Assert.Equal("gpg", call.FileName);
            int home = call.Arguments.ToList().IndexOf("--homedir");
            Assert.True(home >= 0);
            string homePath = call.Arguments[home + 1];
            Assert.StartsWith(Path.GetTempPath(), homePath, StringComparison.Ordinal);
            Assert.True(homePath.Length < 60, "A short home keeps the gpg-agent socket path under its limit.");
            Assert.Equal(homePath, call.Environment["GNUPGHOME"]);
            Assert.Contains("--batch", call.Arguments);
            Assert.Contains("--no-tty", call.Arguments);
        }

        Assert.Contains(rig.Fs.Log, l => l.StartsWith("mkdir:" + Path.GetTempPath(), StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Fs.Paths, p => p.StartsWith(Path.GetTempPath(), StringComparison.Ordinal)); // The home is removed afterwards.
        Assert.Contains("/etc/bazaar/company-key.asc", rig.Gpg.Calls[0].Arguments);
    }

    // ---- install, atomic switch, previous, rollback ----

    [Fact]
    public async Task A_good_release_is_extracted_and_current_points_at_it()
    {
        var rig = new Rig();
        rig.Publish("0.2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.Installed, result.Status);
        Assert.Equal("0.2.0", result.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero), result.At);
        Assert.Equal("releases/0.2.0", rig.Current);
        Assert.Null(rig.Previous); // First install: nothing to keep.
        Assert.Equal("0.2.0", Encoding.UTF8.GetString(rig.Fs.Content(Rig.Root + "/releases/0.2.0/Bazaar")!));
        Assert.Equal("core.dll", rig.Fs.ReadLink(Rig.Root + "/releases/0.2.0/lib/core-link.dll"));
        Assert.DoesNotContain(rig.Fs.Paths, p => p.StartsWith(Rig.Root + "/staging", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Extracted_modes_keep_the_executable_bit_and_drop_dangerous_bits()
    {
        var rig = new Rig();
        var tar = TarBuilder.Build(
            TarBuilder.File("Bazaar", "bin", (UnixFileMode)0b111_111_111 | UnixFileMode.SetUser | UnixFileMode.SetGroup),
            TarBuilder.File("data.txt", "d", (UnixFileMode)0b110_110_110));
        rig.Publish("0.2.0", tarball: tar);

        await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
            rig.Fs.ModeOf(Rig.Root + "/releases/0.2.0/Bazaar"));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            rig.Fs.ModeOf(Rig.Root + "/releases/0.2.0/data.txt"));
    }

    [Fact]
    public async Task The_switch_is_one_atomic_rename_and_current_is_never_deleted()
    {
        var rig = new Rig();
        rig.Publish("0.2.0");
        await rig.Updater.ApplyAsync(_ct);
        rig.Fs.Log.Clear();
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        rig.Publish("0.3.0");

        await rig.Updater.ApplyAsync(_ct);

        string current = Rig.Root + "/current";
        Assert.DoesNotContain("deletefile:" + current, rig.Fs.Log);
        Assert.DoesNotContain(rig.Fs.Log, l => l.StartsWith("link:" + current + "->", StringComparison.Ordinal));
        Assert.Single(rig.Fs.Log, l => l.EndsWith("->" + current, StringComparison.Ordinal) && l.StartsWith("rename:" + current + ".tmp-", StringComparison.Ordinal));
        Assert.Equal("releases/0.3.0", rig.Current);
        Assert.Equal("releases/0.2.0", rig.Previous);
    }

    [Fact]
    public async Task A_failed_verification_leaves_current_and_previous_untouched()
    {
        var rig = new Rig();
        rig.Publish("0.2.0");
        await rig.Updater.ApplyAsync(_ct);
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        rig.Gpg.VerifyExit = 1;
        rig.Gpg.VerifyOutput = "[GNUPG:] BADSIG AAAA x\n";
        rig.Publish("0.3.0");

        await rig.Updater.ApplyAsync(_ct);

        Assert.Equal("releases/0.2.0", rig.Current);
        Assert.Null(rig.Previous);
        Assert.False(rig.Fs.Exists(Rig.Root + "/releases/0.3.0"));
    }

    [Fact]
    public async Task Only_current_and_previous_are_kept()
    {
        var rig = new Rig();
        foreach (string v in new[] { "0.2.0", "0.3.0", "0.4.0" })
        {
            rig.Publish(v);
            Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
            rig.Settings = rig.Settings with { InstalledVersion = v };
        }

        var releases = rig.Fs.EnumerateDirectories(Rig.Root + "/releases").Select(d => Path.GetFileName(d)!).Order().ToArray();
        Assert.Equal(["0.3.0", "0.4.0"], releases);
    }

    [Fact]
    public async Task Rollback_switches_current_back_and_keeps_the_newer_release_for_rolling_forward()
    {
        var rig = new Rig();
        foreach (string v in new[] { "0.2.0", "0.3.0" })
        {
            rig.Publish(v);
            await rig.Updater.ApplyAsync(_ct);
            rig.Settings = rig.Settings with { InstalledVersion = v };
        }

        var back = rig.Updater.Rollback();

        Assert.Equal(UpdateStatus.RolledBack, back.Status);
        Assert.Equal("0.2.0", back.Version);
        Assert.Equal("releases/0.2.0", rig.Current);
        Assert.Equal("releases/0.3.0", rig.Previous);
        Assert.DoesNotContain("deletefile:" + Rig.Root + "/current", rig.Fs.Log);

        var forward = rig.Updater.Rollback();
        Assert.Equal("0.3.0", forward.Version);
        Assert.Equal("releases/0.3.0", rig.Current);
    }

    [Fact]
    public void Rollback_without_a_previous_release_changes_nothing()
    {
        var rig = new Rig();

        Assert.Equal(UpdateStatus.NothingToRollBack, rig.Updater.Rollback().Status);
    }

    [Fact]
    public void Rollback_refuses_a_previous_link_that_leaves_the_releases_directory()
    {
        var rig = new Rig();
        rig.Fs.CreateSymbolicLink(Rig.Root + "/previous", "../../etc");

        Assert.Equal(UpdateStatus.NothingToRollBack, rig.Updater.Rollback().Status);
        Assert.Null(rig.Current);
    }

    [Fact]
    public async Task Rollback_refuses_when_the_previous_release_directory_is_gone()
    {
        var rig = new Rig();
        rig.Publish("0.2.0");
        await rig.Updater.ApplyAsync(_ct);
        rig.Fs.CreateSymbolicLink(Rig.Root + "/previous", "releases/0.1.0");

        Assert.Equal(UpdateStatus.NothingToRollBack, rig.Updater.Rollback().Status);
        Assert.Equal("releases/0.2.0", rig.Current);
    }

    [Fact]
    public async Task A_fixed_build_with_a_newer_version_installs_after_a_rollback()
    {
        var rig = new Rig();
        foreach (string v in new[] { "0.2.0", "0.3.0" })
        {
            rig.Publish(v);
            await rig.Updater.ApplyAsync(_ct);
            rig.Settings = rig.Settings with { InstalledVersion = v };
        }

        rig.Updater.Rollback();
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        rig.Publish("0.3.1", tarball: TarBuilder.Release("fixed"));

        Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
        Assert.Equal("releases/0.3.1", rig.Current);
        Assert.Equal("releases/0.2.0", rig.Previous);
        Assert.Equal("dll-fixed", Encoding.UTF8.GetString(rig.Fs.Content(Rig.Root + "/releases/0.3.1/lib/core.dll")!));
        Assert.False(rig.Fs.Exists(Rig.Root + "/releases/0.3.0"));
    }

    // ---- settings ----

    [Theory]
    [InlineData("relative/root", "0.1.0", "linux-x64", 40)]
    [InlineData("/opt/bazaar", "v0.1.0", "linux-x64", 40)]
    [InlineData("/opt/bazaar", "0.1.0", "windows-x64", 40)]
    [InlineData("/opt/bazaar", "0.1.0", "linux-x64", 39)]
    public void Invalid_settings_are_refused(string root, string installed, string arch, int fingerprintLength)
    {
        var rig = new Rig();
        var settings = rig.Settings with
        {
            InstallRoot = root,
            InstalledVersion = installed,
            Architecture = arch,
            PinnedKeyFingerprint = new string('a', fingerprintLength),
        };

        Assert.Throws<ArgumentException>(() => new LinuxUpdater(settings, rig.Source, rig.Fs, rig.Gpg, new FixedClock(), rig.Compat));
    }

    [Fact]
    public void An_uppercase_pinned_fingerprint_is_refused()
    {
        var rig = new Rig();
        var settings = rig.Settings with { PinnedKeyFingerprint = new string('A', 40) };

        Assert.Throws<ArgumentException>(() => new LinuxUpdater(settings, rig.Source, rig.Fs, rig.Gpg, new FixedClock(), rig.Compat));
    }

    [Fact]
    public void Defaults_are_the_documented_safe_values()
    {
        var s = new Rig().Settings;

        Assert.Equal(ReleaseChannels.Stable, s.Channel);
        Assert.False(s.IsOwnerInstall);
        Assert.Equal("gpg", s.GpgPath);
        Assert.Equal(TimeSpan.FromSeconds(60), s.GpgTimeout);
        Assert.Equal(ReleaseArtifact.MaxSizeBytes, s.MaxPackageBytes);
        Assert.Equal(4L * 1024 * 1024 * 1024, s.MaxExtractedBytes);
        Assert.Equal(100_000, s.MaxArchiveEntries);
    }
}
