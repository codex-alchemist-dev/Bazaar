using Bazaar.Server.Components.Backup.Archiving;

namespace Bazaar.Server.Components.Backup.Recovery;

/// <summary>The outcome of <see cref="RecoveryBundleFile.Read"/>: a verified payload, or a clear failure.</summary>
/// <remarks>
/// <see cref="Payload"/> holds secret material and is owned by the caller, who must dispose it. A failed result never
/// carries a payload and its message never contains the passphrase or key bytes.
/// </remarks>
public sealed record RecoveryBundleReadResult
{
    private RecoveryBundleReadResult(RecoveryBundlePayload? payload, RecoveryBundleFailure? failure, string? message)
    {
        Payload = payload;
        Failure = failure;
        Message = message;
    }

    /// <summary>The verified payload, or null when the read failed.</summary>
    public RecoveryBundlePayload? Payload { get; }

    /// <summary>The failure kind, or null when the read succeeded.</summary>
    public RecoveryBundleFailure? Failure { get; }

    /// <summary>A human-readable failure message, or null when the read succeeded.</summary>
    public string? Message { get; }

    /// <summary>True when the seal verified and <see cref="Payload"/> is set.</summary>
    public bool IsSuccess => Payload is not null;

    internal static RecoveryBundleReadResult Succeeded(RecoveryBundlePayload payload) => new(payload, null, null);

    internal static RecoveryBundleReadResult Failed(RecoveryBundleFailure failure, string message) => new(null, failure, message);
}

/// <summary>
/// Writes and reads sealed recovery bundles as files (doc 48 section 6). The bytes come from
/// <see cref="RecoveryBundleSealer"/>; this class only adds the file handling: an atomic write, a size cap checked
/// before the file is read, and a read that returns a verified payload or a clear failure.
/// </summary>
/// <remarks>
/// Import applies nothing. It returns the payload and the caller decides what to restore; applying a restore is a
/// separate step of the restore wizard.
/// </remarks>
public static class RecoveryBundleFile
{
    private const string _tempSuffix = ".tmp";

    /// <summary>
    /// Seals the payload and writes it to <paramref name="path"/> atomically: the bytes go to a temporary file in the same
    /// directory, are flushed to disk, and are then renamed over the target. A failed write leaves no temporary file.
    /// </summary>
    /// <param name="path">The target file path.</param>
    /// <param name="payload">The payload to seal.</param>
    /// <param name="passphrase">The passphrase the bundle is sealed under.</param>
    /// <param name="kdf">The key derivation cost.</param>
    /// <param name="overwrite">
    /// When false (the default), an existing file at <paramref name="path"/> is never replaced. When true, it is replaced
    /// by the rename.
    /// </param>
    /// <exception cref="IOException">The target exists and <paramref name="overwrite"/> is false, or the write failed.</exception>
    /// <exception cref="RecoveryBundleException">The sealed bundle would exceed <see cref="RecoveryBundleSealer.MaxBundleBytes"/>.</exception>
    public static void Write(string path, RecoveryBundlePayload payload, string passphrase, KdfOptions kdf, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var fullPath = Path.GetFullPath(path);
        if (!overwrite && File.Exists(fullPath))
        {
            throw new IOException("A recovery bundle already exists at the target path and overwrite was not requested.");
        }

        var bundle = RecoveryBundleSealer.Seal(payload, passphrase, kdf);
        if (bundle.Length > RecoveryBundleSealer.MaxBundleBytes)
        {
            throw new RecoveryBundleException(
                RecoveryBundleFailure.TooLarge,
                "Recovery bundle is larger than the supported maximum and would not be readable.");
        }

        WriteAtomically(fullPath, bundle, overwrite);
    }

    /// <summary>
    /// Reads and verifies a sealed bundle file. The file size is checked against
    /// <see cref="RecoveryBundleSealer.MaxBundleBytes"/> before any byte is read. A wrong passphrase, an altered file, an
    /// unknown format version or an oversized file returns a failed result, not an exception.
    /// </summary>
    /// <param name="path">The bundle file.</param>
    /// <param name="passphrase">The passphrase the bundle was sealed under.</param>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="ArgumentException">The passphrase is empty.</exception>
    public static RecoveryBundleReadResult Read(string path, string passphrase)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(passphrase);

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Recovery bundle file was not found.", path);
        }

        if (info.Length > RecoveryBundleSealer.MaxBundleBytes)
        {
            return RecoveryBundleReadResult.Failed(
                RecoveryBundleFailure.TooLarge,
                "Recovery bundle file is larger than the supported maximum and was not read.");
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            var payload = RecoveryBundleSealer.Unseal(bytes, passphrase);
            return RecoveryBundleReadResult.Succeeded(payload);
        }
        catch (RecoveryBundleException ex)
        {
            return RecoveryBundleReadResult.Failed(ex.Failure, ex.Message);
        }
        finally
        {
            Array.Clear(bytes);
        }
    }

    private static void WriteAtomically(string target, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(target)
            ?? throw new IOException("Recovery bundle path has no directory.");
        var temp = Path.Combine(directory, "." + Path.GetFileName(target) + "." + Guid.CreateVersion7().ToString("N") + _tempSuffix);

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // A directory at the target is refused here so every OS reports the same exception type.
            // Windows throws UnauthorizedAccessException from File.Move, POSIX throws IOException.
            if (Directory.Exists(target))
            {
                throw new IOException("Recovery bundle target is a directory.");
            }

            // overwrite:false makes the move fail if the target appeared after the existence check.
            File.Move(temp, target, overwrite);
            FsyncDirectory(directory);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Directory fsync makes the rename durable on POSIX. Windows does not need it.</summary>
    private static void FsyncDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PosixDirectorySync.Flush(directory);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // The original failure is the one to report.
        }
        catch (UnauthorizedAccessException)
        {
            // The original failure is the one to report.
        }
    }
}
