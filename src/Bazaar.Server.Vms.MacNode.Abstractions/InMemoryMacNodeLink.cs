using Bazaar.Server.Vms.Devices;

namespace Bazaar.Server.Vms.MacNode;

/// <summary>One call the in-memory link received, in order, for tests to assert on.</summary>
/// <param name="Operation">Operation name, for example <c>Launch</c> or <c>RunCommand</c>.</param>
/// <param name="Kind">The guest kind, or null for a link-level call.</param>
/// <param name="NodeRef">The node reference, or null.</param>
/// <param name="Command">The command for <c>RunCommand</c>, otherwise null.</param>
public sealed record MacNodeCall(string Operation, MacGuestKind? Kind, string? NodeRef, MacGuestCommand? Command = null);

/// <summary>
/// A fake Mac node that lives in memory, for cloud CI and development: no Mac, Xcode or Apple call is made (doc 62 section 10). It
/// models the node's rules that matter to the providers: it must be connected, a guest must exist and be in the right state, and macOS
/// virtual machines are capped at the node's <see cref="MacNodeInfo.MaxVmInstances"/>, never above Apple's two.
/// <para>Apple rule: this fake stands in for a Mac; real iOS and macOS guests run only on Apple hardware.</para>
/// </summary>
public sealed class InMemoryMacNodeLink : IMacNodeLink
{
    private static readonly byte[] _pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly object _gate = new();
    private readonly Dictionary<string, Guest> _guests = new(StringComparer.Ordinal);
    private readonly List<MacNodeCall> _calls = [];
    private MacNodeStatus _status;
    private MacNodeInfo? _reachableInfo;
    private string? _refusal;
    private int _next;

    /// <summary>Creates a link with no node: <see cref="MacNodeState.Unlinked"/> until <see cref="Configure"/> is called.</summary>
    public InMemoryMacNodeLink() => _status = MacNodeStatus.Unlinked;

    /// <summary>Creates a configured link that connects on <see cref="ConnectAsync"/> to a node with <paramref name="info"/>.</summary>
    public InMemoryMacNodeLink(MacNodeInfo info)
    {
        _status = new MacNodeStatus(MacNodeState.Disconnected, null, null);
        _reachableInfo = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>A node as a good Apple silicon Mac reports itself: Xcode, Virtualization.framework, a dedicated user, the two-VM cap.</summary>
    public static MacNodeInfo GoodNode { get; } = new("15.1", "arm64", true, true, true, MacNodeRules.MaxMacOsVmInstances);

    public MacNodeStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    /// <summary>The calls received so far, in order.</summary>
    public IReadOnlyList<MacNodeCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>Sets the node that the next connect reaches. The link is then Disconnected until <see cref="ConnectAsync"/>.</summary>
    public void Configure(MacNodeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (_gate)
        {
            _reachableInfo = info;
            _refusal = null;
            _status = new MacNodeStatus(MacNodeState.Disconnected, null, null);
        }
    }

    /// <summary>Makes the next connect fail the key check, so the state becomes Refused (fail closed).</summary>
    public void RefuseKey(string reason = "its identity key did not match the pin")
    {
        lock (_gate)
        {
            _refusal = reason;
        }
    }

    /// <summary>Simulates the node going away: the state becomes Disconnected, guests keep their node-side state.</summary>
    public void DropConnection(string? reason = null)
    {
        lock (_gate)
        {
            if (_status.State == MacNodeState.Connected)
            {
                _status = new MacNodeStatus(MacNodeState.Disconnected, reason, null);
            }
        }
    }

    public Task ConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add(new MacNodeCall("Connect", null, null));
            if (_status.State == MacNodeState.Connected)
            {
                return Task.CompletedTask;
            }

            if (_reachableInfo is null)
            {
                return Task.FromException(new MacNodeUnavailableException(MacNodeRules.NoNodeLinkedReason));
            }

            if (_refusal is not null)
            {
                _status = new MacNodeStatus(MacNodeState.Refused, _refusal, null);
                return Task.FromException(new MacNodeUnavailableException(_status.UnavailableReason!));
            }

            _status = new MacNodeStatus(MacNodeState.Connected, null, _reachableInfo);
            return Task.CompletedTask;
        }
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _calls.Add(new MacNodeCall("Disconnect", null, null));
            if (_status.State is MacNodeState.Connected or MacNodeState.Connecting)
            {
                _status = new MacNodeStatus(MacNodeState.Disconnected, null, null);
            }
        }

        return Task.CompletedTask;
    }

    public Task<string> CreateGuestAsync(MacGuestKind kind, MacGuestSpec spec, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Run(() =>
        {
            RequireConnected();
            if (string.IsNullOrWhiteSpace(spec.ParentRef))
            {
                throw new ArgumentException("A guest needs a parent runtime or restore image.", nameof(spec));
            }

            if (kind == MacGuestKind.IosSimulator && string.IsNullOrWhiteSpace(spec.DeviceType))
            {
                throw new ArgumentException("An iOS Simulator guest needs a device type.", nameof(spec));
            }

            var nodeRef = kind == MacGuestKind.IosSimulator
                ? $"sim-{Interlocked.Increment(ref _next):D4}"
                : $"mac-{Interlocked.Increment(ref _next):D4}";
            _guests[nodeRef] = new Guest(kind, spec, Running: false);
            _calls.Add(new MacNodeCall("Create", kind, nodeRef));
            return nodeRef;
        });
    }

    public Task LaunchGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Run(() =>
    {
        RequireConnected();
        var guest = Find(kind, nodeRef);
        _calls.Add(new MacNodeCall("Launch", kind, nodeRef));
        if (guest.Running)
        {
            throw new InvalidOperationException("The guest is already running.");
        }

        if (kind == MacGuestKind.MacOsVm)
        {
            // Apple's cap, enforced by the node whatever the primary asks for.
            var cap = Math.Min(_status.Info!.MaxVmInstances, MacNodeRules.MaxMacOsVmInstances);
            if (_guests.Values.Count(g => g.Kind == MacGuestKind.MacOsVm && g.Running) >= cap)
            {
                throw new InvalidOperationException($"The Mac node allows {cap} running macOS virtual machine(s).");
            }
        }

        _guests[nodeRef] = guest with { Running = true };
    });

    public Task StopGuestAsync(MacGuestKind kind, string nodeRef, bool force, CancellationToken ct) => Run(() =>
    {
        RequireConnected();
        var guest = Find(kind, nodeRef);
        _calls.Add(new MacNodeCall(force ? "StopForce" : "Stop", kind, nodeRef));
        if (!guest.Running)
        {
            throw new InvalidOperationException("The guest is not running.");
        }

        _guests[nodeRef] = guest with { Running = false };
    });

    public Task DeleteGuestAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Run(() =>
    {
        RequireConnected();
        _ = Find(kind, nodeRef);
        _calls.Add(new MacNodeCall("Delete", kind, nodeRef));
        _guests.Remove(nodeRef);
    });

    public Task<bool> IsGuestRunningAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Run(() =>
    {
        RequireConnected();
        return Find(kind, nodeRef).Running;
    });

    public Task RunCommandAsync(MacGuestKind kind, string nodeRef, MacGuestCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Run(() =>
        {
            RequireConnected();
            var guest = Find(kind, nodeRef);
            if (!guest.Running)
            {
                throw new InvalidOperationException("The guest is not running.");
            }

            // The node accepts typed commands only and validates each variable part itself.
            switch (command)
            {
                case MacGuestCommand.InstallApp install:
                    _ = DeviceIdentifiers.WorkspaceRelativePath(install.WorkspaceRelativePath);
                    break;
                case MacGuestCommand.LaunchApp launch:
                    _ = DeviceIdentifiers.AppId(launch.AppId);
                    break;
                case MacGuestCommand.Input { Value: DeviceInput.Text text }:
                    _ = DeviceIdentifiers.InputText(text.Value);
                    break;
                case MacGuestCommand.Input:
                    break;
                default:
                    throw new NotSupportedException("The Mac node does not know that command.");
            }

            _calls.Add(new MacNodeCall("RunCommand", kind, nodeRef, command));
        });
    }

    public Task<DeviceScreen> ScreenshotAsync(MacGuestKind kind, string nodeRef, CancellationToken ct) => Run(() =>
    {
        RequireConnected();
        var guest = Find(kind, nodeRef);
        if (!guest.Running)
        {
            throw new InvalidOperationException("The guest is not running.");
        }

        _calls.Add(new MacNodeCall("Screenshot", kind, nodeRef));
        return new DeviceScreen(_pngSignature, DateTimeOffset.UtcNow);
    });

    private Task Run(Action action)
    {
        lock (_gate)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }
    }

    private Task<T> Run<T>(Func<T> action)
    {
        lock (_gate)
        {
            try
            {
                return Task.FromResult(action());
            }
            catch (Exception ex)
            {
                return Task.FromException<T>(ex);
            }
        }
    }

    private void RequireConnected()
    {
        if (_status.UnavailableReason is { } reason)
        {
            throw new MacNodeUnavailableException(reason);
        }
    }

    private Guest Find(MacGuestKind kind, string nodeRef)
    {
        if (_guests.TryGetValue(nodeRef, out var guest) && guest.Kind == kind)
        {
            return guest;
        }

        throw new KeyNotFoundException("The Mac node has no such guest.");
    }

    private sealed record Guest(MacGuestKind Kind, MacGuestSpec Spec, bool Running);
}
