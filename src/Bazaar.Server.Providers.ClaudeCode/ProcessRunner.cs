using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace Bazaar.Server.Providers.ClaudeCode;

/// <summary>Starts the real child process (docs/design/91 section 6). No shell, an exact environment, bounded output.</summary>
public sealed class ClaudeCodeProcessRunner(int maxLineChars) : IClaudeCodeProcessRunner
{
    public Task<IClaudeCodeRun> StartAsync(ClaudeCodeInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(invocation.Executable)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in invocation.Arguments)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        foreach (var (k, v) in invocation.Environment)
        {
            psi.Environment[k] = v;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        var run = new Run(process, invocation.StdinText, maxLineChars);
        return Task.FromResult<IClaudeCodeRun>(run);
    }

    private sealed class Run : IClaudeCodeRun
    {
        private const int _stderrCap = 2000;
        private readonly Process _process;
        private readonly int _maxLine;
        private readonly StringBuilder _stderr = new();
        private readonly Task _stdin;
        private readonly Task _stderrPump;

        public Run(Process process, string stdinText, int maxLine)
        {
            _process = process;
            _maxLine = maxLine;
            _stdin = Task.Run(async () =>
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdinText).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                    // The child ended before reading its input; the exit code tells the story.
                }
            });
            _stderrPump = Task.Run(async () =>
            {
                try
                {
                    var buf = new char[512];
                    int n;
                    while ((n = await process.StandardError.ReadAsync(buf.AsMemory()).ConfigureAwait(false)) > 0)
                    {
                        lock (_stderr)
                        {
                            _stderr.Append(buf, 0, n);
                            if (_stderr.Length > _stderrCap)
                            {
                                _stderr.Remove(0, _stderr.Length - _stderrCap);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
                {
                }
            });
        }

        public int? ExitCode
        {
            get
            {
                try
                {
                    return _process.HasExited ? _process.ExitCode : null;
                }
                catch (InvalidOperationException)
                {
                    return null;
                }
            }
        }

        public string StderrTail
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.ToString();
                }
            }
        }

        public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            var reader = _process.StandardOutput;
            var line = new StringBuilder();
            var buf = new char[8192];
            while (true)
            {
                var n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                for (var i = 0; i < n; i++)
                {
                    var c = buf[i];
                    if (c == '\n')
                    {
                        if (line.Length > 0)
                        {
                            yield return line.ToString().TrimEnd('\r');
                        }

                        line.Clear();
                    }
                    else
                    {
                        line.Append(c);
                        if (line.Length > _maxLine)
                        {
                            throw new InvalidDataException("A line from the Claude Code CLI exceeded the size limit.");
                        }
                    }
                }
            }

            if (line.Length > 0)
            {
                yield return line.ToString().TrimEnd('\r');
            }
        }

        public void KillTree()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            KillTree();
            try
            {
                await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            {
            }

            try
            {
                await Task.WhenAll(_stdin, _stderrPump).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            _process.Dispose();
        }
    }
}
