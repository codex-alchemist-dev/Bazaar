using System.ComponentModel;
using System.Diagnostics;
using Bazaar.Client.SignIn;

namespace Bazaar.Client.Linux;

/// <summary>
/// Opens an address in the person's default browser through <c>xdg-open</c>. Only http and https addresses are launched, the
/// address is passed as one argv entry (never through a shell), and a failure returns false instead of throwing into the UI.
/// </summary>
internal sealed class XdgOpenUrlLauncher : IUrlLauncher
{
    private const string _program = "xdg-open";

    private readonly Func<ProcessStartInfo, bool> _start;

    public XdgOpenUrlLauncher()
        : this(StartProcess)
    {
    }

    /// <summary>For tests: <paramref name="start"/> replaces the real process start.</summary>
    internal XdgOpenUrlLauncher(Func<ProcessStartInfo, bool> start) => _start = start;

    /// <summary>The <c>xdg-open</c> arguments for <paramref name="address"/>, or null when it is not an http or https address.</summary>
    internal static IReadOnlyList<string>? BuildArguments(Uri address) =>
        address.IsAbsoluteUri && address.Scheme is "http" or "https" ? [address.AbsoluteUri] : null;

    public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (BuildArguments(address) is not { } arguments) return Task.FromResult(false);
        try
        {
            var info = new ProcessStartInfo(_program) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            return Task.FromResult(_start(info));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Task.FromResult(false);
        }
    }

    private static bool StartProcess(ProcessStartInfo info)
    {
        using var started = Process.Start(info);
        return started is not null;
    }
}
