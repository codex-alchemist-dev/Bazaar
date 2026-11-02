using System.Security.Cryptography;

namespace Bazaar.Server.Coding.Workspace;

/// <summary>
/// Fake <see cref="IGuestChannel"/> for tests and for building the harness before a VM exists.
/// Each argv has a script. An unscripted argv exits 127 with a stderr note, so a missing script shows up in the result.
/// </summary>
public sealed class InMemoryGuestChannel : IGuestChannel
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Script> _scripts = new(StringComparer.Ordinal);
    private readonly List<ExecRequest> _requests = [];
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private int _active;

    /// <summary>Every request received, in arrival order.</summary>
    public IReadOnlyList<ExecRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>Executions that have been entered and not yet returned.</summary>
    public int ActiveExecutions => Volatile.Read(ref _active);

    /// <summary>Scripts a process that exits with <paramref name="exitCode"/> after emitting <paramref name="output"/>.</summary>
    public void ScriptExit(IReadOnlyList<string> argv, int exitCode, params (string Stream, string Text)[] output) =>
        Add(argv, new Script(Hang: false, exitCode, TimedOut: false, output));

    /// <summary>Scripts a process that reports a guest-side timeout with <paramref name="exitCode"/>.</summary>
    public void ScriptTimeout(IReadOnlyList<string> argv, int exitCode) =>
        Add(argv, new Script(Hang: false, exitCode, TimedOut: true, []));

    /// <summary>Scripts a process that emits <paramref name="output"/> and then runs until the request's token is cancelled.</summary>
    public void ScriptHang(IReadOnlyList<string> argv, params (string Stream, string Text)[] output) =>
        Add(argv, new Script(Hang: true, ExitCode: -1, TimedOut: false, output));

    /// <inheritdoc />
    public async Task<ExecResult> ExecAsync(ExecRequest req, Func<OutputChunk, Task> onOutput, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(onOutput);

        Script script;
        lock (_gate)
        {
            _requests.Add(req);
            script = _scripts.TryGetValue(Key(req.Argv), out var found)
                ? found
                : new Script(Hang: false, ExitCode: 127, TimedOut: false, [("stderr", "not scripted: " + string.Join(' ', req.Argv))]);
            _active++;
        }

        try
        {
            long seq = 0;
            foreach (var (stream, text) in script.Output)
            {
                if (ct.IsCancellationRequested)
                {
                    return new ExecResult(-1, TimedOut: false, Cancelled: true);
                }

                await onOutput(new OutputChunk(seq++, stream, text));
            }

            if (script.Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return new ExecResult(-1, TimedOut: false, Cancelled: true);
                }
            }

            return new ExecResult(script.ExitCode, script.TimedOut, Cancelled: false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <inheritdoc />
    public async Task PutFileAsync(string guestPath, Stream content, string sha256, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();

        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 mismatch for {guestPath}: expected {sha256}, computed {actual}.");
        }

        lock (_gate)
        {
            _files[guestPath] = bytes;
        }
    }

    /// <inheritdoc />
    public async Task GetFileAsync(string guestPath, Stream sink, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guestPath);
        ArgumentNullException.ThrowIfNull(sink);

        byte[] bytes;
        lock (_gate)
        {
            if (!_files.TryGetValue(guestPath, out var stored))
            {
                throw new FileNotFoundException($"No file at guest path {guestPath}.", guestPath);
            }

            bytes = stored;
        }

        await sink.WriteAsync(bytes, ct);
    }

    private void Add(IReadOnlyList<string> argv, Script script)
    {
        ArgumentNullException.ThrowIfNull(argv);
        if (argv.Count == 0)
        {
            throw new ArgumentException("A script needs a non-empty argv.", nameof(argv));
        }

        lock (_gate)
        {
            _scripts[Key(argv)] = script;
        }
    }

    private static string Key(IReadOnlyList<string> argv) => string.Join('\u001f', argv);

    private sealed record Script(bool Hang, int ExitCode, bool TimedOut, IReadOnlyList<(string Stream, string Text)> Output);
}
