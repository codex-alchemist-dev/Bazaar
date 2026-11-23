using System.Net;
using System.Net.Sockets;
using Bazaar.Server.VmMounts.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bazaar.Server.VmMounts.Providers.Smb;

/// <summary>The SMB script ran and failed, or printed something unexpected. The message never carries host output verbatim beyond one cleaned line.</summary>
public sealed class SmbShareException(string message) : Exception(message);

/// <summary>
/// The Windows-host <see cref="IMountProvider"/> (doc 16 "Workspace share transport", SMB, PROPOSED): one SMB share per mount, named
/// <c>bazaar-&lt;agent tag&gt;-&lt;soul|work&gt;-&lt;mount id&gt;</c>, whose ACL holds exactly one entry, the VM's own local account
/// (<see cref="IVmShareIdentity"/>), with Read for a read-only mount and Change for a read-write one. Everything is a constant PowerShell
/// script with parameters on stdin (<see cref="SmbScripts"/>).
/// </summary>
/// <remarks>
/// <para>
/// What it checks before it shares: the request through <see cref="MountRequestValidator"/> (the manager's own path guard: inside the agent's
/// root, no link, no denied folder, Soul read-only), a share name from the strict pattern, an account name from the strict pattern, and the
/// host address of the switch (a private or link-local unicast address). A Soul mount can never be read-write here.
/// </para>
/// <para>
/// What it does not do (gaps, reported, not hidden): New-SmbShare cannot bind a share to one local address, so the switch address is
/// recorded in the share description and validated, but reaching only that address is up to the host firewall and the egress rules (WP9);
/// the guest-side mount (<c>net use</c> or <c>mount.cifs</c>) needs the guest account's credential and is the identity service's job;
/// canary reads on SMB need object-access auditing and an event reader, so <see cref="WatchCanariesAsync"/> returns a no-op watch,
/// which the port allows. None of this was run against a real Windows host: UNVERIFIED until the Category=Hyperv run.
/// </para>
/// </remarks>
public sealed partial class SmbMountProvider : IMountProvider, IDisposable
{
    private readonly IHostCommandRunner _runner;
    private readonly IVmShareIdentity _identity;
    private readonly IVmShareEndpoint _endpoint;
    private readonly MountRequestValidator _validator;
    private readonly ILogger<SmbMountProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _requireDrivePath;

    internal SmbMountProvider(
        VmMountOptions options,
        IHostCommandRunner runner,
        IVmShareIdentity identity,
        IVmShareEndpoint endpoint,
        ILogger<SmbMountProvider> logger,
        bool requireDrivePath = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _identity = identity;
        _endpoint = endpoint;
        _validator = new MountRequestValidator(options);
        _logger = logger;
        // Always true in production. Tests on a non-Windows machine pass false because their host paths have no drive letter.
        _requireDrivePath = requireDrivePath;
    }

    /// <summary>Creates the share, or rebuilds it if it already exists. Fails closed: on any refusal or script failure no share remains.</summary>
    public async Task AttachAsync(MountProviderRequest request, CancellationToken ct)
    {
        var hostPath = _validator.ValidateAttach(request);
        var name = SmbShareNames.For(request.AgentId, request.Kind, request.MountId);

        var address = await _endpoint.GetHostAddressAsync(request.AgentId, request.VmId, ct).ConfigureAwait(false);
        RequireSwitchAddress(address);
        var account = await _identity.EnsureAccountAsync(request.AgentId, request.VmId, request.Kind, ct).ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(account);

        var description = $"bazaar mount {request.MountId:N} vm {request.VmId:N} switch {address}";
        var script = SmbScripts.BuildAttach(name, hostPath, account.AccountName, request.Mode == MountMode.Ro, description, _requireDrivePath);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunAsync(script, "create the share", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        LogAttached(_logger, name, request.Mode);
    }

    /// <summary>Removes the share. Idempotent; needs neither the guest nor the host path. The caller's token is honoured: the manager supplies its own bounded one.</summary>
    public async Task DetachAsync(MountProviderRequest request, CancellationToken ct)
    {
        MountRequestValidator.ValidateIds(request);
        var script = SmbScripts.BuildRemove(SmbShareNames.For(request.AgentId, request.Kind, request.MountId));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunAsync(script, "remove the share", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The port allows a provider that cannot observe reads to return a no-op watch; this one cannot yet (see the remarks). Logged loudly each time.</summary>
    public Task<IAsyncDisposable> WatchCanariesAsync(CanaryWatch watch, Func<CanaryAccess, ValueTask> onAccess, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(watch);
        LogNoCanaryWatch(_logger, watch.MountId);
        return Task.FromResult<IAsyncDisposable>(NoopWatch.Instance);
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>Lists the prefixed shares on the host. Used by the reconciler.</summary>
    internal async Task<IReadOnlyList<string>> ListPrefixedSharesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await _runner.RunAsync(SmbScripts.BuildList().ToCommand(), ct).ConfigureAwait(false);
            if (!result.Succeeded)
                throw new SmbShareException($"Listing the shares failed (exit {result.ExitCode}). {FirstLine(result.StandardError)}");
            return [.. result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes one share by name. The name must match the strict pattern, so a reconciler cannot be tricked into removing a share it did not create.</summary>
    internal async Task RemoveShareByNameAsync(string shareName, CancellationToken ct)
    {
        var script = SmbScripts.BuildRemove(shareName);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunAsync(script, "remove a stale share", ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RunAsync(PowerShellInvocation script, string what, CancellationToken ct)
    {
        var result = await _runner.RunAsync(script.ToCommand(), ct).ConfigureAwait(false);
        if (!result.Succeeded)
            throw new SmbShareException($"Could not {what} (exit {result.ExitCode}). {FirstLine(result.StandardError)}");
        if (!result.StandardOutput.Trim().Equals("ok", StringComparison.Ordinal))
            throw new SmbShareException($"Could not {what}: the script did not report success.");
    }

    private static void RequireSwitchAddress(IPAddress? address)
    {
        // The share is meant for the host-only switch: a private or link-local unicast address. Anything else (any, loopback,
        // multicast, a public address) means the endpoint port is wrong, and nothing is shared.
        if (address is null || IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.Broadcast))
            throw new SmbShareException("The virtual switch address is not a usable unicast address.");

        var ok = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPrivateV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal,
            _ => false,
        };
        if (!ok)
            throw new SmbShareException("The virtual switch address is not a private or link-local address.");
    }

    private static bool IsPrivateV4(byte[] b) =>
        b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        var clean = new string([.. line.Where(c => c is >= ' ' and <= '~')]);
        return clean.Length > 200 ? clean[..200] + "..." : clean;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMB share {Share} created ({Mode}).")]
    private static partial void LogAttached(ILogger logger, string share, MountMode mode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMB canary reads cannot be observed yet; mount {MountId} has no read watch.")]
    private static partial void LogNoCanaryWatch(ILogger logger, Guid mountId);

    private sealed class NoopWatch : IAsyncDisposable
    {
        public static readonly NoopWatch Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
