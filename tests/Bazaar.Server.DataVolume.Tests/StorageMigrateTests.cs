using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.Identity.Kek;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.DataVolume.Tests;

/// <summary><c>storage migrate</c> (doc 63 section 10, "Existing installs"): copy with verification, resumable, the old folder never touched.</summary>
public sealed class StorageMigrateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bazaar-sm-tests-" + Guid.CreateVersion7().ToString("N"));
    private readonly string _installId = Guid.CreateVersion7().ToString();
    private readonly string _volumeId = Guid.CreateVersion7().ToString();
    private readonly string _old;
    private readonly string _bootRoot;
    private readonly string _dataRoot;
    private readonly string _container;
    private readonly MigrateOperator _operator;
    private readonly FakeDataVolumeProvider _provider;
    private readonly FakeProbe _probe = new();
    private readonly FakeChecksums _checksums = new();
    private readonly StringWriter _output = new();

    public StorageMigrateTests()
    {
        _old = Path.Combine(_root, "old");
        _bootRoot = Path.Combine(_root, "boot");
        _dataRoot = Path.Combine(_root, "data");
        _container = Path.Combine(_root, "container.img");
        _operator = new MigrateOperator(FakeDataVolumeProvider.ProviderKind, _volumeId, _dataRoot);
        _provider = new FakeDataVolumeProvider(_volumeId);
        BuildOldRoot();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string LegacyKek => Path.Combine(_old, "keys", "kek.bin");

    private string BootKek => Path.Combine(_bootRoot, "keys", "kek.bin");

    private string DescriptorPath => Path.Combine(_bootRoot, "volume", "descriptor.json");

    private string JournalPath => Path.Combine(_bootRoot, "volume", "migrate.json");

    private void BuildOldRoot()
    {
        // The legacy KEK exactly as an old install made it: a file KEK under <old root>/keys.
        FileKekProvider.Open(LegacyKek, KekOpenMode.CreateIfMissing).Dispose();
        var data = Path.Combine(_old, "pg", "18", "data");
        Directory.CreateDirectory(Path.Combine(data, "base", "1"));
        File.WriteAllText(Path.Combine(data, "PG_VERSION"), "18\n");
        File.WriteAllBytes(Path.Combine(data, "base", "1", "1234"), RandomNumberGenerator.GetBytes(300_000));
        Directory.CreateDirectory(Path.Combine(_old, "blobs", "ab"));
        File.WriteAllBytes(Path.Combine(_old, "blobs", "ab", "cd.bin"), RandomNumberGenerator.GetBytes(5000));
        File.WriteAllBytes(Path.Combine(_old, "empty.dat"), []);
        Directory.CreateDirectory(Path.Combine(_old, "logs"));
        File.SetLastWriteTimeUtc(Path.Combine(_old, "blobs", "ab", "cd.bin"), new DateTime(2024, 3, 4, 5, 6, 7, DateTimeKind.Utc));
    }

    private IConfiguration Config(string? dataRoot = null, string? installId = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DataVolumeOptions.BootRootKey] = _bootRoot,
            [DataVolumeOptions.DataRootKey] = dataRoot ?? _dataRoot,
            [DataVolumeOptions.InstallIdKey] = installId ?? _installId,
            [DataVolumeOptions.InstanceClassKey] = "Production",
            [KekComponent.KekProviderKey] = "file",
        }).Build();

    private Task<int> RunAsync(string[] args, Action<string>? checkpoint = null, IConfiguration? config = null) =>
        StorageCommand.RunAsync(
            args,
            config ?? Config(),
            [_operator],
            [_provider, new DevPlainDataVolumeProvider()],
            () => KekProviderFactory.Create("file", _bootRoot, KekOpenMode.CreateIfMissing),
            new StorageMigrateServices(_probe, _checksums) { Checkpoint = checkpoint },
            _output,
            Ct);

    private string[] MigrateArgs(params string[] extra) =>
        ["migrate", "--from", _old, "--provider", FakeDataVolumeProvider.ProviderKind, "--container", _container, "--max-gb", "4", "--service-account", "svc", .. extra];

    private static Dictionary<string, string> Snapshot(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Walk(DirectoryInfo directory)
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, entry.FullName);
                if (entry.LinkTarget is not null)
                {
                    map[relative] = "link:" + entry.LinkTarget;
                }
                else if (entry is DirectoryInfo child)
                {
                    map[relative] = "dir:" + child.LastWriteTimeUtc.Ticks;
                    Walk(child);
                }
                else
                {
                    var bytes = File.ReadAllBytes(entry.FullName);
                    map[relative] = $"file:{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}:{entry.LastWriteTimeUtc.Ticks}";
                }
            }
        }

        Walk(new DirectoryInfo(root));
        return map;
    }

    private void AssertNoKeyInOutput()
    {
        var text = _output.ToString();
        var legacy = File.ReadAllBytes(LegacyKek);
        foreach (var key in new[] { _operator.KeyCopy, legacy })
        {
            if (key.Length == 0)
                continue;

            Assert.DoesNotContain(Convert.ToHexString(key), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Convert.ToBase64String(key), text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Success_copies_everything_verifies_it_and_leaves_the_old_root_alone()
    {
        var before = Snapshot(_old);

        var code = await RunAsync(MigrateArgs());

        Assert.Equal(0, code);
        Assert.Equal(1, _operator.CreateCalls);
        Assert.Equal(before, Snapshot(_old));

        foreach (var relative in new[] { "pg/18/data/PG_VERSION", "pg/18/data/base/1/1234", "blobs/ab/cd.bin", "empty.dat" })
        {
            var native = relative.Replace('/', Path.DirectorySeparatorChar);
            Assert.Equal(File.ReadAllBytes(Path.Combine(_old, native)), File.ReadAllBytes(Path.Combine(_dataRoot, native)));
        }

        Assert.True(Directory.Exists(Path.Combine(_dataRoot, "logs")));
        Assert.False(Directory.Exists(Path.Combine(_dataRoot, "keys")));
        Assert.Equal(File.ReadAllBytes(LegacyKek), File.ReadAllBytes(BootKek));
        Assert.True(File.Exists(LegacyKek));
        Assert.True(File.Exists(DescriptorPath));
        Assert.True(File.Exists(Path.Combine(_dataRoot, VolumeMarker.FileName)));
        Assert.Equal([Path.Combine(_dataRoot, "pg", "18", "data")], _checksums.Clusters);

        var text = _output.ToString();
        Assert.Contains($"{DataVolumeOptions.BootRootKey}={_bootRoot}", text);
        Assert.Contains($"{DataVolumeOptions.DataRootKey}={_dataRoot}", text);
        Assert.Contains($"{DataVolumeOptions.InstallIdKey}={_installId}", text);
        Assert.Contains($"{DataVolumeOptions.VolumeIdKey}={_volumeId}", text);
        Assert.Contains($"storage migrate: verified, plain copy kept at {_old}", text.TrimEnd().Split('\n')[^1]);
        AssertNoKeyInOutput();
    }

    [Fact]
    public async Task Timestamps_and_unix_modes_are_preserved()
    {
        var script = Path.Combine(_old, "blobs", "ab", "cd.bin");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);

        Assert.Equal(0, await RunAsync(MigrateArgs()));

        var copy = Path.Combine(_dataRoot, "blobs", "ab", "cd.bin");
        Assert.Equal(File.GetLastWriteTimeUtc(script), File.GetLastWriteTimeUtc(copy));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(File.GetUnixFileMode(script), File.GetUnixFileMode(copy));
    }

    [Fact]
    public async Task The_legacy_kek_is_copied_not_regenerated_and_a_value_wrapped_before_still_unwraps()
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var aad = Encoding.UTF8.GetBytes("migrate-test");
        byte[] wrapped;
        using (var legacy = FileKekProvider.Open(LegacyKek, KekOpenMode.OpenExisting))
            wrapped = await legacy.WrapAsync(dek, aad, Ct);

        Assert.Equal(0, await RunAsync(MigrateArgs()));

        Assert.Equal(File.ReadAllBytes(LegacyKek), File.ReadAllBytes(BootKek));
        using var moved = FileKekProvider.Open(BootKek, KekOpenMode.OpenExisting);
        Assert.Equal(dek, await moved.UnwrapAsync(wrapped, aad, Ct));
    }

    [Theory]
    [InlineData("start:kek")]
    [InlineData("done:kek")]
    [InlineData("start:volume")]
    [InlineData("done:volume")]
    [InlineData("start:copy")]
    [InlineData("done:copy")]
    [InlineData("start:verify")]
    [InlineData("done:verify")]
    public async Task A_run_that_dies_at_any_phase_resumes_without_repeating_the_volume_or_the_kek(string crashAt)
    {
        var before = Snapshot(_old);

        var crashed = await RunAsync(MigrateArgs(), checkpoint: name =>
        {
            if (name == crashAt)
                throw new InvalidOperationException("simulated crash at " + name);
        });
        Assert.Equal(StorageCommand.FailedExitCode, crashed);
        Assert.Contains("simulated crash", _output.ToString());
        Assert.Equal(before, Snapshot(_old));

        var descriptorBefore = File.Exists(DescriptorPath) ? File.ReadAllBytes(DescriptorPath) : null;
        var kekBefore = File.Exists(BootKek) ? File.ReadAllBytes(BootKek) : null;

        Assert.Equal(0, await RunAsync(MigrateArgs()));

        Assert.Equal(1, _operator.CreateCalls);
        if (descriptorBefore is not null)
            Assert.Equal(descriptorBefore, File.ReadAllBytes(DescriptorPath));
        if (kekBefore is not null)
            Assert.Equal(kekBefore, File.ReadAllBytes(BootKek));

        Assert.Equal(File.ReadAllBytes(LegacyKek), File.ReadAllBytes(BootKek));
        Assert.Equal(File.ReadAllBytes(Path.Combine(_old, "blobs", "ab", "cd.bin")), File.ReadAllBytes(Path.Combine(_dataRoot, "blobs", "ab", "cd.bin")));
        Assert.Equal(before, Snapshot(_old));
        Assert.Contains("verified, plain copy kept", _output.ToString());
    }

    [Fact]
    public async Task A_guard_failure_after_the_volume_exists_resumes_with_the_same_volume()
    {
        _provider.Status = _provider.Status with { Encrypted = false };
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.Equal(1, _operator.CreateCalls);
        Assert.True(File.Exists(DescriptorPath));
        Assert.False(File.Exists(Path.Combine(_dataRoot, "pg", "18", "data", "PG_VERSION")));

        _provider.Status = _provider.Status with { Encrypted = true };
        Assert.Equal(0, await RunAsync(MigrateArgs()));
        Assert.Equal(1, _operator.CreateCalls);
    }

    [Fact]
    public async Task An_operator_failure_keeps_the_container_and_a_rerun_refuses_to_create_another()
    {
        var before = Snapshot(_old);
        _operator.Failure = new IOException("device busy");

        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.True(File.Exists(_container));
        Assert.Contains("may exist", _output.ToString());

        _operator.Failure = null;
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.Equal(1, _operator.CreateCalls);
        Assert.Contains("volume key is lost", _output.ToString());
        Assert.True(File.Exists(_container));
        Assert.Equal(before, Snapshot(_old));
    }

    [Fact]
    public async Task A_corrupted_destination_file_fails_verification_and_the_source_is_untouched()
    {
        var before = Snapshot(_old);
        _checksums.Outcome = PostgresChecksumOutcome.Failed;
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        var copy = Path.Combine(_dataRoot, "blobs", "ab", "cd.bin");
        var bytes = File.ReadAllBytes(copy);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(copy, bytes);

        _checksums.Outcome = PostgresChecksumOutcome.Passed;
        _output.GetStringBuilder().Clear();
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        var text = _output.ToString();
        Assert.Contains("hash differs: blobs/ab/cd.bin", text);
        Assert.DoesNotContain("verified, plain copy kept", text);
        Assert.Equal(before, Snapshot(_old));
    }

    [Fact]
    public async Task An_extra_file_in_the_copy_fails_verification()
    {
        _checksums.Outcome = PostgresChecksumOutcome.Failed;
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        File.WriteAllText(Path.Combine(_dataRoot, "stray.txt"), "x");

        _output.GetStringBuilder().Clear();
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.Contains("unexpected file: stray.txt", _output.ToString());
    }

    [Fact]
    public async Task A_symlink_that_leaves_the_old_root_is_refused_before_anything_is_created()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_old, "pg_tblspc"), outside);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip("This OS account cannot create symbolic links.");
            return;
        }

        var before = Snapshot(_old);
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        Assert.Contains("leaves the old root", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
        Assert.False(Directory.Exists(_bootRoot));
        Assert.False(Directory.Exists(_dataRoot));
        Assert.Equal(before, Snapshot(_old));
        Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
    }

    [Fact]
    public async Task A_running_host_is_refused_and_nothing_is_created()
    {
        var before = Snapshot(_old);
        _probe.Reason = "postmaster.pid is held";

        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        Assert.Contains("postmaster.pid is held", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
        Assert.False(Directory.Exists(_bootRoot));
        Assert.False(Directory.Exists(_dataRoot));
        Assert.Equal(before, Snapshot(_old));
    }

    [Fact]
    public async Task The_shipped_probe_refuses_while_a_postmaster_pid_exists()
    {
        var probe = new PostmasterPidHostRunningProbe();
        Assert.Null(await probe.FindInUseAsync(_old, Ct));

        File.WriteAllText(Path.Combine(_old, "pg", "18", "data", "postmaster.pid"), "123\n");
        Assert.Contains("postmaster.pid", await probe.FindInUseAsync(_old, Ct));
    }

    [Fact]
    public async Task Dry_run_creates_nothing_and_prints_the_plan_with_sizes()
    {
        var before = Snapshot(_old);

        Assert.Equal(0, await RunAsync(MigrateArgs("--dry-run")));

        Assert.Equal(0, _operator.CreateCalls);
        Assert.False(Directory.Exists(_bootRoot));
        Assert.False(Directory.Exists(_dataRoot));
        Assert.False(File.Exists(_container));
        Assert.Equal(before, Snapshot(_old));

        var text = _output.ToString();
        Assert.Contains("nothing is created", text);
        Assert.Contains("to copy: 4 files", text);
        Assert.Contains("KB", text);
        Assert.Contains(_container, text);
        Assert.DoesNotContain("verified, plain copy kept", text);
    }

    [Fact]
    public async Task A_disabled_checksum_cluster_is_reported_as_not_verified_by_pg_checksums()
    {
        _checksums.Outcome = PostgresChecksumOutcome.ChecksumsDisabled;

        Assert.Equal(0, await RunAsync(MigrateArgs()));

        var text = _output.ToString();
        Assert.Contains("NOT verified by pg_checksums", text);
        Assert.DoesNotContain("pg_checksums passed", text);
        Assert.Contains("verified, plain copy kept", text);
    }

    [Fact]
    public async Task A_failing_pg_checksums_run_stops_before_verified_and_resumes_after_it_is_fixed()
    {
        _checksums.Outcome = PostgresChecksumOutcome.Failed;
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.DoesNotContain("verified, plain copy kept", _output.ToString());
        Assert.Contains("last completed phase: Copied", _output.ToString());

        _checksums.Outcome = PostgresChecksumOutcome.Passed;
        Assert.Equal(0, await RunAsync(MigrateArgs()));
        Assert.Equal(1, _operator.CreateCalls);
    }

    [Fact]
    public async Task A_second_run_after_success_changes_nothing()
    {
        Assert.Equal(0, await RunAsync(MigrateArgs()));
        var after = Snapshot(_dataRoot);

        Assert.Equal(0, await RunAsync(MigrateArgs()));

        Assert.Equal(1, _operator.CreateCalls);
        Assert.Equal(after, Snapshot(_dataRoot));
    }

    [Fact]
    public async Task A_missing_legacy_kek_is_refused_and_no_new_kek_is_made()
    {
        File.Delete(LegacyKek);

        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        Assert.False(File.Exists(BootKek));
        Assert.Equal(0, _operator.CreateCalls);
        Assert.Contains("no new one is generated", _output.ToString());
    }

    [Fact]
    public async Task A_different_kek_already_at_the_boot_root_is_never_replaced()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BootKek)!);
        var other = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(BootKek, other);

        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        Assert.Equal(other, File.ReadAllBytes(BootKek));
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task Extra_files_in_the_keys_folder_are_refused_rather_than_left_behind()
    {
        File.WriteAllText(Path.Combine(_old, "keys", "notes.txt"), "x");

        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.Contains("besides kek.bin", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task An_old_root_that_is_already_a_volume_or_overlaps_the_mount_is_refused()
    {
        File.WriteAllText(Path.Combine(_old, VolumeMarker.FileName), "x");
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));
        Assert.Contains("already is a data volume", _output.ToString());
        File.Delete(Path.Combine(_old, VolumeMarker.FileName));

        _output.GetStringBuilder().Clear();
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs(), config: Config(dataRoot: _old)));
        Assert.Contains("must differ", _output.ToString());

        _output.GetStringBuilder().Clear();
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs(), config: Config(dataRoot: Path.Combine(_old, "new"))));
        Assert.Contains("must differ", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task A_journal_from_another_migration_is_not_resumed()
    {
        _checksums.Outcome = PostgresChecksumOutcome.Failed;
        Assert.Equal(StorageCommand.FailedExitCode, await RunAsync(MigrateArgs()));

        var other = Path.Combine(_root, "old2");
        Directory.CreateDirectory(other);
        Assert.Equal(DataVolumeRefusedException.ExitCode, await RunAsync(["migrate", "--from", other, "--provider", FakeDataVolumeProvider.ProviderKind, "--container", _container, "--max-gb", "4", "--service-account", "svc"]));
        Assert.Contains("different migration", _output.ToString());
        Assert.Equal(1, _operator.CreateCalls);
    }

    [Fact]
    public async Task Usage_errors_and_a_missing_from_are_reported()
    {
        Assert.Equal(VolumeCommand.UsageExitCode, await RunAsync(["migrate", "--provider", FakeDataVolumeProvider.ProviderKind]));
        Assert.Contains("--from is required", _output.ToString());

        Assert.Equal(VolumeCommand.UsageExitCode, await RunAsync(["init", "--from", _old, "--provider", FakeDataVolumeProvider.ProviderKind]));
        Assert.Contains("unknown option '--from'", _output.ToString());
        Assert.Equal(0, _operator.CreateCalls);
    }

    [Fact]
    public async Task The_pg_checksums_process_verifier_maps_exit_codes()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake pg_checksums is a shell script.");
            return;
        }

        var cluster = Path.Combine(_old, "pg", "18", "data");
        var bundle = Path.Combine(_root, "bundle");
        var bin = Path.Combine(bundle, "18", "bin");
        Directory.CreateDirectory(bin);
        var tool = Path.Combine(bin, "pg_checksums");

        Assert.Equal(PostgresChecksumOutcome.ToolUnavailable, (await new PgChecksumsProcessVerifier(null).CheckAsync(cluster, Ct)).Outcome);
        Assert.Equal(PostgresChecksumOutcome.ToolUnavailable, (await new PgChecksumsProcessVerifier(bundle).CheckAsync(cluster, Ct)).Outcome);

        var verifier = new PgChecksumsProcessVerifier(bundle);
        await WriteToolAsync(tool, "exit 0");
        Assert.Equal(PostgresChecksumOutcome.Passed, (await verifier.CheckAsync(cluster, Ct)).Outcome);

        await WriteToolAsync(tool, "echo 'pg_checksums: error: data checksums are not enabled in cluster' >&2; exit 1");
        Assert.Equal(PostgresChecksumOutcome.ChecksumsDisabled, (await verifier.CheckAsync(cluster, Ct)).Outcome);

        await WriteToolAsync(tool, "echo 'checksum verification failed in file base/1/1234' >&2; exit 1");
        var failed = await verifier.CheckAsync(cluster, Ct);
        Assert.Equal(PostgresChecksumOutcome.Failed, failed.Outcome);
        Assert.Contains("base/1/1234", failed.Detail);
    }

    private static async Task WriteToolAsync(string path, string body)
    {
        await File.WriteAllTextAsync(path, "#!/bin/sh\n" + body + "\n", Ct);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class FakeProbe : IHostRunningProbe
    {
        public string? Reason { get; set; }

        public ValueTask<string?> FindInUseAsync(string oldDataRoot, CancellationToken cancellationToken) => ValueTask.FromResult(Reason);
    }

    private sealed class FakeChecksums : IPostgresChecksumVerifier
    {
        public PostgresChecksumOutcome Outcome { get; set; } = PostgresChecksumOutcome.Passed;

        public List<string> Clusters { get; } = [];

        public ValueTask<PostgresChecksumResult> CheckAsync(string clusterDirectory, CancellationToken cancellationToken)
        {
            Clusters.Add(clusterDirectory);
            return ValueTask.FromResult(new PostgresChecksumResult(Outcome, Outcome == PostgresChecksumOutcome.Failed ? "page checksum mismatch" : "fake"));
        }
    }

    private sealed class MigrateOperator(string kind, string volumeId, string dataRoot) : IDataVolumeOperator
    {
        public string Kind => kind;

        public int CreateCalls { get; private set; }

        public byte[] KeyCopy { get; private set; } = [];

        public Exception? Failure { get; set; }

        public ValueTask<string> CreateAsync(DataVolumeCreateRequest request, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken)
        {
            CreateCalls++;
            KeyCopy = volumeKey.ToArray();
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(request.ContainerPath, "container");
            return Failure is null ? ValueTask.FromResult(volumeId) : ValueTask.FromException<string>(Failure);
        }

        public ValueTask OpenAsync(DataVolumeDescriptor descriptor, string mountPath, ReadOnlyMemory<byte> volumeKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask CloseAsync(DataVolumeDescriptor descriptor, string mountPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
