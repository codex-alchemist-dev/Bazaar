using System.IO.Pipes;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// The unprivileged host's only way to learn the BitLocker state of a folder mount (doc 63 sections 3.1 and 4): it asks the
/// privileged volume service "status for volume GUID X".
/// </summary>
internal interface IVolumeStatusChannel
{
    /// <summary>
    /// Returns the service's answer for <paramref name="canonicalVolumeGuid"/>, or null when the channel is unavailable, times
    /// out, answers with an error or answers for another volume. Null always means "not proven encrypted".
    /// </summary>
    ValueTask<VolumeStatusReply?> GetStatusAsync(string canonicalVolumeGuid, CancellationToken cancellationToken);
}

/// <summary>The named-pipe client of the status channel (<c>\\.\pipe\bazaar-volume</c>).</summary>
internal sealed class NamedPipeVolumeStatusChannel(string pipeName = VolumeStatusProtocol.DefaultPipeName, TimeSpan? timeout = null) : IVolumeStatusChannel
{
    private readonly TimeSpan _timeout = timeout ?? VolumeStatusProtocol.Timeout;

    public async ValueTask<VolumeStatusReply?> GetStatusAsync(string canonicalVolumeGuid, CancellationToken cancellationToken)
    {
        if (!VolumeGuid.IsCanonical(canonicalVolumeGuid))
            return null;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(_timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(limit.Token);
            return await ExchangeAsync(pipe, canonicalVolumeGuid, limit.Token);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>One request and one answer over any duplex stream. Exposed so tests can run it without a real pipe.</summary>
    internal static async ValueTask<VolumeStatusReply?> ExchangeAsync(Stream stream, string canonicalVolumeGuid, CancellationToken cancellationToken)
    {
        await VolumeStatusProtocol.WriteLineAsync(stream, VolumeStatusProtocol.FormatRequest(canonicalVolumeGuid), cancellationToken);
        var line = await VolumeStatusProtocol.ReadLineAsync(stream, VolumeStatusProtocol.MaxResponseBytes, cancellationToken);
        var reply = line is null ? null : VolumeStatusProtocol.ParseReply(line);
        if (reply is null || reply.Error is not null)
            return null;

        // An answer about another volume proves nothing about this one.
        return string.Equals(reply.Volume, canonicalVolumeGuid, StringComparison.Ordinal) ? reply : null;
    }
}
