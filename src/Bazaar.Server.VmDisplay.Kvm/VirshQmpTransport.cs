using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Kvm;

/// <summary>
/// Sends QMP through <c>virsh -c &lt;uri&gt; qemu-monitor-command &lt;domain&gt; &lt;json&gt;</c>. The arguments are passed as separate
/// argv entries, never through a shell. libvirt answers with the full response object.
/// </summary>
public sealed class VirshQmpTransport : IQmpTransport
{
    private const int MaxStdoutChars = 1024 * 1024;
    private readonly KvmOptions _options;
    private readonly IProcessLauncher _launcher;

    /// <summary>Creates the transport with the real process launcher.</summary>
    public VirshQmpTransport(KvmOptions options)
        : this(options, new SystemProcessLauncher())
    {
    }

    internal VirshQmpTransport(KvmOptions options, IProcessLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        _options = options;
        _launcher = launcher;
    }

    /// <inheritdoc />
    public async Task<string> ExecuteAsync(string domain, string qmpJson, CancellationToken ct)
    {
        if (!DomainName.IsValid(domain))
            throw new DisplayException(DisplayFailure.InvalidInput, "The VM name is not valid.");
        string[] argv = ["-c", _options.LibvirtUri, "qemu-monitor-command", domain, qmpJson];
        ProcessResult r = await _launcher
            .RunAsync(_options.VirshPath, argv, TimeSpan.FromSeconds(_options.CommandTimeoutSeconds), MaxStdoutChars, ct)
            .ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new DisplayException(DisplayFailure.SourceFailed, $"virsh exited with code {r.ExitCode}: {Trim(r.StdErr)}");
        return r.StdOut;
    }

    private static string Trim(string text)
    {
        string t = text.Trim();
        return t.Length <= 200 ? t : t[..200];
    }
}
