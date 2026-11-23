using System.Diagnostics;
using System.Text;

namespace Bazaar.Server.VmEgress.Enforcers;

/// <summary>
/// <see cref="IHostCommandRunner"/> over <see cref="Process"/>. Only <see cref="HostTool"/> values run, each at a fixed absolute
/// path (PowerShell under System32, nft under /usr/sbin or /sbin), so a planted executable on PATH or in the current directory is
/// never launched. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>; no shell is involved. Output is capped, a
/// run has a timeout, and cancellation or timeout kills the whole process tree. The OS checks live in this class only.
/// </summary>
public sealed class ProcessHostCommandRunner : IHostCommandRunner
{
    /// <summary>Default per-command timeout.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const int _maxOutputChars = 1024 * 1024;
    private static readonly string[] _nftPaths = ["/usr/sbin/nft", "/sbin/nft"];

    private readonly TimeSpan _timeout;
    private readonly Func<HostTool, string> _resolvePath;

    /// <summary>Creates the runner with the default timeout.</summary>
    public ProcessHostCommandRunner()
        : this(DefaultTimeout)
    {
    }

    /// <summary>Creates the runner with <paramref name="timeout"/> per command.</summary>
    public ProcessHostCommandRunner(TimeSpan timeout)
        : this(timeout, ResolveSystemPath)
    {
    }

    // Tests only: a resolver that points the allowed tool names at harmless binaries.
    internal ProcessHostCommandRunner(TimeSpan timeout, Func<HostTool, string> resolvePath)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        }

        _timeout = timeout;
        _resolvePath = resolvePath;
    }

    /// <inheritdoc />
    public async Task<HostCommandResult> RunAsync(HostCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(command.Tool))
        {
            throw new ArgumentException($"The tool '{(int)command.Tool}' is not allowed.", nameof(command));
        }

        var info = new ProcessStartInfo(_resolvePath(command.Tool))
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (command.Environment is not null)
        {
            foreach (var (key, value) in command.Environment)
            {
                info.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EgressEnforcementException($"The {command.Tool} executable could not be started.", e);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            var stdout = ReadCappedAsync(process.StandardOutput, cts.Token);
            var stderr = ReadCappedAsync(process.StandardError, cts.Token);
            await WriteInputAsync(process, command.StandardInput, cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return new HostCommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Kill(process);
            throw new EgressEnforcementException($"The {command.Tool} command timed out.");
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    // The only OS checks: which absolute path an allowed tool has on this host.
    private static string ResolveSystemPath(HostTool tool)
    {
        switch (tool)
        {
            case HostTool.PowerShell:
                if (!OperatingSystem.IsWindows())
                {
                    throw new PlatformNotSupportedException("PowerShell is allowed on Windows only.");
                }

                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            case HostTool.Nft:
                if (!OperatingSystem.IsLinux())
                {
                    throw new PlatformNotSupportedException("nft is allowed on Linux only.");
                }

                return _nftPaths.FirstOrDefault(File.Exists)
                    ?? throw new EgressEnforcementException("nft was not found at /usr/sbin/nft or /sbin/nft.");
            default:
                throw new ArgumentException("The tool is not allowed.", nameof(tool));
        }
    }

    private static async Task WriteInputAsync(Process process, string input, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The tool exited before reading all of its input; its exit code and stderr say why.
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > _maxOutputChars)
            {
                throw new EgressEnforcementException("The command output is larger than the limit.");
            }
        }

        return text.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process already ended.
        }
    }
}
