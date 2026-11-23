using System.Diagnostics;
using System.Text;

namespace Bazaar.Client.Athanor.Gtk;

/// <summary>
/// Starts the GTK helper as a child process with redirected standard streams (doc 57 section 2.1). The helper path
/// ending in <c>.dll</c> runs through <c>dotnet</c>; anything else is run as an executable.
/// </summary>
public sealed class ProcessEditorHelperLauncher : IEditorHelperLauncher
{
    private const string _disableSandboxVariable = "WEBKIT_DISABLE_SANDBOX_THIS_IS_DANGEROUS";

    private readonly string _command;
    private readonly IReadOnlyList<string> _arguments;
    private readonly bool _allowNoSandbox;
    private readonly int _maxLineChars;

    /// <summary>Creates a launcher from a ready command line.</summary>
    public ProcessEditorHelperLauncher(string command, IReadOnlyList<string> arguments, bool allowNoSandbox = false, int maxLineChars = 16 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(arguments);
        _command = command;
        _arguments = arguments;
        _allowNoSandbox = allowNoSandbox;
        _maxLineChars = maxLineChars;
    }

    /// <summary>
    /// Builds the launcher for the helper executable or assembly at <paramref name="helperPath"/> and the Monaco bundle
    /// directory. The bundle path is the only thing the helper loads (no network).
    /// </summary>
    public static ProcessEditorHelperLauncher ForHelper(string helperPath, string bundleDirectory, GtkEditorHostOptions options, string? title = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentNullException.ThrowIfNull(options);
        var args = new List<string>();
        string command = helperPath;
        if (helperPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            command = "dotnet";
            args.Add(helperPath);
        }

        args.Add("--bundle");
        args.Add(bundleDirectory);
        if (title is not null)
        {
            args.Add("--title");
            args.Add(title);
        }

        if (options.AllowNoSandbox)
        {
            args.Add("--allow-no-sandbox");
        }

        return new ProcessEditorHelperLauncher(command, args, options.AllowNoSandbox, (int)Math.Min(int.MaxValue / 2, options.MaxPayloadBytes + 4096));
    }

    public ValueTask<IEditorHelperConnection> LaunchAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(_command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in _arguments)
        {
            info.ArgumentList.Add(argument);
        }

        if (!_allowNoSandbox)
        {
            // The helper also refuses to run with this set; removing it here keeps a stray inherited value from blocking it.
            info.Environment.Remove(_disableSandboxVariable);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult(SafeExitCode(process));
        process.Start();
        return ValueTask.FromResult<IEditorHelperConnection>(new ProcessConnection(process, exited.Task, _maxLineChars));
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private sealed class ProcessConnection : IEditorHelperConnection
    {
        private const int _stderrLines = 20;

        private readonly Process _process;
        private readonly StreamWriter _stdin;
        private readonly BoundedLineReader _stdout;
        private readonly Queue<string> _stderr = new();
        private readonly Task _stderrPump;

        public ProcessConnection(Process process, Task<int> exited, int maxLineChars)
        {
            _process = process;
            Exited = exited;
            _stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
            _stdout = new BoundedLineReader(process.StandardOutput, maxLineChars);
            _stderrPump = Task.Run(PumpStderrAsync);
        }

        public Task<int> Exited { get; }

        public string? DiagnosticTail
        {
            get
            {
                lock (_stderr)
                {
                    return _stderr.Count == 0 ? null : string.Join('\n', _stderr);
                }
            }
        }

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
        {
            await _stdin.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => _stdout.ReadLineAsync(cancellationToken);

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        public async ValueTask DisposeAsync()
        {
            Kill();
            try
            {
                await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The pump ends when the pipe closes; do not wait for it forever.
            }

            _process.Dispose();
        }

        private async Task PumpStderrAsync()
        {
            try
            {
                string? line;
                while ((line = await _process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (_stderr)
                    {
                        _stderr.Enqueue(line);
                        while (_stderr.Count > _stderrLines)
                        {
                            _stderr.Dequeue();
                        }
                    }
                }
            }
            catch (IOException)
            {
                // The pipe closed.
            }
            catch (ObjectDisposedException)
            {
                // The process was disposed.
            }
        }
    }
}

/// <summary>Reads newline-terminated lines and refuses a line longer than the limit, so a runaway helper cannot exhaust memory.</summary>
internal sealed class BoundedLineReader
{
    private readonly TextReader _reader;
    private readonly int _maxChars;
    private readonly char[] _buffer = new char[8192];
    private int _position;
    private int _length;

    public BoundedLineReader(TextReader reader, int maxChars)
    {
        _reader = reader;
        _maxChars = maxChars;
    }

    /// <summary>The next line without its terminator, or null at end of input. Throws <see cref="InvalidDataException"/> over the limit.</summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        while (true)
        {
            if (_position >= _length)
            {
                _length = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _position = 0;
                if (_length == 0)
                {
                    return line.Length > 0 ? Trim(line) : null;
                }
            }

            ReadOnlySpan<char> pending = _buffer.AsSpan(_position, _length - _position);
            int newline = pending.IndexOf('\n');
            int take = newline < 0 ? pending.Length : newline;
            if (line.Length + take > _maxChars)
            {
                throw new InvalidDataException("A line from the helper is over the size limit.");
            }

            line.Append(pending[..take]);
            if (newline >= 0)
            {
                _position += take + 1;
                return Trim(line);
            }

            _position = _length;
        }
    }

    private static string Trim(StringBuilder line)
    {
        if (line.Length > 0 && line[^1] == '\r')
        {
            line.Length--;
        }

        return line.ToString();
    }
}
