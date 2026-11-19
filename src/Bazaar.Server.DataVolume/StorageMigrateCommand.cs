using System.Globalization;
using System.Security.Cryptography;
using Bazaar.Server.Identity.Kek;

namespace Bazaar.Server.DataVolume;

/// <summary>
/// <c>bazaar-server storage migrate</c> (doc 63 section 10, "Existing installs"): copies a plain data root into a new
/// encrypted volume with verification. Owner-started and resumable through <c>&lt;boot root&gt;/volume/migrate.json</c>. The old
/// folder is only read, never written or removed: the secure removal of the plain copy is doc 61's step
/// (<see cref="IPlainCopyRemover"/>) and is not part of this command. It does not edit configuration; it prints the values to set.
/// </summary>
internal static class StorageMigrateCommand
{
    private static readonly string[] _phaseNames = ["preconditions", "KEK copy", "volume creation", "volume creation", "copy", "verify"];

    public static async Task<int> RunAsync(
        StorageArguments args,
        DataVolumeOptions options,
        List<IDataVolumeOperator> operators,
        List<IDataVolumeProvider> providers,
        Func<IKekProvider> createOrOpenKek,
        StorageMigrateServices services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var from = Path.GetFullPath(args.From!);
        var journalPath = MigrateJournalStore.PathFor(options);

        try
        {
            if (!Path.IsPathFullyQualified(args.From!))
                throw new MigrateRefusedException("--from must be an absolute path.");

            var journal = await ReadJournalAsync(journalPath, from, options, cancellationToken);
            var phase = journal?.Phase ?? MigratePhase.Started;
            await PreconditionsAsync(from, options, services, cancellationToken);
            var plan = StorageCommand.BuildPlan(args, options, operators, providers, resuming: journal is not null);
            if (journal is not null)
                plan = plan with { InstallId = journal.InstallId, InstallIdGenerated = false };

            // Phases up to the copy read the old root; after it the manifest stands in for it.
            var scan = journal is null || phase < MigratePhase.Copied ? await Task.Run(() => MigrateCopier.Scan(from, cancellationToken), cancellationToken) : null;
            if (scan is not null)
                CheckScan(plan, scan);

            if (args.DryRun)
            {
                await PrintDryRunAsync(from, plan, options, scan, journal, output);
                return 0;
            }

            return await RunPhasesAsync(from, plan, options, journal, scan, createOrOpenKek, services, journalPath, output, cancellationToken);
        }
        catch (MigrateRefusedException e)
        {
            await output.WriteLineAsync($"storage migrate refused: {StorageCommand.OneLine(e.Message)}", CancellationToken.None);
            await output.WriteLineAsync($"the old data root '{from}' was not touched.", CancellationToken.None);
            return StorageCommand.FailedExitCode;
        }
    }

    private static async Task<int> RunPhasesAsync(
        string from, InitPlan plan, DataVolumeOptions options, MigrateJournal? journal, MigrateScan? scan, Func<IKekProvider> createOrOpenKek,
        StorageMigrateServices services, string journalPath, TextWriter output, CancellationToken cancellationToken)
    {
        var state = new VolumeCreationState();
        var phase = journal?.Phase ?? MigratePhase.Started;
        IKekProvider? kek = null;
        try
        {
            if (journal is null)
            {
                var now = DateTimeOffset.UtcNow;
                journal = new MigrateJournal(
                    MigrateJournalStore.FormatVersion, from, options.BootRoot, options.DataRoot, plan.InstallId, VolumeId: null,
                    plan.ProviderKind, plan.ContainerKind, plan.ContainerPath, MigratePhase.Started, now, now);
                await MigrateJournalStore.WriteAsync(journalPath, journal, cancellationToken);
                await output.WriteLineAsync($"storage migrate: journal written at '{journalPath}'", CancellationToken.None);
            }
            else
            {
                await output.WriteLineAsync($"storage migrate: resuming after '{phase}' (journal '{journalPath}')", CancellationToken.None);
            }

            async Task Advance(MigratePhase next, string? volumeId = null)
            {
                journal = journal! with { Phase = next, VolumeId = volumeId ?? journal!.VolumeId, Updated = DateTimeOffset.UtcNow };
                await MigrateJournalStore.WriteAsync(journalPath, journal, cancellationToken);
                phase = next;
            }

            if (phase < MigratePhase.KekCopied)
            {
                services.Checkpoint?.Invoke("start:kek");
                await CopyKekAsync(from, options, output, cancellationToken);
                kek = StorageCommand.OpenKek(createOrOpenKek);
                await Advance(MigratePhase.KekCopied);
                services.Checkpoint?.Invoke("done:kek");
            }

            kek ??= StorageCommand.OpenKek(createOrOpenKek);

            if (phase < MigratePhase.VolumeCreated)
            {
                services.Checkpoint?.Invoke("start:volume");
                if (phase == MigratePhase.VolumeCreating)
                {
                    // A previous run reached the operator. With a descriptor the volume is whole; without one the key is gone.
                    var existing = await DescriptorStore.ReadAsync(options.DescriptorPath, cancellationToken)
                        ?? throw new MigrateRefusedException(
                            $"An earlier run started creating the volume and did not write its descriptor, so the volume key is lost. A {plan.ContainerKind} container may exist at '{plan.ContainerPath}'. Nothing was deleted: remove the container by hand once you are sure it holds no data, delete '{journalPath}', and run again.");
                    await StorageCommand.VerifyGuardAsync(plan, options, kek, existing.VolumeId, cancellationToken);
                    await Advance(MigratePhase.VolumeCreated, existing.VolumeId);
                }
                else
                {
                    await StorageCommand.CreateVolumeAsync(
                        plan, options, kek, state,
                        beforeOperatorCreate: ct => ToValueTask(Advance(MigratePhase.VolumeCreating), ct),
                        cancellationToken);
                    await Advance(MigratePhase.VolumeCreated, state.VolumeId);
                    await output.WriteLineAsync($"storage migrate: volume {state.VolumeId} created, descriptor and marker written", CancellationToken.None);
                }

                services.Checkpoint?.Invoke("done:volume");
            }

            var volumeId = journal!.VolumeId ?? throw new InvalidOperationException("The journal has no volume id.");

            if (phase < MigratePhase.Copied)
            {
                services.Checkpoint?.Invoke("start:copy");

                // Never write into a mount point that is not the volume: that would put plain data on the boot disk.
                await StorageCommand.VerifyGuardAsync(plan, options, kek, volumeId, cancellationToken);
                await output.WriteLineAsync($"storage migrate: copying {scan!.Files.Count} files ({FormatBytes(scan.TotalBytes)}) to '{options.DataRoot}'", CancellationToken.None);
                await MigrateCopier.CopyAsync(from, options.DataRoot, scan, cancellationToken);
                await Advance(MigratePhase.Copied);
                services.Checkpoint?.Invoke("done:copy");
            }

            string checksumSummary = string.Empty;
            if (phase < MigratePhase.Verified)
            {
                services.Checkpoint?.Invoke("start:verify");
                await StorageCommand.VerifyGuardAsync(plan, options, kek, volumeId, cancellationToken);
                var problems = await MigrateCopier.VerifyAsync(options.DataRoot, cancellationToken);
                if (problems.Count > 0)
                {
                    foreach (var problem in problems.Take(20))
                        await output.WriteLineAsync($"verify: {problem}", CancellationToken.None);

                    if (problems.Count > 20)
                        await output.WriteLineAsync($"verify: and {problems.Count - 20} more", CancellationToken.None);

                    throw new InvalidDataException($"The copy does not match the manifest ({problems.Count} problems).");
                }

                await output.WriteLineAsync("storage migrate: file hashes and file set verified", CancellationToken.None);
                checksumSummary = await VerifyClustersAsync(options.DataRoot, services, output, cancellationToken);
                await Advance(MigratePhase.Verified);
                services.Checkpoint?.Invoke("done:verify");
            }

            await ReportVerifiedAsync(from, plan, options, volumeId, checksumSummary, output);
            return 0;
        }
        catch (MigrateRefusedException e)
        {
            await output.WriteLineAsync($"storage migrate refused: {StorageCommand.OneLine(e.Message)}", CancellationToken.None);
            await ReportStateAsync(from, plan, options, journal, state, phase, output);
            return StorageCommand.FailedExitCode;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await output.WriteLineAsync($"storage migrate failed: {e.GetType().Name}: {StorageCommand.OneLine(e.Message)}", CancellationToken.None);
            await ReportStateAsync(from, plan, options, journal, state, phase, output);
            return StorageCommand.FailedExitCode;
        }
        finally
        {
            (kek as IDisposable)?.Dispose();
        }
    }

    /// <summary>Copies the legacy KEK byte for byte to the boot root. Never generates one, never replaces a different one, leaves the legacy file in place.</summary>
    private static async Task CopyKekAsync(string from, DataVolumeOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        var legacy = Path.Combine(from, "keys", "kek.bin");
        var target = StorageCommand.KekPath(options);
        byte[]? source = null;
        byte[]? existing = null;
        byte[]? copy = null;
        try
        {
            try
            {
                source = await File.ReadAllBytesAsync(legacy, cancellationToken);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new MigrateRefusedException($"The legacy KEK '{legacy}' does not exist. Existing wrapped keys depend on it, so no new one is generated; a fresh install uses storage init.");
            }

            if (source.Length == 0)
                throw new MigrateRefusedException($"The legacy KEK '{legacy}' is empty.");

            if (File.Exists(target))
            {
                existing = await File.ReadAllBytesAsync(target, cancellationToken);
                if (!CryptographicOperations.FixedTimeEquals(existing, source))
                    throw new MigrateRefusedException($"The boot root already holds a different KEK at '{target}'. It is never replaced.");

                await output.WriteLineAsync("storage migrate: the legacy KEK is already at the boot root, identical", CancellationToken.None);
                return;
            }

            await AtomicFile.WriteAsync(target, source, overwrite: false, cancellationToken);
            copy = await File.ReadAllBytesAsync(target, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(copy, source))
                throw new InvalidDataException($"The KEK copy at '{target}' does not match the legacy file. The legacy file is untouched; remove the copy and run again.");

            await output.WriteLineAsync($"storage migrate: legacy KEK copied to '{target}' and verified byte for byte; '{legacy}' stays in place", CancellationToken.None);
        }
        finally
        {
            foreach (var buffer in new[] { source, existing, copy })
            {
                if (buffer is not null)
                    CryptographicOperations.ZeroMemory(buffer);
            }
        }
    }

    private static async Task<string> VerifyClustersAsync(string dataRoot, StorageMigrateServices services, TextWriter output, CancellationToken cancellationToken)
    {
        var pgRoot = Path.Combine(dataRoot, "pg");
        var clusters = Directory.Exists(pgRoot)
            ? Directory.EnumerateDirectories(pgRoot).Select(d => Path.Combine(d, "data")).Where(d => File.Exists(Path.Combine(d, "PG_VERSION"))).Order(StringComparer.Ordinal).ToList()
            : [];
        if (clusters.Count == 0)
        {
            await output.WriteLineAsync("storage migrate: no PostgreSQL cluster found under pg/<major>/data; pg_checksums did not run", CancellationToken.None);
            return "no cluster found";
        }

        var allPassed = true;
        foreach (var cluster in clusters)
        {
            var result = await services.ChecksumVerifier.CheckAsync(cluster, cancellationToken);
            if (result.Outcome == PostgresChecksumOutcome.Failed)
                throw new InvalidDataException($"pg_checksums failed on '{cluster}': {result.Detail}");

            allPassed &= result.Outcome == PostgresChecksumOutcome.Passed;
            var line = result.Outcome == PostgresChecksumOutcome.Passed
                ? $"pg_checksums passed on '{cluster}'"
                : $"NOT verified by pg_checksums: '{cluster}' ({result.Detail}). The file hashes above are the only integrity check for it.";
            await output.WriteLineAsync($"storage migrate: {line}", CancellationToken.None);
        }

        return allPassed ? "passed" : "not verified by pg_checksums";
    }

    private static async Task ReportVerifiedAsync(string from, InitPlan plan, DataVolumeOptions options, string volumeId, string checksumSummary, TextWriter output)
    {
        await output.WriteLineAsync("storage migrate: switch the host to the new volume by setting these values (this command edits no configuration):", CancellationToken.None);
        await output.WriteLineAsync($"  {DataVolumeOptions.BootRootKey}={options.BootRoot}", CancellationToken.None);
        await output.WriteLineAsync($"  {DataVolumeOptions.DataRootKey}={options.DataRoot}", CancellationToken.None);
        await output.WriteLineAsync($"  {DataVolumeOptions.InstallIdKey}={plan.InstallId}", CancellationToken.None);
        await output.WriteLineAsync($"  {DataVolumeOptions.VolumeIdKey}={volumeId}", CancellationToken.None);
        await output.WriteLineAsync("then start the host. The legacy KEK file and the whole old data root are still in place; this command does not remove them.", CancellationToken.None);
        if (checksumSummary.Length > 0)
            await output.WriteLineAsync($"storage migrate: pg_checksums: {checksumSummary}", CancellationToken.None);

        await output.WriteLineAsync($"storage migrate: verified, plain copy kept at {from}", CancellationToken.None);
    }

    /// <summary>States how far the run got and what exists. Nothing is deleted.</summary>
    private static async Task ReportStateAsync(
        string from, InitPlan plan, DataVolumeOptions options, MigrateJournal? journal, VolumeCreationState state, MigratePhase phase, TextWriter output)
    {
        var next = Math.Min((int)phase + 1, _phaseNames.Length - 1);
        await output.WriteLineAsync($"last completed phase: {phase}; the failure is in or just after: {_phaseNames[next]}", CancellationToken.None);
        await output.WriteLineAsync($"the old data root '{from}' was not touched.", CancellationToken.None);
        await output.WriteLineAsync($"journal: {(journal is null ? "not written" : $"'{MigrateJournalStore.PathFor(options)}'")}", CancellationToken.None);
        await output.WriteLineAsync($"boot root KEK: {(File.Exists(StorageCommand.KekPath(options)) ? "present" : "absent")}", CancellationToken.None);
        await output.WriteLineAsync($"descriptor: {(File.Exists(options.DescriptorPath) ? "written" : "not written")}; marker: {(File.Exists(options.MarkerPath) ? "written" : "not written")}", CancellationToken.None);
        if (!plan.Plain && (state.ContainerMayExist || journal?.Phase >= MigratePhase.VolumeCreating))
            await output.WriteLineAsync($"a {plan.ContainerKind} container may exist at '{plan.ContainerPath}' and may be mounted at '{options.DataRoot}'. It is kept.", CancellationToken.None);

        await output.WriteLineAsync("run the same command again to resume after the last completed phase.", CancellationToken.None);
    }

    private static async Task PreconditionsAsync(string from, DataVolumeOptions options, StorageMigrateServices services, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(from))
            throw new MigrateRefusedException($"The old data root '{from}' does not exist.");

        if (StorageCommand.IsInside(from, options.DataRoot) || StorageCommand.IsInside(options.DataRoot, from))
            throw new MigrateRefusedException("The new mount point and the old data root must differ and neither may contain the other.");

        if (StorageCommand.IsInside(from, options.BootRoot) || StorageCommand.IsInside(options.BootRoot, from))
            throw new MigrateRefusedException("The boot root and the old data root must not contain one another: the plain copy is removed later and must not take the boot root with it.");

        if (File.Exists(Path.Combine(from, VolumeMarker.FileName)))
            throw new MigrateRefusedException($"'{from}' carries a volume marker: it already is a data volume, not a plain data root.");

        var inUse = await services.HostProbe.FindInUseAsync(from, cancellationToken);
        if (inUse is not null)
            throw new MigrateRefusedException($"The host is still running on the old data root: {inUse}");
    }

    private static void CheckScan(InitPlan plan, MigrateScan scan)
    {
        if (scan.Refusals.Count > 0)
            throw new MigrateRefusedException($"The old data root cannot be copied as it stands: {string.Join(" ", scan.Refusals.Take(10))}{(scan.Refusals.Count > 10 ? $" ({scan.Refusals.Count - 10} more)" : string.Empty)}");

        if (!plan.Plain && plan.ContainerKind == "file" && plan.MaxSizeBytes < scan.TotalBytes)
            throw new MigrateRefusedException($"The old data root holds {FormatBytes(scan.TotalBytes)}, more than the container's maximum of {FormatBytes(plan.MaxSizeBytes)}. Raise --max-gb.");
    }

    private static async Task<MigrateJournal?> ReadJournalAsync(string path, string from, DataVolumeOptions options, CancellationToken cancellationToken)
    {
        var journal = await MigrateJournalStore.ReadAsync(path, cancellationToken);
        if (journal is null)
            return null;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(journal.From, from, comparison)
            || !string.Equals(journal.DataRoot, options.DataRoot, comparison)
            || !string.Equals(journal.BootRoot, options.BootRoot, comparison)
            || (!string.IsNullOrWhiteSpace(options.InstallId) && !string.Equals(journal.InstallId, options.InstallId, StringComparison.Ordinal)))
        {
            throw new DataVolumeRefusedException(
                DataVolumeRefusal.AlreadyInitialised,
                $"A migration journal at '{path}' belongs to a different migration (from '{journal.From}' to '{journal.DataRoot}'). Finish that one or remove the journal by hand.");
        }

        return journal;
    }

    private static async Task PrintDryRunAsync(string from, InitPlan plan, DataVolumeOptions options, MigrateScan? scan, MigrateJournal? journal, TextWriter output)
    {
        await output.WriteLineAsync("storage migrate dry run: nothing is created", CancellationToken.None);
        await output.WriteLineAsync($"old data root (read only): {from}", CancellationToken.None);
        await output.WriteLineAsync($"provider: {plan.ProviderKind}", CancellationToken.None);
        await output.WriteLineAsync($"boot root: {options.BootRoot}", CancellationToken.None);
        await output.WriteLineAsync($"data root (mount point): {options.DataRoot}", CancellationToken.None);
        await output.WriteLineAsync(
            plan.Plain
                ? "container: none (plain folder, no encryption)"
                : $"container: {plan.ContainerKind} at {plan.ContainerPath}, {(plan.ContainerKind == "file" ? $"max {FormatBytes(plan.MaxSizeBytes)}" : "the device")}",
            CancellationToken.None);
        await output.WriteLineAsync($"journal: {MigrateJournalStore.PathFor(options)} ({(journal is null ? "will be created" : $"exists, last completed phase {journal.Phase}")})", CancellationToken.None);
        await output.WriteLineAsync($"legacy KEK: {Path.Combine(from, "keys", "kek.bin")} -> {StorageCommand.KekPath(options)} (copied, not regenerated; the legacy file stays)", CancellationToken.None);
        if (scan is not null)
        {
            await output.WriteLineAsync($"to copy: {scan.Files.Count} files, {scan.Directories.Count} folders, {FormatBytes(scan.TotalBytes)}", CancellationToken.None);
            var tops = scan.Files
                .GroupBy(f => f.RelativePath.Contains('/') ? f.RelativePath[..f.RelativePath.IndexOf('/', StringComparison.Ordinal)] : f.RelativePath + " (file)")
                .Select(g => (Name: g.Key, Bytes: g.Sum(f => f.Length)))
                .OrderByDescending(t => t.Bytes).Take(10);
            foreach (var (name, bytes) in tops)
                await output.WriteLineAsync($"  {name}: {FormatBytes(bytes)}", CancellationToken.None);
        }

        await output.WriteLineAsync("then: verify (file hashes, pg_checksums, guard), print the configuration values to set. The plain copy is kept.", CancellationToken.None);
    }

    private static async ValueTask ToValueTask(Task task, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await task;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " bytes",
    };
}

/// <summary>A migration precondition failed. Nothing was created or changed.</summary>
internal sealed class MigrateRefusedException(string message) : Exception(message);
