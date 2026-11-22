using System.Security.Cryptography;
using System.Text;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Paths;

namespace Bazaar.Server.VmMounts.Profiles;

/// <summary>
/// Copies the cookie profile into the guest and reads it back over <see cref="IMountGuestChannel"/>, file by file. Push hashes
/// every file on the host and the guest verifies it. Pull treats the guest as hostile: every listed path must be a safe relative
/// path, nothing may alias another file, the counts and sizes stay within <see cref="ProfileLimits"/>, the content policy must
/// accept each file, and every file's bytes must match the length and SHA-256 the guest listed. Anything else rejects the whole
/// pull and removes the staging directory, so a profile is written back completely or not at all.
/// </summary>
/// <remarks>
/// Validation is structural only (see <see cref="IProfileTransfer"/>): it cannot judge what the cookie database holds, and a hash the
/// guest itself supplied proves consistency, not honesty. Names from the guest are cleaned (<see cref="SafeText"/>) before they reach
/// a message, an audit row or a result.
/// </remarks>
public sealed class GuestProfileTransfer(IMountGuestChannel channel, IMountContentPolicy policy, VmMountOptions options) : IProfileTransfer
{
    public async Task<ProfileManifest> PushAsync(
        Guid vmId, GuestOs guestOs, string hostProfileDirectory, string guestProfileDirectory, CancellationToken ct)
    {
        var limits = options.ProfileLimits;
        var files = new List<(string Relative, string Path, long Length)>();
        foreach (var path in Directory.EnumerateFiles(hostProfileDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(hostProfileDirectory, path).Replace('\\', '/');
            if (HostPathGuard.IsLink(path))
                throw Invalid($"The host profile holds a link: '{SafeText.Clean(relative)}'.");
            files.Add((relative, path, new FileInfo(path).Length));
        }

        // A link to a directory is not returned by EnumerateFiles as a file; refuse those too.
        foreach (var dir in Directory.EnumerateDirectories(hostProfileDirectory, "*", SearchOption.AllDirectories))
        {
            if (HostPathGuard.IsLink(dir))
                throw Invalid($"The host profile holds a link: '{SafeText.Clean(Path.GetRelativePath(hostProfileDirectory, dir))}'.");
        }

        ValidateList(files.Select(f => (f.Relative, f.Length)), limits);
        foreach (var f in files)
            RequireAllowed(f.Relative, f.Length);

        var manifest = new List<ProfileFile>();
        try
        {
            await channel.DeleteDirectoryAsync(vmId, guestProfileDirectory, ct).ConfigureAwait(false);
            foreach (var f in files.OrderBy(x => x.Relative, StringComparer.Ordinal))
            {
                string hash;
                await using (var hashing = File.OpenRead(f.Path))
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashing, ct).ConfigureAwait(false));

                await using var content = File.OpenRead(f.Path);
                await channel.PutFileAsync(vmId, GuestPaths.Join(guestOs, guestProfileDirectory, f.Relative), content, hash, ct)
                    .ConfigureAwait(false);
                manifest.Add(new ProfileFile(f.Relative, f.Length, hash));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProfileTransferException)
        {
            throw new ProfileTransferException(ProfileTransferFailure.Channel, "The guest channel failed while copying the profile in.", ex);
        }

        return Build(manifest);
    }

    public async Task<ProfileManifest> PullAsync(
        Guid vmId, GuestOs guestOs, string guestProfileDirectory, string hostStagingDirectory, CancellationToken ct)
    {
        var staging = Path.GetFullPath(hostStagingDirectory);
        // A link as the staging directory would send every write to wherever the link points. Checked before anything is created.
        if (HostPathGuard.IsLink(staging))
            throw Invalid("The staging directory is a link.");
        if (Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any())
            throw Invalid("The staging directory is not empty.");

        try
        {
            IReadOnlyList<GuestFileInfo> listed;
            try
            {
                listed = await channel.ListFilesAsync(vmId, guestProfileDirectory, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new ProfileTransferException(ProfileTransferFailure.Channel, "The guest channel failed while listing the profile.", ex);
            }

            ValidateList(listed.Select(f => (f.RelativePath, f.Length)), options.ProfileLimits);
            foreach (var f in listed)
            {
                if (f.Sha256 is not { Length: 64 } || !f.Sha256.All(Uri.IsHexDigit))
                    throw Invalid($"The guest gave no valid SHA-256 for '{SafeText.Clean(f.RelativePath)}'.");
                RequireAllowed(f.RelativePath, f.Length);
            }

            Directory.CreateDirectory(staging);
            var manifest = new List<ProfileFile>();
            foreach (var f in listed.OrderBy(x => x.RelativePath, StringComparer.Ordinal))
            {
                var target = Path.GetFullPath(Path.Combine(staging, f.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!SafeNames.IsUnderOrEqual(staging, target, caseInsensitive: false) || target.Equals(staging, StringComparison.Ordinal))
                    throw Invalid($"'{SafeText.Clean(f.RelativePath)}' resolves outside the staging directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                string actualHash;
                long actualLength;
                await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await using (var sink = new HashingLimitStream(file, f.Length))
                {
                    try
                    {
                        await channel.GetFileAsync(vmId, GuestPaths.Join(guestOs, guestProfileDirectory, f.RelativePath), sink, ct)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not ProfileTransferException)
                    {
                        throw new ProfileTransferException(
                            ProfileTransferFailure.Channel, $"The guest channel failed while reading '{SafeText.Clean(f.RelativePath)}'.", ex);
                    }

                    (actualHash, actualLength) = sink.Finish();
                }

                if (actualLength != f.Length || !actualHash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ProfileTransferException(
                        ProfileTransferFailure.HashMismatch, $"'{SafeText.Clean(f.RelativePath)}' does not match the length or hash the guest listed.");
                manifest.Add(new ProfileFile(f.RelativePath, f.Length, actualHash));
            }

            return Build(manifest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A name the host file system refuses (for example a file and a folder of the same name) must not surface as a raw IO error.
            RemoveStaging(staging);
            throw new ProfileTransferException(ProfileTransferFailure.Invalid, "The host could not write the pulled profile into staging.", ex);
        }
        catch
        {
            RemoveStaging(staging);
            throw;
        }
    }

    private static void RemoveStaging(string staging)
    {
        try
        {
            if (Directory.Exists(staging) && !HostPathGuard.IsLink(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async Task DeleteGuestCopyAsync(Guid vmId, string guestProfileDirectory, CancellationToken ct)
    {
        try
        {
            await channel.DeleteDirectoryAsync(vmId, guestProfileDirectory, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ProfileTransferException)
        {
            throw new ProfileTransferException(ProfileTransferFailure.Channel, "The guest channel failed while deleting the profile copy.", ex);
        }
    }

    private void RequireAllowed(string relativePath, long length)
    {
        var decision = policy.Evaluate(new MountContentCandidate(MountKind.Soul, MountContentOrigin.ProfileFile, relativePath, length));
        if (!decision.Allowed)
            throw Invalid($"'{SafeText.Clean(relativePath)}' is refused by the content policy: {SafeText.Clean(decision.Reason)}.");
    }

    private static void ValidateList(IEnumerable<(string Relative, long Length)> files, ProfileLimits limits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var (relative, length) in files)
        {
            if (!SafeNames.IsSafeRelativePath(relative))
                throw Invalid($"'{SafeText.Clean(relative)}' is not a safe relative path.");
            // Case-insensitive on every OS: two names that differ only in case would collide on a Windows host.
            if (!seen.Add(relative))
                throw Invalid($"'{SafeText.Clean(relative)}' appears twice, or differs from another file only by case.");
            if (seen.Count > limits.MaxFiles)
                throw Invalid($"The profile has more than {limits.MaxFiles} files.");
            if (length < 0 || length > limits.MaxFileBytes)
                throw Invalid($"'{SafeText.Clean(relative)}' is larger than {limits.MaxFileBytes} bytes.");
            // Compared before adding: a guest that lists huge lengths must not wrap the running total around.
            if (length > limits.MaxTotalBytes - total)
                throw Invalid($"The profile is larger than {limits.MaxTotalBytes} bytes.");
            total += length;
        }

        // A path that is also a folder of another path (a file "a" next to "a/b") cannot exist on disk. Whole segments, any case.
        foreach (var relative in seen)
        {
            var slash = relative.IndexOf('/', StringComparison.Ordinal);
            while (slash >= 0)
            {
                if (seen.Contains(relative[..slash]))
                    throw Invalid($"'{SafeText.Clean(relative)}' lies below another listed file.");
                slash = relative.IndexOf('/', slash + 1);
            }
        }
    }

    private static ProfileManifest Build(List<ProfileFile> files)
    {
        files.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        var text = new StringBuilder();
        foreach (var f in files)
            text.Append(f.RelativePath).Append('\0').Append(f.Length).Append('\0').Append(f.Sha256).Append('\n');
        return new ProfileManifest(files, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
    }

    private static ProfileTransferException Invalid(string message) => new(ProfileTransferFailure.Invalid, message);
}

/// <summary>Guest path joining. Guest paths are built here from server-chosen roots and validated relative paths only.</summary>
internal static class GuestPaths
{
    public static string Join(GuestOs os, string directory, string relativePath) => os == GuestOs.Windows
        ? directory.TrimEnd('\\') + "\\" + relativePath.Replace('/', '\\')
        : directory.TrimEnd('/') + "/" + relativePath;
}

/// <summary>A write-only stream that hashes what passes through and refuses more than the declared length.</summary>
internal sealed class HashingLimitStream(Stream inner, long declaredLength) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _written;

    public (string Hash, long Length) Finish()
    {
        inner.Flush();
        return (Convert.ToHexStringLower(_hash.GetHashAndReset()), _written);
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _written;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Account(buffer.Length);
        _hash.AppendData(buffer);
        inner.Write(buffer);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Account(buffer.Length);
        _hash.AppendData(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Account(int count)
    {
        _written += count;
        if (_written > declaredLength)
            throw new ProfileTransferException(ProfileTransferFailure.HashMismatch, "The guest sent more bytes than it listed.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
