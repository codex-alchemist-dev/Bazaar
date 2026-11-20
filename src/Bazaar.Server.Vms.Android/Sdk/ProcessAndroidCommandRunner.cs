using System.Diagnostics;

namespace Bazaar.Server.Vms.Android.Sdk;

/// <summary><see cref="IAndroidCommandRunner"/> over <see cref="Process"/>: argument list, cleared environment, hard timeout, output cap.</summary>
public sealed class ProcessAndroidCommandRunner(int maxStdoutBytes = ProcessAndroidCommandRunner.DefaultMaxStdoutBytes) : IAndroidCommandRunner
{
    public const int DefaultMaxStdoutBytes = 32 * 1024 * 1024;

    private const int _maxStderrBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task<AndroidCommandResult> RunAsync(AndroidCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var process = new Process { StartInfo = Build(command, redirectOutput: true) };
        process.Start();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(command.Timeout);
        using var overflow = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var overflowed = false;

        try
        {
            var stdoutTask = ReadCappedAsync(process.StandardOutput.BaseStream, maxStdoutBytes, () => { overflowed = true; overflow.Cancel(); }, overflow.Token);
            var stderrTask = ReadCappedAsync(process.StandardError.BaseStream, _maxStderrBytes, null, overflow.Token);
            await WriteStdinAsync(process, command.Stdin, overflow.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(overflow.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new AndroidCommandResult(process.ExitCode, stdout, System.Text.Encoding.UTF8.GetString(stderr));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (overflowed)
            {
                throw new InvalidOperationException($"Output of '{Path.GetFileName(command.Executable)}' exceeded {maxStdoutBytes} bytes.");
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"'{Path.GetFileName(command.Executable)}' did not finish within {command.Timeout}.");
        }
    }

    /// <inheritdoc />
    public IAndroidProcess StartDetached(AndroidCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var process = new Process { StartInfo = Build(command, redirectOutput: true) };
        process.Start();
        process.StandardInput.Close();

        // Drain the pipes so the child never blocks on a full buffer; the output is not kept.
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        return new ProcessHandle(process);
    }

    private static ProcessStartInfo Build(AndroidCommand command, bool redirectOutput)
    {
        var info = new ProcessStartInfo(command.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment.Clear();
        foreach (var (key, value) in command.Environment)
        {
            info.Environment[key] = value;
        }

        return info;
    }

    private static async Task WriteStdinAsync(Process process, byte[]? stdin, CancellationToken ct)
    {
        try
        {
            if (stdin is { Length: > 0 })
            {
                await process.StandardInput.BaseStream.WriteAsync(stdin, ct).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The child closed its input early; that is not an error.
        }
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int cap, Action? onOverflow, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            var room = cap - (int)buffer.Length;
            if (read > room)
            {
                buffer.Write(chunk, 0, room);
                if (onOverflow is not null)
                {
                    onOverflow();
                    ct.ThrowIfCancellationRequested();
                }

                // Stderr is only truncated, but keep draining so the child is not blocked.
                while (await stream.ReadAsync(chunk, ct).ConfigureAwait(false) > 0)
                {
                }

                return buffer.ToArray();
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static void TryKill(Process process)
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

    private sealed class ProcessHandle(Process process) : IAndroidProcess
    {
        public bool HasExited => process.HasExited;

        public void Kill() => TryKill(process);

        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);

        public void Dispose() => process.Dispose();
    }
}
