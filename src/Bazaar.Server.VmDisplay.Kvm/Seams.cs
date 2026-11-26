using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>Sends one QMP command to a domain's QEMU monitor and returns the JSON response object.</summary>
public interface IQmpTransport
{
    /// <summary>Executes <paramref name="qmpJson"/> (one QMP command object) against <paramref name="domain"/>.</summary>
    /// <returns>The response object, the one that carries <c>return</c> or <c>error</c>.</returns>
    /// <exception cref="DisplayException">The monitor could not be reached or the call failed outside QMP.</exception>
    Task<string> ExecuteAsync(string domain, string qmpJson, CancellationToken ct);
}

/// <summary>Reads and removes the file a screendump wrote.</summary>
public interface IScreendumpFileReader
{
    /// <summary>Reads the whole file, refusing files above the size cap.</summary>
    Task<byte[]> ReadAsync(string path, CancellationToken ct);

    /// <summary>Deletes the file when it exists. It never throws for a missing file.</summary>
    void Delete(string path);
}

/// <summary>Reads screendump files from the local disk.</summary>
public sealed class LocalScreendumpFileReader : IScreendumpFileReader
{
    private readonly int _maxBytes;

    /// <summary>Creates the reader.</summary>
    /// <param name="maxBytes">Largest file accepted.</param>
    public LocalScreendumpFileReader(int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
            if (stream.Length > _maxBytes)
                throw new DisplayException(DisplayFailure.FrameTooLarge, "The screendump file is larger than the limit.");
            byte[] data = new byte[stream.Length];
            await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            return data;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DisplayException(DisplayFailure.SourceFailed, "The screendump file could not be read.", e);
        }
    }

    /// <inheritdoc />
    public void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a file we cannot delete is the operator's directory permission problem, not a capture failure.
        }
    }
}
