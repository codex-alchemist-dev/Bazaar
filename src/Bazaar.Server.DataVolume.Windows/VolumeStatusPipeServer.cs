using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Bazaar.Server.DataVolume.Windows;

/// <summary>
/// The server side of the status pipe, hosted by the privileged volume service (host work, not part of this module). It answers
/// one question, "status for volume GUID X", by asking BitLocker, and rejects every other request. It has no verb that
/// changes anything.
/// </summary>
/// <remarks>
/// Pipe ACL: SYSTEM and Administrators full control, the service account read and write, remote (network) callers denied, nobody
/// else. <c>PipeOptions.CurrentUserOnly</c> is not used because the client runs as another account.
/// </remarks>
public sealed class VolumeStatusPipeServer
{
    private readonly IBitLockerVolumes _volumes;
    private readonly string _pipeName;
    private readonly TimeSpan _timeout;

    internal VolumeStatusPipeServer(IBitLockerVolumes volumes, string pipeName = VolumeStatusProtocol.DefaultPipeName, TimeSpan? timeout = null)
    {
        _volumes = volumes;
        _pipeName = pipeName;
        _timeout = timeout ?? VolumeStatusProtocol.Timeout;
    }

    /// <summary>The server with the real BitLocker seam.</summary>
    [SupportedOSPlatform("windows")]
    public static VolumeStatusPipeServer CreateDefault() => new(new PowerShellBitLockerVolumes(new ProcessCommandRunner()));

    /// <summary>Serves requests one at a time until cancelled. Each connection gets a 5 second budget.</summary>
    [SupportedOSPlatform("windows")]
    public async Task RunAsync(SecurityIdentifier serviceAccount, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceAccount);
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = NamedPipeServerStreamAcl.Create(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                inBufferSize: VolumeStatusProtocol.MaxResponseBytes,
                outBufferSize: VolumeStatusProtocol.MaxResponseBytes,
                BuildSecurity(serviceAccount));

            await pipe.WaitForConnectionAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(_timeout);
            try
            {
                await HandleAsync(pipe, limit.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // A slow or broken client costs it its connection and nothing else.
            }
            finally
            {
                if (pipe.IsConnected)
                    pipe.Disconnect();
            }
        }
    }

    /// <summary>The pipe's access control list. Public for the Windows-only ACL test.</summary>
    [SupportedOSPlatform("windows")]
    internal static PipeSecurity BuildSecurity(SecurityIdentifier serviceAccount)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(serviceAccount, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>Answers one request on any duplex stream. A malformed or oversized request gets an error line, never a status.</summary>
    internal async ValueTask HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        VolumeStatusReply reply;
        try
        {
            var line = await VolumeStatusProtocol.ReadLineAsync(stream, VolumeStatusProtocol.MaxRequestBytes, cancellationToken);
            reply = VolumeStatusProtocol.TryParseRequest(line, out var guid)
                ? await ReadStatusAsync(guid, cancellationToken)
                : Failure("bad_request");
        }
        catch (InvalidDataException)
        {
            reply = Failure("bad_request");
        }

        await VolumeStatusProtocol.WriteLineAsync(stream, VolumeStatusProtocol.SerializeReply(reply), cancellationToken);
    }

    private async ValueTask<VolumeStatusReply> ReadStatusAsync(string guid, CancellationToken cancellationToken)
    {
        try
        {
            var conversion = await _volumes.GetConversionStatusAsync(guid, cancellationToken);
            var protection = await _volumes.GetProtectionStatusAsync(guid, cancellationToken);
            var method = await _volumes.GetEncryptionMethodAsync(guid, cancellationToken);
            var lockStatus = await _volumes.GetLockStatusAsync(guid, cancellationToken);
            return new VolumeStatusReply(
                guid,
                conversion == BitLockerConversion.FullyEncrypted,
                protection == BitLockerValues.ProtectionOn,
                method,
                lockStatus == BitLockerValues.Locked);
        }
        catch (BitLockerException)
        {
            return Failure("status_failed");
        }
    }

    private static VolumeStatusReply Failure(string error) => new(null, false, false, 0, false, error);
}
