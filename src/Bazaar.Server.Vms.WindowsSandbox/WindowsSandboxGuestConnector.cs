using System.Security.Cryptography;
using Bazaar.Server.VmTransport;

namespace Bazaar.Server.Vms.WindowsSandbox;

/// <summary>Where the guest agent lives in the sandbox and how it reaches the host (doc 50 section 9).</summary>
/// <param name="ServiceId">The Hyper-V service id the agent dials. Registered once under <c>GuestCommunicationServices</c> at provisioning.</param>
/// <param name="Roots">The absolute guest folders the agent may serve; at least one.</param>
/// <param name="AgentExecutable">The agent program inside the read-only agent folder.</param>
/// <param name="ConnectTimeout">How long the agent has to start, dial and send its Hello before the host gives up.</param>
public sealed record WindowsSandboxGuestOptions(
    Guid ServiceId,
    IReadOnlyList<string> Roots,
    string AgentExecutable = WindowsSandboxGuestOptions.DefaultAgentExecutable,
    TimeSpan? ConnectTimeout = null)
{
    /// <summary>The agent program in the mapped agent folder (<see cref="WindowsSandboxConfig.GuestAgentFolder"/>).</summary>
    public const string DefaultAgentExecutable = @"C:\Bazaar\agent\Bazaar.Guest.Agent.exe";

    /// <summary>The connect timeout in use.</summary>
    public TimeSpan EffectiveConnectTimeout => ConnectTimeout ?? TimeSpan.FromSeconds(30);
}

/// <summary>
/// Opens the guest channel of a running Windows Sandbox (doc 50 section 9): looks up the sandbox's VM id, listens on a Hyper-V socket bound to
/// that id, starts the agent with <c>wsb exec -r System</c> and waits for its Hello. A fresh nonce goes to the agent on its command line. Every
/// failure disposes the listener and throws, and the caller stops the VM; nothing is retried silently.
/// </summary>
public sealed class WindowsSandboxGuestConnector
{
    private readonly IWindowsSandboxHost _host;
    private readonly IWindowsSandboxVmIdSource _vmIds;
    private readonly GuestChannelFactory _channels;
    private readonly Func<Guid, IGuestConnectionListener> _listenerFor;
    private readonly WindowsSandboxGuestOptions _options;

    /// <summary>
    /// Creates a connector. <paramref name="listenerFor"/> builds the listener for one sandbox VM id; the shipped one is
    /// <see cref="HyperVListener"/>. Tests pass a loopback listener.
    /// </summary>
    public WindowsSandboxGuestConnector(
        IWindowsSandboxHost host,
        IWindowsSandboxVmIdSource vmIds,
        GuestChannelFactory channels,
        WindowsSandboxGuestOptions options,
        Func<Guid, IGuestConnectionListener>? listenerFor = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(vmIds);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Roots.Count == 0)
        {
            throw new ArgumentException("The agent needs at least one root folder.", nameof(options));
        }

        _host = host;
        _vmIds = vmIds;
        _channels = channels;
        _options = options;
        _listenerFor = listenerFor ?? (vmId => HyperVListener(vmId, options.ServiceId));
    }

    /// <summary>A Hyper-V socket listener bound to one sandbox's own VM id, as spike S-WSB step 9 requires. Needs a Windows host.</summary>
    public static IGuestConnectionListener HyperVListener(Guid sandboxVmId, Guid serviceId) =>
        new HvSocketListener(new HvSocketListenerOptions(serviceId, BindVmId: sandboxVmId));

    /// <summary>
    /// Opens the channel to the sandbox <paramref name="sandboxInstanceId"/> (the <c>wsb</c> id). <paramref name="bazaarVmId"/> is the id the agent
    /// echoes in its Hello.
    /// </summary>
    /// <exception cref="WindowsSandboxVmIdException">The sandbox's VM id could not be found.</exception>
    /// <exception cref="GuestHandshakeException">The agent's Hello did not match.</exception>
    /// <exception cref="TimeoutException">The agent did not connect in time, or exited first.</exception>
    public async Task<GuestChannel> ConnectAsync(string sandboxInstanceId, string bazaarVmId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(sandboxInstanceId);
        ArgumentException.ThrowIfNullOrEmpty(bazaarVmId);

        var hypervisorId = await _vmIds.FindRunningAsync(ct).ConfigureAwait(false);
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var listener = _listenerFor(hypervisorId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.EffectiveConnectTimeout);
        try
        {
            var opening = _channels.OpenAsync(listener, hypervisorId.ToString("D"), new GuestHandshake(bazaarVmId, nonce), timeout.Token);

            // `wsb exec` blocks until the agent exits, so it runs unawaited; its end before the Hello means the agent died.
            var agent = _host.ExecAsync(sandboxInstanceId, AgentCommand(bazaarVmId, nonce), timeout.Token);
            var first = await Task.WhenAny(opening, agent).ConfigureAwait(false);
            if (first == agent)
            {
                var result = await agent.ConfigureAwait(false);
                if (!opening.IsCompleted)
                {
                    throw new TimeoutException($"The guest agent exited with code {result.ExitCode} before it connected.");
                }
            }

            return await opening.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException("The guest agent did not connect in time.");
        }
        finally
        {
            // One connection per sandbox: the accepted stream is independent of the listening socket.
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    private List<string> AgentCommand(string bazaarVmId, string nonce)
    {
        var command = new List<string>
        {
            _options.AgentExecutable,
            "--transport", "hvsock",
            "--hvsock-service-id", _options.ServiceId.ToString("D"),
            "--vm-id", bazaarVmId,
            "--nonce", nonce,
            "--connect-timeout-seconds", ((int)Math.Ceiling(_options.EffectiveConnectTimeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        foreach (var root in _options.Roots)
        {
            command.Add("--root");
            command.Add(root);
        }

        return command;
    }
}
