namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>Settings of the virsh adapter.</summary>
/// <param name="VmDirectory">Absolute directory that holds one folder per domain (disk and domain XML).</param>
/// <param name="ConnectionUri">The libvirt connection, passed as <c>virsh --connect</c>.</param>
/// <param name="ShutdownTimeout">How long a graceful stop waits for the guest to power off before it throws. Default 120 s.</param>
/// <param name="ShutdownPollInterval">How often a graceful stop checks the domain state. Default 2 s.</param>
public sealed record VirshKvmOptions(
    string VmDirectory,
    string ConnectionUri = "qemu:///system",
    TimeSpan? ShutdownTimeout = null,
    TimeSpan? ShutdownPollInterval = null)
{
    public TimeSpan EffectiveShutdownTimeout => ShutdownTimeout ?? TimeSpan.FromSeconds(120);

    public TimeSpan EffectiveShutdownPollInterval => ShutdownPollInterval ?? TimeSpan.FromSeconds(2);
}

/// <summary>
/// <see cref="IKvmHost"/> over <c>virsh</c> and <c>qemu-img</c>. Every call is <c>virsh --connect &lt;uri&gt; &lt;verb&gt; ...</c> with an
/// argument list, so nothing is shell-parsed. Every name is validated against <see cref="KvmNames"/> and every path is checked before
/// any process starts.
/// </summary>
public sealed class VirshKvmHost : IKvmHost
{
    private const string _diskFileName = "disk.qcow2";
    private const string _domainFileName = "domain.xml";

    private readonly IKvmCommandRunner _runner;
    private readonly VirshKvmOptions _options;
    private readonly IKvmFileSystem _files;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<KvmDomainDefinition, string> _buildXml;

    public VirshKvmHost(
        IKvmCommandRunner runner,
        VirshKvmOptions options,
        IKvmFileSystem? files = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<KvmDomainDefinition, string>? buildXml = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ValidatePath(options.VmDirectory, nameof(options));
        ValidateConnectionUri(options.ConnectionUri);
        _runner = runner;
        _options = options;
        _files = files ?? new PhysicalKvmFileSystem();
        _delay = delay ?? Task.Delay;
        _buildXml = buildXml ?? KvmDomainXml.Build;
    }

    /// <inheritdoc />
    public async Task<string> CreateBackedDiskAsync(string domainName, string parentDiskPath, CancellationToken ct)
    {
        RequireDomain(domainName);
        ValidatePath(parentDiskPath, nameof(parentDiskPath));
        if (!_files.FileExists(parentDiskPath))
        {
            throw new FileNotFoundException($"The parent disk '{parentDiskPath}' does not exist.", parentDiskPath);
        }

        var directory = DomainDirectory(domainName);
        var child = Path.Combine(directory, _diskFileName);
        _files.CreateDirectory(directory);
        var result = await _runner.RunAsync("qemu-img", ["create", "-f", "qcow2", "-b", parentDiskPath, "-F", "qcow2", "--", child], null, ct);
        Ensure(result, "qemu-img create");
        return child;
    }

    /// <inheritdoc />
    public async Task DefineAsync(KvmDomainDefinition domain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domain);
        RequireDomain(domain.Name);
        ValidatePath(domain.DiskPath, nameof(domain));

        var xml = _buildXml(domain);
        var directory = DomainDirectory(domain.Name);
        var file = Path.Combine(directory, _domainFileName);
        _files.CreateDirectory(directory);
        await _files.WriteAllTextAsync(file, xml, ct);
        await VirshAsync(["define", file], ct);
    }

    /// <inheritdoc />
    public async Task StartAsync(string domainName, CancellationToken ct)
    {
        RequireDomain(domainName);
        await VirshAsync(["start", domainName], ct);
    }

    /// <inheritdoc />
    public async Task StopAsync(string domainName, bool force, CancellationToken ct)
    {
        RequireDomain(domainName);
        if (force)
        {
            await VirshAsync(["destroy", domainName], ct);
            return;
        }

        await VirshAsync(["shutdown", domainName], ct);
        var interval = _options.EffectiveShutdownPollInterval;
        var timeout = _options.EffectiveShutdownTimeout;
        for (var waited = TimeSpan.Zero; ; waited += interval)
        {
            var state = await GetStateAsync(domainName, ct);
            if (state is KvmDomainState.Off or KvmDomainState.Saved or KvmDomainState.Missing)
            {
                return;
            }

            if (waited >= timeout)
            {
                throw new KvmCommandException($"Domain '{domainName}' did not shut down within {timeout}.");
            }

            await _delay(interval, ct);
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(string domainName, CancellationToken ct)
    {
        RequireDomain(domainName);
        await VirshAsync(["managedsave", domainName], ct);
    }

    /// <inheritdoc />
    public async Task CheckpointAsync(string domainName, string checkpointName, CancellationToken ct)
    {
        RequireDomain(domainName);
        RequireCheckpoint(checkpointName);
        await VirshAsync(["snapshot-create-as", domainName, checkpointName, "--atomic"], ct);
    }

    /// <inheritdoc />
    public async Task RestoreAsync(string domainName, string checkpointName, CancellationToken ct)
    {
        RequireDomain(domainName);
        RequireCheckpoint(checkpointName);
        await VirshAsync(["snapshot-revert", domainName, checkpointName], ct);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string domainName, bool deleteDisk, CancellationToken ct)
    {
        RequireDomain(domainName);
        var state = await GetStateAsync(domainName, ct);
        if (state != KvmDomainState.Missing)
        {
            if (state is KvmDomainState.Running or KvmDomainState.Paused or KvmDomainState.Transitioning)
            {
                await VirshAsync(["destroy", domainName], ct);
            }

            await VirshAsync(["undefine", domainName, "--managed-save", "--snapshots-metadata", "--nvram"], ct);
        }

        if (deleteDisk)
        {
            _files.DeleteDirectory(DomainDirectory(domainName));
        }
    }

    /// <inheritdoc />
    public async Task<KvmDomainState> GetStateAsync(string domainName, CancellationToken ct)
    {
        RequireDomain(domainName);
        var result = await RunVirshAsync(["domstate", domainName], ct);
        if (result.ExitCode != 0)
        {
            if (IsMissing(result))
            {
                return KvmDomainState.Missing;
            }

            throw Failure("virsh domstate", result);
        }

        var text = result.StdOut.Trim().ToLowerInvariant();
        switch (text)
        {
            case "running":
            case "blocked":
                return KvmDomainState.Running;
            case "paused":
                return KvmDomainState.Paused;
            case "in shutdown":
            case "crashed":
            case "pmsuspended":
                return KvmDomainState.Transitioning;
            case "shut off":
                return await HasManagedSaveAsync(domainName, ct) ? KvmDomainState.Saved : KvmDomainState.Off;
            default:
                throw new KvmCommandException($"virsh domstate returned an unknown state '{text}' for '{domainName}'.");
        }
    }

    private static bool IsMissing(KvmCommandResult result) =>
        result.StdErr.Contains("failed to get domain", StringComparison.OrdinalIgnoreCase)
        || result.StdErr.Contains("Domain not found", StringComparison.OrdinalIgnoreCase);

    private static KvmCommandException Failure(string what, KvmCommandResult result) =>
        new($"{what} failed with exit code {result.ExitCode}: {result.StdErr.Trim()}");

    private static void Ensure(KvmCommandResult result, string what)
    {
        if (result.ExitCode != 0)
        {
            throw Failure(what, result);
        }
    }

    private static void RequireDomain(string name)
    {
        if (!KvmNames.IsDomainName(name))
        {
            throw new ArgumentException("Not a Bazaar domain name.", nameof(name));
        }
    }

    private static void RequireCheckpoint(string name)
    {
        if (!KvmNames.IsCheckpointName(name))
        {
            throw new ArgumentException("Not a Bazaar checkpoint name.", nameof(name));
        }
    }

    private static void ValidatePath(string path, string parameter)
    {
        if (string.IsNullOrEmpty(path)
            || path[0] == '-'
            || path.Any(char.IsControl)
            || !Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The path must be absolute, without control characters and not start with '-'.", parameter);
        }
    }

    private static void ValidateConnectionUri(string uri)
    {
        if (string.IsNullOrEmpty(uri) || uri[0] == '-' || uri.Any(char.IsControl) || uri.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Not a usable libvirt connection URI.", nameof(uri));
        }
    }

    private string DomainDirectory(string domainName) => Path.Combine(_options.VmDirectory, domainName);

    private Task<KvmCommandResult> RunVirshAsync(IEnumerable<string> verbAndArguments, CancellationToken ct) =>
        _runner.RunAsync("virsh", ["--connect", _options.ConnectionUri, .. verbAndArguments], null, ct);

    private async Task VirshAsync(IReadOnlyList<string> verbAndArguments, CancellationToken ct)
    {
        var result = await RunVirshAsync(verbAndArguments, ct);
        Ensure(result, $"virsh {verbAndArguments[0]}");
    }

    private async Task<bool> HasManagedSaveAsync(string domainName, CancellationToken ct)
    {
        var result = await RunVirshAsync(["dominfo", domainName], ct);
        Ensure(result, "virsh dominfo");
        foreach (var line in result.StdOut.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0
                && line[..colon].Trim().Equals("Managed save", StringComparison.OrdinalIgnoreCase)
                && line[(colon + 1)..].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
