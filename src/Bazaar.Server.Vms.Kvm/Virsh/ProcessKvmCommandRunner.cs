using System.ComponentModel;
using System.Diagnostics;

namespace Bazaar.Server.Vms.Kvm.Virsh;

/// <summary>
/// Runs <c>virsh</c> and <c>qemu-img</c> with <see cref="Process"/>. Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so nothing
/// is shell-parsed, and any other program is refused. The child runs with <c>LC_ALL=C</c> so its messages do not depend on the host
/// locale (the adapter matches libvirt's English error text). Each output stream is capped; a cancelled, timed-out or over-long call
/// kills the whole process tree.
/// </summary>
public sealed class ProcessKvmCommandRunner : IKvmCommandRunner
{
    /// <summary>The most characters kept from standard output or standard error of one call.</summary>
    public const int DefaultMaxOutputChars = 1024 * 1024;

    /// <summary>The default limit for one call. Graceful shutdown polls with separate short calls, so no single call runs long.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    private static readonly string[] _allowedPrograms = ["virsh", "qemu-img"];

    private readonly TimeSpan _timeout;
    private readonly IReadOnlyCollection<string> _programs;
    private readonly int _maxOutputChars;

    public ProcessKvmCommandRunner(TimeSpan? timeout = null)
        : this(timeout, _allowedPrograms, DefaultMaxOutputChars)
    {
    }

    internal ProcessKvmCommandRunner(TimeSpan? timeout, IReadOnlyCollection<string> allowedPrograms, int maxOutputChars)
    {
        _timeout = timeout ?? DefaultTimeout;
        _programs = allowedPrograms;
        _maxOutputChars = maxOutputChars;
    }

    /// <inheritdoc />
    public async Task<KvmCommandResult> RunAsync(string program, IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!_programs.Contains(program))
        {
            throw new ArgumentException($"Program '{program}' is not allowed; only virsh and qemu-img run.", nameof(program));
        }

        var startInfo = CreateStartInfo(program, arguments, standardInput is not null);

        using var process = StartProcess(program, startInfo);
        var exceeded = 0;
        void OnExceeded()
        {
            Interlocked.Exchange(ref exceeded, 1);
            KillTree(process);
        }

        var stdout = ReadCappedAsync(process.StandardOutput, _maxOutputChars, OnExceeded);
        var stderr = ReadCappedAsync(process.StandardError, _maxOutputChars, OnExceeded);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        try
        {
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), limit.Token);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            KillTree(process);
            throw new KvmCommandException($"'{program}' did not finish within {_timeout}.");
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        var output = await stdout;
        var error = await stderr;
        if (Volatile.Read(ref exceeded) == 1)
        {
            throw new KvmCommandException($"'{program}' wrote more than {_maxOutputChars} characters; it was stopped.");
        }

        return new KvmCommandResult(process.ExitCode, output, error);
    }

    /// <summary>
    /// Builds the process start info: the arguments go through <see cref="ProcessStartInfo.ArgumentList"/> one by one, and the environment
    /// is the service's own plus <c>LC_ALL=C</c> (and no <c>LANGUAGE</c>, which would override the locale for gettext).
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string program, IReadOnlyList<string> arguments, bool redirectInput)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment.Remove("LANGUAGE");
        return startInfo;
    }

    /// <summary>
    /// Reads <paramref name="reader"/> to the end but keeps at most <paramref name="maxChars"/> characters. The first time more arrives,
    /// <paramref name="onExceeded"/> runs once (the caller stops the process) and the rest is discarded so the pipe never blocks.
    /// </summary>
    internal static async Task<string> ReadCappedAsync(TextReader reader, int maxChars, Action onExceeded)
    {
        var buffer = new char[4096];
        var kept = new System.Text.StringBuilder();
        var over = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory());
            if (read == 0)
            {
                return kept.ToString();
            }

            if (over)
            {
                continue;
            }

            var room = maxChars - kept.Length;
            kept.Append(buffer, 0, Math.Min(read, room));
            if (read > room)
            {
                over = true;
                onExceeded();
            }
        }
    }

    private static Process StartProcess(string program, ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo) ?? throw new KvmCommandException($"Could not start '{program}'.");
        }
        catch (Win32Exception ex)
        {
            throw new KvmCommandException($"Could not start '{program}': {ex.Message}", ex);
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
