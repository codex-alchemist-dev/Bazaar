namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>
/// Finds the Hyper-V partition id of the running sandbox. The Hyper-V socket listener must bind to this id, not the wildcard
/// (doc 50 section 9, spike S-WSB step 9). It is new on every start and is not the <c>wsb</c> instance id.
/// </summary>
public interface IWindowsSandboxVmIdSource
{
    /// <summary>Returns the id of the one running sandbox, or throws <see cref="WindowsSandboxVmIdException"/> when there is none or more than one.</summary>
    Task<Guid> FindRunningAsync(CancellationToken ct);
}

/// <summary>The sandbox's VM id could not be determined. The caller stops the VM: the channel fails closed.</summary>
public sealed class WindowsSandboxVmIdException(string message) : Exception(message);

/// <summary>Runs <c>hcsdiag list</c> (elevated) and returns its text. The only path to the process, so tests replace it.</summary>
public interface IHcsDiagRunner
{
    /// <summary>Returns the standard output of <c>hcsdiag list</c>.</summary>
    Task<string> ListAsync(CancellationToken ct);
}

/// <summary>
/// Reads the sandbox's VM id from <c>hcsdiag list</c> output, verified on the owner's PC. Each entry is a lower-case id line
/// followed by an indented line <c>type, state, ID, owner</c>. The sandbox's entry is the one whose owner is <c>WindowsSandbox</c>;
/// no field links it to the <c>wsb</c> id, so exactly one such entry must exist. More than one (a second sandbox, or another
/// user's) is refused rather than guessed.
/// </summary>
public sealed class HcsDiagVmIdSource(IHcsDiagRunner runner) : IWindowsSandboxVmIdSource
{
    private const string _owner = "WindowsSandbox";

    /// <inheritdoc />
    public async Task<Guid> FindRunningAsync(CancellationToken ct) => Parse(await runner.ListAsync(ct).ConfigureAwait(false));

    /// <summary>Parses <c>hcsdiag list</c> text. Pure.</summary>
    public static Guid Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var found = new List<Guid>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4 || !string.Equals(parts[^1], _owner, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(parts[1], "Running", StringComparison.Ordinal) || !Guid.TryParse(parts[^2], out var id))
            {
                continue;
            }

            found.Add(id);
        }

        return found.Count switch
        {
            1 => found[0],
            0 => throw new WindowsSandboxVmIdException("hcsdiag lists no running Windows Sandbox VM."),
            _ => throw new WindowsSandboxVmIdException($"hcsdiag lists {found.Count} running Windows Sandbox VMs; the owner of the channel cannot be told apart."),
        };
    }
}
