using System.Text;

namespace Bazaar.Client.Updater.Linux.Tests;

public class CompatibilityTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    // server, previous major accepted, running, build min, build max, expected (doc 02 versioning rule)
    [Theory]
    // Accept: the build's majors include the server's major.
    [InlineData("1.0", true, "1.0", "1.0", "1.0", UpdateStatus.Available)]
    [InlineData("1.3", true, "1.0", "1.0", "1.1", UpdateStatus.Available)] // Minors only add: an older minor still talks to a newer server.
    [InlineData("1.0", true, "1.0", "1.0", "1.9", UpdateStatus.Available)] // A newer minor negotiates down.
    [InlineData("2.0", true, "1.5", "1.0", "2.0", UpdateStatus.Available)]
    [InlineData("2.0", false, "1.5", "1.0", "2.0", UpdateStatus.Available)]
    // Accept via the previous major, only while the server still accepts it.
    [InlineData("2.0", true, "1.5", "1.0", "1.9", UpdateStatus.Available)]
    // Upgrade-required: every major the build speaks is below what the server accepts (it would answer 4426).
    [InlineData("2.0", false, "1.0", "1.0", "1.9", UpdateStatus.ProtocolBuildTooOld)]
    [InlineData("3.0", true, "1.0", "1.0", "1.9", UpdateStatus.ProtocolBuildTooOld)]
    // The build needs a newer protocol than the server has: the server must be upgraded first.
    [InlineData("1.9", true, "1.0", "2.0", "2.0", UpdateStatus.ProtocolBuildTooNew)]
    [InlineData("1.0", true, "1.0", "3.0", "3.1", UpdateStatus.ProtocolBuildTooNew)]
    // Refuse-downgrade: the build's highest protocol is below the running client's.
    [InlineData("1.5", true, "1.4", "1.0", "1.3", UpdateStatus.ProtocolDowngradeRefused)]
    [InlineData("2.0", true, "2.0", "1.0", "1.9", UpdateStatus.ProtocolDowngradeRefused)]
    public async Task Protocol_range_is_gated_by_doc_02(string server, bool previous, string running, string min, string max, UpdateStatus expected)
    {
        var rig = new Rig();
        rig.Compat.ServerProtocol = server;
        rig.Compat.ServerAcceptsPreviousMajor = previous;
        rig.Compat.RunningProtocol = running;
        rig.Publish("0.2.0", minProtocol: min, maxProtocol: max);

        Assert.Equal(expected, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_refused_protocol_downloads_and_installs_nothing()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", minProtocol: "2.0", maxProtocol: "2.0");

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.ProtocolBuildTooNew, result.Status);
        Assert.Contains("upgrade the server", result.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Source.Opened);
        Assert.Null(rig.Current);
    }

    [Fact]
    public async Task The_too_old_message_names_the_close_code()
    {
        var rig = new Rig();
        rig.Compat.ServerProtocol = "3.0";
        rig.Compat.ServerAcceptsPreviousMajor = false;
        rig.Compat.RunningProtocol = "1.0";
        rig.Publish("0.2.0");

        var result = await rig.Updater.CheckAsync(_ct);

        Assert.Equal(UpdateStatus.ProtocolBuildTooOld, result.Status);
        Assert.Contains("4426", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2")] // Older.
    [InlineData("3")] // Newer: the server refuses to run on it too (doc 04).
    [InlineData("1.0")]
    public async Task The_schema_must_be_exactly_the_servers(string schema)
    {
        var rig = new Rig();
        rig.Publish("0.2.0", schema: schema);

        var result = await rig.Updater.CheckAsync(_ct);

        Assert.Equal(UpdateStatus.SchemaMismatch, result.Status);
        Assert.Contains(schema, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_matching_schema_is_accepted()
    {
        var rig = new Rig();
        rig.Compat.ServerSchemaVersion = "0044";
        rig.Publish("0.2.0", schema: "0044");

        Assert.Equal(UpdateStatus.Available, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_matching_schema_digest_is_accepted_and_a_different_one_is_a_mismatch()
    {
        const string digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string other = "sha256:fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        var rig = new Rig();
        rig.Compat.ServerSchemaVersion = digest;
        rig.Publish("0.2.0", schema: digest);
        Assert.Equal(UpdateStatus.Available, (await rig.Updater.CheckAsync(_ct)).Status);

        var mismatch = new Rig();
        mismatch.Compat.ServerSchemaVersion = digest;
        mismatch.Publish("0.2.0", schema: other);
        Assert.Equal(UpdateStatus.SchemaMismatch, (await mismatch.Updater.CheckAsync(_ct)).Status);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("one.zero")]
    [InlineData("")]
    public async Task Unknown_or_malformed_protocol_values_fail_closed(string bad)
    {
        var rig = new Rig();
        rig.Compat.ServerProtocol = bad;
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.CompatibilityUnknown, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task An_empty_server_schema_fails_closed()
    {
        var rig = new Rig();
        rig.Compat.ServerSchemaVersion = " ";
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.CompatibilityUnknown, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task Compatibility_is_read_at_check_time_so_a_server_upgrade_is_followed()
    {
        var rig = new Rig();
        rig.Publish("0.2.0", maxProtocol: "1.5");
        var updater = rig.Updater;
        Assert.Equal(UpdateStatus.Available, (await updater.CheckAsync(_ct)).Status);

        rig.Compat.ServerProtocol = "3.0";

        Assert.Equal(UpdateStatus.ProtocolBuildTooOld, (await updater.CheckAsync(_ct)).Status);
    }
}

public class SkipVersionTests
{
    private static readonly CancellationToken _ct = CancellationToken.None;

    private static async Task<Rig> TwoInstallsThenRollbackAsync()
    {
        var rig = new Rig();
        foreach (string v in new[] { "0.2.0", "0.3.0" })
        {
            rig.Publish(v);
            Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
            rig.Settings = rig.Settings with { InstalledVersion = v };
        }

        Assert.Equal(UpdateStatus.RolledBack, rig.Updater.Rollback().Status);
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        return rig;
    }

    [Fact]
    public async Task Skip_versions_defaults_to_empty()
    {
        var rig = new Rig();
        Assert.Empty(rig.Settings.SkipVersions);
        rig.Publish("0.2.0");

        Assert.Equal(UpdateStatus.Available, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_listed_version_is_neither_offered_nor_installed_but_a_newer_one_is()
    {
        var rig = new Rig();
        rig.Settings = rig.Settings with { SkipVersions = ["0.2.0"] };
        rig.Publish("0.2.0");

        var skipped = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.VersionSkipped, skipped.Status);
        Assert.Empty(rig.Source.Opened);
        rig.Publish("0.2.1");
        Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_listed_version_matches_ignoring_build_metadata()
    {
        var rig = new Rig();
        rig.Settings = rig.Settings with { SkipVersions = ["0.2.0+abc"] };
        rig.Publish("0.2.0+def");

        Assert.Equal(UpdateStatus.VersionSkipped, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public void A_malformed_skip_entry_is_refused()
    {
        var rig = new Rig();
        var settings = rig.Settings with { SkipVersions = ["not-a-version"] };

        Assert.Throws<ArgumentException>(() => new LinuxUpdater(settings, rig.Source, rig.Fs, rig.Gpg, new FixedClock(), rig.Compat));
    }

    [Fact]
    public async Task After_a_rollback_the_version_rolled_back_from_is_not_offered_again()
    {
        var rig = await TwoInstallsThenRollbackAsync();
        rig.Publish("0.3.0");
        rig.Source.Opened.Clear();

        var result = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.VersionSkipped, result.Status);
        Assert.Equal("releases/0.2.0", rig.Current);
        Assert.Empty(rig.Source.Opened);
    }

    [Fact]
    public async Task A_version_between_the_installed_and_the_rolled_back_one_is_also_refused()
    {
        var rig = await TwoInstallsThenRollbackAsync();
        rig.Publish("0.2.5");

        Assert.Equal(UpdateStatus.VersionSkipped, (await rig.Updater.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task A_newer_version_is_accepted_after_a_rollback()
    {
        var rig = await TwoInstallsThenRollbackAsync();
        rig.Publish("0.3.1");

        Assert.Equal(UpdateStatus.Installed, (await rig.Updater.ApplyAsync(_ct)).Status);
        Assert.Equal("releases/0.3.1", rig.Current);
    }

    [Fact]
    public async Task The_record_is_a_small_owner_only_file_written_by_rename_and_survives_a_restart()
    {
        var rig = await TwoInstallsThenRollbackAsync();

        string path = Rig.Root + "/rolled-back.json";
        Assert.Equal("{\"version\":\"0.3.0\"}", Encoding.UTF8.GetString(rig.Fs.Content(path)!));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, rig.Fs.ModeOf(path));
        Assert.Contains(rig.Fs.Log, l => l.StartsWith("rename:" + path + ".tmp-", StringComparison.Ordinal) && l.EndsWith("->" + path, StringComparison.Ordinal));

        // A new updater instance (a restart) over the same disk still refuses.
        rig.Publish("0.3.0");
        var restarted = new LinuxUpdater(rig.Settings, rig.Source, rig.Fs, rig.Gpg, new FixedClock(), rig.Compat);
        Assert.Equal(UpdateStatus.VersionSkipped, (await restarted.CheckAsync(_ct)).Status);
    }

    [Fact]
    public async Task Rolling_back_again_keeps_the_highest_version_recorded()
    {
        var rig = await TwoInstallsThenRollbackAsync();

        rig.Updater.Rollback(); // Roll forward to 0.3.0, rolling back from 0.2.0.

        Assert.Equal("{\"version\":\"0.3.0\"}", Encoding.UTF8.GetString(rig.Fs.Content(Rig.Root + "/rolled-back.json")!));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":5}")]
    [InlineData("{\"version\":\"banana\"}")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task A_corrupt_record_fails_closed_with_a_clear_error(string content)
    {
        var rig = new Rig();
        rig.Publish("0.2.0");
        await using (var stream = rig.Fs.CreateFile(Rig.Root + "/rolled-back.json"))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content), _ct);
        }

        var check = await rig.Updater.CheckAsync(_ct);
        var apply = await rig.Updater.ApplyAsync(_ct);

        Assert.Equal(UpdateStatus.SkipRecordInvalid, check.Status);
        Assert.Equal(UpdateStatus.SkipRecordInvalid, apply.Status);
        Assert.Contains("rolled-back.json", apply.Detail, StringComparison.Ordinal);
        Assert.Equal(0, rig.Source.ManifestReads);
        Assert.Null(rig.Current);
    }

    [Fact]
    public async Task A_rollback_replaces_a_corrupt_record_so_the_install_can_recover()
    {
        var rig = new Rig();
        foreach (string v in new[] { "0.2.0", "0.3.0" })
        {
            rig.Publish(v);
            await rig.Updater.ApplyAsync(_ct);
            rig.Settings = rig.Settings with { InstalledVersion = v };
        }

        await using (var stream = rig.Fs.CreateFile(Rig.Root + "/rolled-back.json"))
        {
            await stream.WriteAsync("garbage"u8.ToArray(), _ct);
        }

        Assert.Equal(UpdateStatus.RolledBack, rig.Updater.Rollback().Status);
        rig.Settings = rig.Settings with { InstalledVersion = "0.2.0" };
        rig.Publish("0.3.1");
        Assert.Equal(UpdateStatus.Available, (await rig.Updater.CheckAsync(_ct)).Status);
    }
}
