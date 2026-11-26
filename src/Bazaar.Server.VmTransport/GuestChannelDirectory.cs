using System.Text.RegularExpressions;
using Bazaar.Server.VmMounts.Abstractions;

namespace Bazaar.Server.VmTransport;

/// <summary>
/// The writer side of the channel directory (doc 16 "Channel directory", PROPOSED): who may put an open channel on record. Registering does not
/// prove ownership; <see cref="IGuestChannelDirectory"/> checks every entry against the VM store before it serves it.
/// </summary>
public interface IGuestChannelRegistry
{
    /// <summary>
    /// Records <paramref name="channel"/> as the open channel of VM <paramref name="vmId"/>. Call it only after the channel's Hello matched the VM.
    /// An older channel of the same VM is replaced and disposed (when it is <see cref="IAsyncDisposable"/>). The entry removes itself when the
    /// channel ends.
    /// </summary>
    /// <param name="vmId">The environment VM id.</param>
    /// <param name="agentId">The owning agent (a GUID in any format).</param>
    /// <param name="vmName">The VM's backend ref name; must pass the strict VM-name pattern.</param>
    /// <param name="os">The guest OS of the VM.</param>
    /// <param name="channel">The open channel; it must implement <see cref="IGuestChannelLifetime"/>.</param>
    /// <exception cref="ArgumentException">An id or name is not valid, or the channel has no lifetime signal.</exception>
    /// <exception cref="InvalidOperationException">The channel is already ended.</exception>
    void Register(Guid vmId, string agentId, string vmName, GuestOs os, IGuestChannel channel);

    /// <summary>Removes the entry of <paramref name="vmId"/> only if it still holds <paramref name="channel"/> (the same instance). Does not dispose it. True when removed.</summary>
    bool Unregister(Guid vmId, IGuestChannel channel);
}

/// <summary>The thread-safe registry of open channels, one per VM.</summary>
public sealed partial class GuestChannelRegistry : IGuestChannelRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    /// <summary>True when <paramref name="name"/> is a plain VM name: a letter or digit first, then letters, digits, dot, dash or underscore, at most 63 characters.</summary>
    public static bool IsValidVmName(string? name) => name is not null && VmNamePattern().IsMatch(name);

    /// <summary>Parses an agent id once. Returns false for anything that is not a non-empty GUID.</summary>
    public static bool TryParseAgentId(string? agentId, out Guid id) => Guid.TryParse(agentId, out id) && id != Guid.Empty;

    /// <inheritdoc />
    public void Register(Guid vmId, string agentId, string vmName, GuestOs os, IGuestChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        if (vmId == Guid.Empty)
        {
            throw new ArgumentException("The VM id must be set.", nameof(vmId));
        }

        if (!TryParseAgentId(agentId, out var agent))
        {
            throw new ArgumentException("The agent id must be a non-empty GUID.", nameof(agentId));
        }

        if (!IsValidVmName(vmName))
        {
            throw new ArgumentException("The VM name is not a plain VM name.", nameof(vmName));
        }

        if (!Enum.IsDefined(os))
        {
            throw new ArgumentException("The guest OS is not known.", nameof(os));
        }

        if (channel is not IGuestChannelLifetime lifetime)
        {
            throw new ArgumentException("The channel does not report whether it is open, so it cannot be kept.", nameof(channel));
        }

        if (!lifetime.IsOpen)
        {
            throw new InvalidOperationException("The channel has already ended.");
        }

        var entry = new Entry(vmId, agent, vmName, os, channel, lifetime);
        Entry? replaced;
        lock (_gate)
        {
            _entries.TryGetValue(vmId, out replaced);
            _entries[vmId] = entry;
        }

        // Registered after the entry is in, so a channel that ends in between is still removed. Unregister is by instance, so it is safe either way.
        _ = lifetime.Completion.ContinueWith(
            _ => Unregister(vmId, channel),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        if (replaced is not null && !ReferenceEquals(replaced.Channel, channel))
        {
            _ = DisposeQuietlyAsync(replaced.Channel);
        }
    }

    /// <inheritdoc />
    public bool Unregister(Guid vmId, IGuestChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        lock (_gate)
        {
            if (_entries.TryGetValue(vmId, out var current) && ReferenceEquals(current.Channel, channel))
            {
                _entries.Remove(vmId);
                return true;
            }

            return false;
        }
    }

    /// <summary>The entry of one VM when its channel is still open; an ended channel is removed and not returned.</summary>
    internal Entry? Find(Guid vmId)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(vmId, out var entry))
            {
                return null;
            }

            if (entry.Lifetime.IsOpen)
            {
                return entry;
            }

            _entries.Remove(vmId);
            return null;
        }
    }

    private static async Task DisposeQuietlyAsync(IGuestChannel channel)
    {
        try
        {
            if (channel is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // The old channel is being thrown away; a failure to close it must not break the new registration.
        }
    }

    // \A and \z, not ^ and $, so a trailing newline does not pass. Same shape as the Hyper-V enforcer's VM name rule.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VmNamePattern();

    /// <summary>One registered channel.</summary>
    internal sealed record Entry(Guid VmId, Guid AgentId, string VmName, GuestOs Os, IGuestChannel Channel, IGuestChannelLifetime Lifetime);
}

/// <summary>The read view of a <see cref="GuestChannelRegistry"/>, checked against the VM store on every call.</summary>
public sealed class GuestChannelDirectory : IGuestChannelDirectory
{
    private readonly GuestChannelRegistry _registry;
    private readonly IAgentVmLookup _vms;

    /// <summary>Creates the directory over a registry and the VM store port.</summary>
    public GuestChannelDirectory(GuestChannelRegistry registry, IAgentVmLookup vms)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(vms);
        _registry = registry;
        _vms = vms;
    }

    /// <inheritdoc />
    public async ValueTask<AgentGuest?> TryGetAsync(string agentId, GuestOs os, CancellationToken ct)
    {
        var guests = await ServedAsync(agentId, ct).ConfigureAwait(false);
        var ofOs = guests.Where(g => g.Os == os).Take(2).ToList();
        return ofOs.Count == 1 ? ofOs[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<AgentGuest>> ListAsync(string agentId, CancellationToken ct) =>
        await ServedAsync(agentId, ct).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<IGuestChannel?> TryGetByVmAsync(Guid vmId, CancellationToken ct)
    {
        var entry = _registry.Find(vmId);
        if (entry is null)
        {
            return null;
        }

        var served = await ServedAsync(entry.AgentId, ct).ConfigureAwait(false);
        return served.FirstOrDefault(g => g.VmId == vmId && ReferenceEquals(g.Channel, entry.Channel))?.Channel;
    }

    private async ValueTask<IReadOnlyList<AgentGuest>> ServedAsync(string agentId, CancellationToken ct)
    {
        if (!GuestChannelRegistry.TryParseAgentId(agentId, out var agent))
        {
            return [];
        }

        return await ServedAsync(agent, ct).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<AgentGuest>> ServedAsync(Guid agent, CancellationToken ct)
    {
        var vms = await _vms.ListAsync(agent, ct).ConfigureAwait(false);
        var served = new List<AgentGuest>();
        foreach (var vm in vms)
        {
            if (vm.Retired || !GuestChannelRegistry.IsValidVmName(vm.VmName))
            {
                continue;
            }

            var entry = _registry.Find(vm.VmId);
            if (entry is null || entry.AgentId != agent || entry.Os != vm.Os || !string.Equals(entry.VmName, vm.VmName, StringComparison.Ordinal))
            {
                continue;
            }

            served.Add(new AgentGuest(agent.ToString("D"), vm.VmId, entry.VmName, entry.Os, entry.Channel));
        }

        served.Sort(static (a, b) =>
        {
            var byOs = a.Os.CompareTo(b.Os);
            return byOs != 0 ? byOs : string.CompareOrdinal(a.VmName, b.VmName);
        });
        return served;
    }
}

/// <summary>A channel was refused at registration. The channel is disposed.</summary>
public sealed class GuestChannelRegistrationException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public GuestChannelRegistrationException(string message) : base(message)
    {
    }
}

/// <summary>
/// What the VM lifecycle calls once a channel is open (<see cref="GuestChannelFactory.OpenAsync"/> or the Windows Sandbox connector). It checks
/// the VM record first, so a channel for a VM that is not the agent's, is retired, or has another name or OS is refused and disposed.
/// </summary>
public sealed class GuestChannelRegistrar
{
    private readonly IGuestChannelRegistry _registry;
    private readonly IAgentVmLookup _vms;

    /// <summary>Creates the registrar.</summary>
    public GuestChannelRegistrar(IGuestChannelRegistry registry, IAgentVmLookup vms)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(vms);
        _registry = registry;
        _vms = vms;
    }

    /// <summary>
    /// Registers a channel that has just opened. <paramref name="expected"/> is the handshake the channel was opened against; its VM id must name
    /// this VM (the backend ref name, or the environment VM id), or the Hello proved nothing about this VM. The VM must be a live VM of the agent
    /// in the store. On any refusal the channel is disposed and <see cref="GuestChannelRegistrationException"/> is thrown.
    /// </summary>
    public async Task<AgentGuest> RegisterOpenedAsync(Guid vmId, string agentId, GuestHandshake expected, IGuestChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            if (!GuestChannelRegistry.TryParseAgentId(agentId, out var agent))
            {
                throw new GuestChannelRegistrationException("The agent id is not a GUID.");
            }

            var vms = await _vms.ListAsync(agent, ct).ConfigureAwait(false);
            var vm = vms.FirstOrDefault(v => v.VmId == vmId);
            if (vm is null || vm.Retired || !GuestChannelRegistry.IsValidVmName(vm.VmName))
            {
                throw new GuestChannelRegistrationException("The VM is not a live VM of this agent.");
            }

            var helloNamesVm = string.Equals(expected.VmId, vm.VmName, StringComparison.Ordinal)
                || (Guid.TryParse(expected.VmId, out var helloGuid) && helloGuid == vmId);
            if (!helloNamesVm)
            {
                throw new GuestChannelRegistrationException("The handshake was not for this VM.");
            }

            _registry.Register(vmId, agentId, vm.VmName!, vm.Os, channel);
            return new AgentGuest(agent.ToString("D"), vmId, vm.VmName!, vm.Os, channel);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DisposeAsync(channel).ConfigureAwait(false);
            throw ex as GuestChannelRegistrationException ?? new GuestChannelRegistrationException($"The channel was not registered: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            await DisposeAsync(channel).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisposeAsync(IGuestChannel channel)
    {
        try
        {
            if (channel is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Already refusing; a failed close changes nothing.
        }
    }
}
