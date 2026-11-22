using System.Text;
using System.Text.RegularExpressions;
using Bazaar.Server.Coding.Workspace;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>How one guest command ended. <see cref="ExitCode"/> is null when the guest reported none.</summary>
internal sealed record GuestCommandResult(int? ExitCode, bool TimedOut, bool Faulted, string Stdout, string Stderr, bool Truncated)
{
    /// <summary>True only for exit 0 with no timeout and no fault.</summary>
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Faulted;
}

/// <summary>
/// Runs one argv in the guest over <see cref="IGuestChannel"/> with a host-side timeout, a bounded output buffer and a secret
/// redactor. Shared by <c>shell</c> and <c>run_checks</c>. Nothing here starts a process on the server host.
/// </summary>
internal static class GuestCommand
{
    /// <summary>Extra characters kept past the cap so a secret that straddles the cap is redacted whole before the cut.</summary>
    private const int RedactionSlack = 512;

    /// <summary>
    /// Runs <paramref name="argv"/> in <paramref name="cwd"/> (relative to the worktree). The environment is empty.
    /// Cancellation by <paramref name="cancellationToken"/> stops the guest process and is rethrown; the timeout is a result.
    /// </summary>
    /// <exception cref="ArgumentException">The cwd leaves the worktree (see <see cref="GuestPath"/>).</exception>
    public static async Task<GuestCommandResult> RunAsync(
        GuestWorktreeTarget target,
        IReadOnlyList<string> argv,
        string cwd,
        TimeSpan timeout,
        int outputCap,
        Action<OutputChunk>? observe,
        CancellationToken cancellationToken)
    {
        var guestCwd = GuestPath.Resolve(target.GuestRoot, cwd);
        var request = new ExecRequest([.. argv], guestCwd, new Dictionary<string, string>(), timeout);

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var limit = outputCap + RedactionSlack;
        var truncated = false;
        var gate = new object();

        Task Sink(OutputChunk chunk)
        {
            lock (gate)
            {
                observe?.Invoke(chunk);
                var buffer = chunk.Stream == "stdout" ? stdout : stderr;
                var room = limit - buffer.Length;
                if (room <= 0)
                {
                    truncated = true;
                }
                else if (chunk.Text.Length > room)
                {
                    buffer.Append(chunk.Text, 0, room);
                    truncated = true;
                }
                else
                {
                    buffer.Append(chunk.Text);
                }
            }

            return Task.CompletedTask;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        ExecResult? raw = null;
        var faulted = false;
        var hostTimedOut = false;
        try
        {
            raw = await target.Channel.ExecAsync(request, Sink, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            // The host-side timer fired: reported as a timeout, never as a success.
            hostTimedOut = true;
        }
        catch (Exception)
        {
            // A guest or wire fault. Detail stays out of the model's view and out of any row.
            faulted = true;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // A guest that stops on the host timer's token may report "cancelled"; the host knows its own timer fired.
        hostTimedOut |= timeoutSource.IsCancellationRequested;
        var timedOut = hostTimedOut || raw is { TimedOut: true };
        if (raw is { Cancelled: true } && !hostTimedOut)
        {
            // The guest says it was cancelled but the caller did not ask: the outcome is unknown, not a success.
            faulted = true;
        }

        // ExecResult uses -1 for "no exit code reported".
        int? exit = raw is null || raw.ExitCode == -1 ? null : raw.ExitCode;

        string stdoutText;
        string stderrText;
        var cut = truncated;
        lock (gate)
        {
            stdoutText = Finish(stdout, outputCap, ref cut);
            stderrText = Finish(stderr, outputCap, ref cut);
        }

        return new GuestCommandResult(exit, timedOut, faulted, stdoutText, stderrText, cut);
    }

    private static string Finish(StringBuilder buffer, int cap, ref bool truncated)
    {
        var text = SecretRedactor.Redact(buffer.ToString());
        if (text.Length > cap)
        {
            text = text[..cap];
            truncated = true;
        }

        return text;
    }
}

/// <summary>
/// Replaces common secret shapes in command output with <c>[redacted]</c>. A backstop: the call also starts with an empty
/// environment, so no secret should be there to print.
/// </summary>
internal static partial class SecretRedactor
{
    private const string Mask = "[redacted]";

    /// <summary>Returns <paramref name="text"/> with secret-shaped values masked.</summary>
    public static string Redact(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        text = PrivateKey().Replace(text, Mask);
        text = Tokens().Replace(text, Mask);
        text = Bearer().Replace(text, "Bearer " + Mask);
        return Assignment().Replace(text, m => m.Groups[1].Value + Mask);
    }

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-ant-[A-Za-z0-9_\-]{10,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9\-]{10,})")]
    private static partial Regex Tokens();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9._~+/=\-]{16,}", RegexOptions.IgnoreCase)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"((?:password|passwd|secret|token|api[_-]?key)\s*[=:]\s*)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Assignment();
}
