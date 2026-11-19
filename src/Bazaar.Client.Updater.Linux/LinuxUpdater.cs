using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Contracts.Releases;

namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// The Linux in-place updater (doc 30 sections 3 to 7). Order of checks: channel, manifest, downgrade, artifact, size, pinned
/// key; then download, SHA-256 (inline value, <c>.sha256</c> file and the bytes must agree), detached signature, safe
/// extraction, and one atomic switch of <c>current</c>. The previous release stays for <see cref="Rollback"/>.
/// Layout under <see cref="LinuxUpdaterSettings.InstallRoot"/>: <c>releases/&lt;version&gt;</c>, symlinks <c>current</c> and
/// <c>previous</c> (relative targets), and a <c>staging</c> directory that is emptied after every run. One updater runs at a
/// time per install root; the head owns that guarantee.
/// </summary>
public sealed class LinuxUpdater : ILinuxUpdater
{
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const int MaxChecksumFileBytes = 1024;
    private const int MaxSignatureFileBytes = 64 * 1024;
    private const string ReleasesDir = "releases";

    private readonly LinuxUpdaterSettings _settings;
    private readonly IReleaseSource _source;
    private readonly IUpdateFileSystem _fs;
    private readonly IProcessRunner _runner;
    private readonly TimeProvider _clock;

    /// <summary>Creates the updater.</summary>
    /// <exception cref="ArgumentException">A setting is missing or malformed.</exception>
    public LinuxUpdater(LinuxUpdaterSettings settings, IReleaseSource source, IUpdateFileSystem fs, IProcessRunner runner, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(clock);
        Validate(settings);
        _settings = settings;
        _source = source;
        _fs = fs;
        _runner = runner;
        _clock = clock;
    }

    private string Root => _settings.InstallRoot;

    private string CurrentLink => Path.Combine(Root, "current");

    private string PreviousLink => Path.Combine(Root, "previous");

    /// <inheritdoc />
    public async Task<UpdateResult> CheckAsync(CancellationToken ct) => (await PlanAsync(ct).ConfigureAwait(false)).Result;

    /// <inheritdoc />
    public async Task<UpdateResult> ApplyAsync(CancellationToken ct)
    {
        var plan = await PlanAsync(ct).ConfigureAwait(false);
        if (plan.Artifact is null || plan.Manifest is null || plan.Result.Status != UpdateStatus.Available)
        {
            return plan.Result;
        }

        var manifest = plan.Manifest;
        var artifact = plan.Artifact;
        string staging = Path.Combine(Root, "staging");
        string work = Path.Combine(staging, Guid.NewGuid().ToString("N"));
        string? partial = null;
        try
        {
            _fs.DeleteDirectory(staging); // Leftovers of a crashed run.
            _fs.CreateDirectory(work, PrivateDirectory);

            string tarball = Path.Combine(work, "package.tar.gz");
            string checksum = Path.Combine(work, "package.sha256");
            string signature = Path.Combine(work, "package.asc");

            var failure = await DownloadAsync(artifact.Url, tarball, artifact.SizeBytes, ct).ConfigureAwait(false)
                ?? await DownloadAsync(artifact.Sha256Url, checksum, MaxChecksumFileBytes, ct).ConfigureAwait(false)
                ?? await DownloadAsync(artifact.SignatureUrl, signature, MaxSignatureFileBytes, ct).ConfigureAwait(false)
                ?? await CheckHashAsync(artifact, tarball, checksum, ct).ConfigureAwait(false)
                ?? await new GpgSignatureVerifier(_settings, _runner, _fs).VerifyAsync(tarball, signature, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                return Result(failure.Status, manifest.AppVersion, failure.Detail);
            }

            string releases = Path.Combine(Root, ReleasesDir);
            _fs.CreateDirectory(releases, PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            partial = Path.Combine(releases, ".partial-" + Guid.NewGuid().ToString("N"));
            try
            {
                var extractor = new SafeTarGzExtractor(_fs, _settings.MaxExtractedBytes, _settings.MaxArchiveEntries);
                await using var archive = _fs.OpenRead(tarball);
                await extractor.ExtractAsync(archive, partial, ct).ConfigureAwait(false);
            }
            catch (ArchiveRejectedException e)
            {
                return Result(UpdateStatus.ExtractionRejected, manifest.AppVersion, e.Message);
            }

            string release = Path.Combine(releases, manifest.AppVersion);
            if (_fs.DirectoryExists(release))
            {
                // A release dir left by an earlier install or rollback of this very version; current and previous never point at a newer one.
                _fs.DeleteDirectory(release);
            }

            _fs.MoveDirectory(partial, release);
            partial = null;

            string? oldTarget = _fs.ReadLink(CurrentLink);
            if (oldTarget is not null)
            {
                PointLink(PreviousLink, oldTarget);
            }

            PointLink(CurrentLink, ReleasesDir + "/" + manifest.AppVersion);
            Prune();
            return Result(UpdateStatus.Installed, manifest.AppVersion, "Installed.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result(UpdateStatus.InstallFailed, manifest.AppVersion, e.Message);
        }
        finally
        {
            if (partial is not null)
            {
                _fs.DeleteDirectory(partial);
            }

            _fs.DeleteDirectory(staging);
        }
    }

    /// <inheritdoc />
    public UpdateResult Rollback()
    {
        try
        {
            string? previous = _fs.ReadLink(PreviousLink);
            string? current = _fs.ReadLink(CurrentLink);
            if (previous is null || !TryVersionOf(previous, out string? version) || !_fs.DirectoryExists(Path.Combine(Root, previous)))
            {
                return Result(UpdateStatus.NothingToRollBack, null, "There is no previous release.");
            }

            PointLink(CurrentLink, previous);
            if (current is not null)
            {
                PointLink(PreviousLink, current);
            }
            else
            {
                _fs.DeleteFile(PreviousLink);
            }

            return Result(UpdateStatus.RolledBack, version, "Rolled back.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Result(UpdateStatus.InstallFailed, null, e.Message);
        }
    }

    private sealed record Plan(UpdateResult Result, ReleaseManifest? Manifest = null, ReleaseArtifact? Artifact = null);

    private async Task<Plan> PlanAsync(CancellationToken ct)
    {
        if (!ReleaseChannels.IsKnown(_settings.Channel))
        {
            return new Plan(Result(UpdateStatus.ChannelRefused, null, $"'{_settings.Channel}' is not a release channel."));
        }

        if (_settings.Channel == ReleaseChannels.LatestCommit && !_settings.IsOwnerInstall)
        {
            return new Plan(Result(UpdateStatus.ChannelRefused, null, "Latest-Commit is owner-only."));
        }

        ReleaseManifest? manifest;
        try
        {
            manifest = await _source.GetManifestAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or JsonException or ArgumentException)
        {
            return new Plan(Result(UpdateStatus.NoManifest, null, "The feed could not be read: " + e.Message));
        }

        if (manifest is null)
        {
            return new Plan(Result(UpdateStatus.NoManifest, null, "The server offers no release."));
        }

        string version = manifest.AppVersion;
        if (manifest.Channel != _settings.Channel)
        {
            return new Plan(Result(UpdateStatus.ChannelMismatch, version, $"The release is on {manifest.Channel}, this install follows {_settings.Channel}."));
        }

        int order = SemVerPrecedence.Compare(version, _settings.InstalledVersion);
        if (order < 0)
        {
            return new Plan(Result(UpdateStatus.DowngradeRefused, version, $"{version} is older than {_settings.InstalledVersion}."));
        }

        if (order == 0)
        {
            return new Plan(Result(UpdateStatus.UpToDate, version, "Already on this version."));
        }

        var artifact = manifest.Find(_settings.Architecture, ReleaseArtifact.FormatTarGz);
        if (artifact is null)
        {
            return new Plan(Result(UpdateStatus.NoArtifact, version, $"No tar.gz for {_settings.Architecture}."));
        }

        if (artifact.SizeBytes > _settings.MaxPackageBytes)
        {
            return new Plan(Result(UpdateStatus.SizeExceeded, version, $"The package ({artifact.SizeBytes} bytes) exceeds the limit of {_settings.MaxPackageBytes}."));
        }

        if (manifest.SigningKeyFingerprint != _settings.PinnedKeyFingerprint)
        {
            return new Plan(Result(UpdateStatus.FingerprintMismatch, version, "The release is signed by a key this build does not pin."));
        }

        return new Plan(Result(UpdateStatus.Available, version, "An update is available."), manifest, artifact);
    }

    /// <summary>Downloads one file into the staging area, never more than <paramref name="maxBytes"/>.</summary>
    private async Task<UpdateFailure?> DownloadAsync(string url, string path, long maxBytes, CancellationToken ct)
    {
        try
        {
            await using var input = await _source.OpenAsync(url, ct).ConfigureAwait(false);
            await using var output = _fs.CreateFile(path);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return new UpdateFailure(UpdateStatus.SizeExceeded, $"A download exceeded its limit of {maxBytes} bytes.");
                }

                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return null;
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return new UpdateFailure(UpdateStatus.DownloadFailed, e.Message);
        }
    }

    private async Task<UpdateFailure?> CheckHashAsync(ReleaseArtifact artifact, string tarball, string checksumFile, CancellationToken ct)
    {
        string fileText;
        await using (var stream = _fs.OpenRead(checksumFile))
        using (var reader = new StreamReader(stream))
        {
            fileText = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
        }

        // "sha256sum" format: "<hex>  <name>". Only the hash matters.
        string fromFile = fileText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;
        if (fromFile != artifact.Sha256)
        {
            return new UpdateFailure(UpdateStatus.ChecksumMismatch, "The .sha256 file does not match the manifest.");
        }

        string actual;
        await using (var stream = _fs.OpenRead(tarball))
        {
            actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        }

        return actual == artifact.Sha256
            ? null
            : new UpdateFailure(UpdateStatus.ChecksumMismatch, "The downloaded package does not match its SHA-256.");
    }

    /// <summary>Points <paramref name="link"/> at <paramref name="target"/> by creating a temporary link and renaming it over the old one.</summary>
    private void PointLink(string link, string target)
    {
        string temp = link + ".tmp-" + Guid.NewGuid().ToString("N");
        _fs.CreateSymbolicLink(temp, target);
        try
        {
            _fs.RenameOver(temp, link);
        }
        catch
        {
            _fs.DeleteFile(temp);
            throw;
        }
    }

    /// <summary>Keeps the releases that <c>current</c> and <c>previous</c> name and removes the rest, including partial extractions.</summary>
    private void Prune()
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (string link in new[] { CurrentLink, PreviousLink })
        {
            if (_fs.ReadLink(link) is { } target && TryVersionOf(target, out string? version))
            {
                keep.Add(version);
            }
        }

        foreach (string dir in _fs.EnumerateDirectories(Path.Combine(Root, ReleasesDir)))
        {
            if (!keep.Contains(Path.GetFileName(dir)))
            {
                _fs.DeleteDirectory(dir);
            }
        }
    }

    /// <summary>Accepts only <c>releases/&lt;version&gt;</c>, so a tampered link cannot make the updater act outside the releases directory.</summary>
    private static bool TryVersionOf(string linkTarget, [NotNullWhen(true)] out string? version)
    {
        version = null;
        string prefix = ReleasesDir + "/";
        if (!linkTarget.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string name = linkTarget[prefix.Length..];
        if (!SemVerPrecedence.TryParse(name, out _, out _))
        {
            return false;
        }

        version = name;
        return true;
    }

    private UpdateResult Result(UpdateStatus status, string? version, string detail) =>
        new(status, version, detail, _clock.GetUtcNow());

    private static void Validate(LinuxUpdaterSettings s)
    {
        if (string.IsNullOrEmpty(s.InstallRoot) || !Path.IsPathRooted(s.InstallRoot))
        {
            throw new ArgumentException("InstallRoot must be an absolute path.", nameof(s));
        }

        if (!SemVerPrecedence.TryParse(s.InstalledVersion, out _, out _))
        {
            throw new ArgumentException("InstalledVersion must be a SemVer version.", nameof(s));
        }

        if (s.Architecture is not (ReleaseArtifact.ArchLinuxX64 or ReleaseArtifact.ArchLinuxArm64))
        {
            throw new ArgumentException("Architecture must be linux-x64 or linux-arm64.", nameof(s));
        }

        if (s.PinnedKeyFingerprint is not { Length: 40 or 64 } fingerprint || !fingerprint.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("PinnedKeyFingerprint must be 40 or 64 lowercase hex characters.", nameof(s));
        }

        if (string.IsNullOrWhiteSpace(s.PinnedKeyPath) || string.IsNullOrWhiteSpace(s.GpgPath))
        {
            throw new ArgumentException("PinnedKeyPath and GpgPath are required.", nameof(s));
        }

        if (s.MaxPackageBytes < 1 || s.MaxExtractedBytes < 1 || s.MaxArchiveEntries < 1 || s.GpgTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("Limits and the gpg timeout must be positive.", nameof(s));
        }
    }
}
