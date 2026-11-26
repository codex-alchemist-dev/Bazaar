namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>Runs <c>virsh</c> and <c>qemu-img</c>. The only path from the adapter to a process, so tests replace it.</summary>
public interface IKvmCommandRunner
{
    /// <summary>
    /// Runs <paramref name="program"/> with <paramref name="arguments"/>, passed as a list and never through a shell. A non-zero exit code
    /// is a result, not an exception: the caller decides what it means.
    /// </summary>
    Task<KvmCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct);
}

/// <summary>The exit code and captured output of one command.</summary>
public sealed record KvmCommandResult(int ExitCode, string StdOut, string StdErr);

/// <summary>A libvirt or qemu-img call failed, could not start, or its output could not be read.</summary>
public sealed class KvmCommandException : Exception
{
    public KvmCommandException(string message)
        : base(message)
    {
    }

    public KvmCommandException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
