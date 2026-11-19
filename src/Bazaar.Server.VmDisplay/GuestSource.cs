using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// The <see cref="DisplaySourceKind.Guest"/> source for Linux guests: fixed argv over <see cref="IDisplayGuestExec"/>. The
/// guest never chooses a path or a tool (docs/design/59, section 5, rule 4).
/// </summary>
internal static class GuestSource
{
    private const string CaptureDirectory = "/run/bazaar/display/";

    /// <summary>Captures one PNG with <c>grim</c>, fetches it into a capped stream and removes the file.</summary>
    public static async Task<byte[]> CaptureAsync(IDisplayGuestExec exec, DisplayTarget target, TimeSpan timeout, int maxBytes, CancellationToken ct)
    {
        string path = $"{CaptureDirectory}{Guid.NewGuid():D}.png";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            int exit = await exec.ExecAsync(target.VmId, ["grim", "-t", "png", path], timeout, cts.Token).ConfigureAwait(false);
            if (exit != 0)
                throw new DisplayException(DisplayFailure.SourceFailed, $"The guest capture tool exited with code {exit}.");
            using var sink = new CappedSinkStream(maxBytes);
            await exec.GetFileAsync(target.VmId, path, sink, cts.Token).ConfigureAwait(false);
            return sink.ToArray();
        }
        finally
        {
            await CleanupAsync(exec, target.VmId, path).ConfigureAwait(false);
        }
    }

    /// <summary>Runs the fixed input commands for <paramref name="actions"/>, stopping at the first failure.</summary>
    public static async Task SendInputAsync(IDisplayGuestExec exec, DisplayTarget target, IReadOnlyList<InputAction> actions, TimeSpan timeout, CancellationToken ct)
    {
        foreach (IReadOnlyList<string> argv in LinuxInputArgv.Build(actions))
        {
            int exit = await exec.ExecAsync(target.VmId, argv, timeout, ct).ConfigureAwait(false);
            if (exit != 0)
                throw new DisplayException(DisplayFailure.SourceFailed, $"The guest input tool exited with code {exit}.");
        }
    }

    // Best effort and deliberately not tied to the caller's token, so a cancelled capture still removes its file.
    private static async Task CleanupAsync(IDisplayGuestExec exec, Guid vmId, string path)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await exec.ExecAsync(vmId, ["rm", "-f", path], TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The file is under a tmpfs the guest clears on reboot; a failed cleanup must not mask the real result.
        }
    }
}
