using Bazaar.Contracts.Backup;
using Bazaar.Server.Components.Backup.Engines;
using Bazaar.Server.Components.Backup.Jobs;
using Bazaar.Server.Components.Backup.Offsite;
using Bazaar.Server.Components.Backup.Processes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Bazaar.Server.Components.Backup.Tests;

/// <summary>Offsite composition (doc 33, "Offsite composition [PROPOSED]"): target choice, upload after backup, live cookie flag, failures.</summary>
public sealed class OffsiteCompositionTests
{
    private const string _resticOk = "{\"message_type\":\"summary\",\"snapshot_id\":\"snap1\"}\n";

    private static (BaseBackupJob Job, InMemoryBackupRunLog Log, FakeProcessRunner Restic, MutableOptions Options) Build(
        string setsRoot,
        BackupOffsiteSettings settings,
        Func<string, IReadOnlyList<string>, ProcessResult>? respond = null,
        IOffsiteCredentialSource? credentials = null,
        int attempts = 3)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 18, 0, 0, 0, TimeSpan.Zero));
        var log = new InMemoryBackupRunLog();
        var restic = new FakeProcessRunner(respond ?? ((_, _) => new ProcessResult(0, _resticOk, string.Empty)));
        var options = new MutableOptions(settings);
        var resolver = new BackupTargetResolver(options, restic, credentials ?? new UnavailableOffsiteCredentialSource(), new ServiceCollection().BuildServiceProvider(), time);
        var job = new BaseBackupJob(new SetEngine(time), setsRoot, 2, time, log, offsite: resolver, offsiteAttempts: attempts, offsiteRetryDelay: TimeSpan.Zero);
        return (job, log, restic, options);
    }

    private static readonly BackupOffsiteSettings _s3 = new(OffsiteTargetKind.ResticS3, "restic", "s3:https://example.invalid/bucket");

    [Fact]
    public async Task None_target_uploads_nothing_and_records_no_offsite_run()
    {
        using var dir = new TempDir();
        var (job, log, restic, _) = Build(dir.Path, new BackupOffsiteSettings());

        var run = await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupState.Succeeded, run.State);
        Assert.Empty(restic.Calls);
        Assert.DoesNotContain(log.Recent(10), r => r.Kind == BackupKind.Offsite);
    }

    [Fact]
    public async Task No_op_target_reports_none_and_lists_nothing()
    {
        var target = new NoOpBackupTarget();

        var snapshot = await target.PutDirectoryAsync("/does/not/exist", ["t"], TestContext.Current.CancellationToken);

        Assert.Equal("none", target.Name);
        Assert.Equal(string.Empty, snapshot.SnapshotId);
        Assert.Empty(await target.ListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Restic_target_is_called_after_a_base_backup_and_recorded()
    {
        using var dir = new TempDir();
        var (job, log, restic, _) = Build(dir.Path, _s3);

        var run = await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupState.Succeeded, run.State);
        var call = Assert.Single(restic.Calls);
        Assert.Equal("restic", call.File);
        Assert.Contains("backup", call.Args);
        Assert.Contains(call.Args, a => a.StartsWith(dir.Path, StringComparison.Ordinal) && a.Contains("set-", StringComparison.Ordinal));
        Assert.Contains("--tag=base-backup", call.Args);
        var offsite = Assert.Single(log.Recent(10), r => r.Kind == BackupKind.Offsite);
        Assert.Equal(BackupState.Succeeded, offsite.State);
        Assert.Contains("snap1", offsite.Detail);
        Assert.Contains("restic-s3", offsite.Detail);
    }

    [Fact]
    public async Task Cookie_flag_flip_between_two_uploads_changes_the_restic_arguments()
    {
        using var dir = new TempDir();
        var (job, _, restic, options) = Build(dir.Path, _s3);

        await job.RunAsync(TestContext.Current.CancellationToken);
        options.Current = _s3 with { IncludeCookieProfiles = true };
        await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, restic.Calls.Count);
        Assert.Contains("--exclude=*.session.json", restic.Calls[0].Args);
        Assert.DoesNotContain(restic.Calls[1].Args, a => a.StartsWith("--exclude=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_failure_keeps_the_local_backup_and_is_recorded_after_the_attempts()
    {
        using var dir = new TempDir();
        var (job, log, restic, _) = Build(dir.Path, _s3, (_, _) => new ProcessResult(1, string.Empty, "repository unreachable"), attempts: 3);

        var run = await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(BackupState.Succeeded, run.State);
        Assert.Single(BaseBackupJob.DiscoverSets(dir.Path));
        Assert.Equal(3, restic.Calls.Count);
        var offsite = Assert.Single(log.Recent(10), r => r.Kind == BackupKind.Offsite);
        Assert.Equal(BackupState.Failed, offsite.State);
        Assert.Contains("ProcessFailedException", offsite.Detail);
    }

    [Fact]
    public async Task Upload_that_succeeds_on_a_retry_is_recorded_as_succeeded()
    {
        using var dir = new TempDir();
        var calls = 0;
        var (job, log, restic, _) = Build(dir.Path, _s3, (_, _) => ++calls == 1 ? new ProcessResult(1, string.Empty, "blip") : new ProcessResult(0, _resticOk, string.Empty));

        await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, restic.Calls.Count);
        Assert.Equal(BackupState.Succeeded, Assert.Single(log.Recent(10), r => r.Kind == BackupKind.Offsite).State);
    }

    [Fact]
    public async Task Missing_repository_fails_the_upload_without_calling_restic()
    {
        using var dir = new TempDir();
        var (job, log, restic, _) = Build(dir.Path, new BackupOffsiteSettings(OffsiteTargetKind.ResticB2), attempts: 1);

        await job.RunAsync(TestContext.Current.CancellationToken);

        Assert.Empty(restic.Calls);
        Assert.Equal(BackupState.Failed, Assert.Single(log.Recent(10), r => r.Kind == BackupKind.Offsite).State);
    }

    [Fact]
    public async Task Credentials_reach_restic_through_the_environment_only()
    {
        using var dir = new TempDir();
        var settings = _s3 with { CredentialsRef = "env:OFFSITE" };
        var (job, _, restic, _) = Build(dir.Path, settings, credentials: new FixedCredentials("# c\nRESTIC_PASSWORD=pw-value\nAWS_ACCESS_KEY_ID=key-id\n"));

        await job.RunAsync(TestContext.Current.CancellationToken);

        var call = Assert.Single(restic.Calls);
        Assert.Equal("pw-value", call.Env!["RESTIC_PASSWORD"]);
        Assert.DoesNotContain(call.Args, a => a.Contains("pw-value", StringComparison.Ordinal) || a.Contains("key-id", StringComparison.Ordinal));
    }

    [Fact]
    public void Credentials_with_a_name_outside_the_allow_list_are_refused_without_echoing_the_value()
    {
        var ex = Assert.Throws<FormatException>(() => OffsiteCredentialParser.Parse("LD_PRELOAD=/tmp/secret-value.so"));

        Assert.DoesNotContain("secret-value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Custom_target_uses_the_keyed_service_and_fails_without_one()
    {
        var time = TimeProvider.System;
        var settings = new MutableOptions(new BackupOffsiteSettings(OffsiteTargetKind.Custom));
        var none = new BackupTargetResolver(settings, new FakeProcessRunner(), new UnavailableOffsiteCredentialSource(), new ServiceCollection().BuildServiceProvider(), time);
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.ResolveAsync(TestContext.Current.CancellationToken));

        var custom = new NoOpBackupTarget();
        var provider = new ServiceCollection().AddKeyedSingleton<IBackupTarget>(BackupTargetResolver.CustomTargetKey, custom).BuildServiceProvider();
        var resolved = await new BackupTargetResolver(settings, new FakeProcessRunner(), new UnavailableOffsiteCredentialSource(), provider, time).ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Same(custom, resolved);
    }

    [Fact]
    public async Task Component_registers_the_resolver_with_a_none_default()
    {
        using var dataSource = Npgsql.NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=bazaar");
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddSingleton(TimeProvider.System);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{BaseBackupJobOptions.SectionName}:Enabled"] = "true",
                [$"{BaseBackupJobOptions.SectionName}:SetsRoot"] = "/var/lib/bazaar/backups",
                [$"{BaseBackupJobOptions.SectionName}:PgBinDirectory"] = "/usr/lib/postgresql/18/bin",
            })
            .Build();
        new BackupComponent().AddServices(services, configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        var target = await provider.GetRequiredService<IBackupTargetResolver>().ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(NoOpBackupTarget.TargetName, target.Name);
    }

    private sealed class MutableOptions(BackupOffsiteSettings initial) : IBackupOffsiteOptions
    {
        public BackupOffsiteSettings Current { get; set; } = initial;

        public Task<BackupOffsiteSettings> GetAsync(CancellationToken ct) => Task.FromResult(Current);
    }

    private sealed class FixedCredentials(string text) : IOffsiteCredentialSource
    {
        public Task<string> ReadAsync(string reference, CancellationToken ct) => Task.FromResult(text);
    }

    private sealed class SetEngine(TimeProvider time) : IBackupEngine
    {
        private int _seq;

        public string Name => "fake";

        public Task<BackupSet> TakeBaseBackupAsync(string destinationDirectory, CancellationToken ct)
        {
            Directory.CreateDirectory(destinationDirectory);
            var wal = (++_seq).ToString("X24", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(destinationDirectory, "backup_label"), $"START WAL LOCATION: 0/1 (file {wal})\n");
            File.WriteAllBytes(Path.Combine(destinationDirectory, "base.bin"), new byte[64]);
            return Task.FromResult(new BackupSet("fake-" + wal, destinationDirectory, time.GetUtcNow(), 64, wal));
        }
    }
}
