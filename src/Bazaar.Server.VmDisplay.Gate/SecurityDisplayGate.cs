using System.Text;
using Bazaar.Server.Security.Credentials;
using Bazaar.Server.Security.KillSwitch;
using Bazaar.Server.Security.Taint;
using Bazaar.Server.VmDisplay.Abstractions;

namespace Bazaar.Server.VmDisplay.Gate;

/// <summary>
/// The display gate (docs/design/59, section 5; docs/design/15, "Reads for modules" and "Screen tools"). It reads the kill
/// switch, the agent's session taint and the leased-credential matcher, and fails closed: a missing reader, a failed read or a
/// timeout is a refusal. Refusal reasons are fixed strings; they never carry typed text, credential material or exception text.
/// </summary>
public sealed class SecurityDisplayGate : IDisplayGate
{
    internal const string ReasonNoAgent = "no agent";
    internal const string ReasonNotEnabled = "display gate not enabled";
    internal const string ReasonReadsUnavailable = "security reads unavailable";
    internal const string ReasonFrozen = "agent is frozen";
    internal const string ReasonReadFailed = "security read failed";
    internal const string ReasonReadTimedOut = "security read timed out";
    internal const string ReasonCredential = "input contains a leased credential";
    internal const string ReasonHardTaintAccount = "account browser input refused under hard taint";
    internal const string ReasonHardTaintAll = "input refused under hard taint";
    internal const string ReasonApprovalRequired = "approval required";

    private readonly DisplayGateOptions _options;
    private readonly IKillSwitchReader? _killSwitch;
    private readonly IAgentSessionTaintReader? _taint;
    private readonly ILeasedCredentialMatcher? _matcher;
    private readonly IBrowserSessionClassifier? _classifier;
    private readonly IDisplayInputApproval? _approval;
    private readonly TimeProvider _time;

    /// <summary>Creates the gate. A reader that is <see langword="null"/> makes the gate refuse everything.</summary>
    public SecurityDisplayGate(
        DisplayGateOptions options,
        IKillSwitchReader? killSwitch,
        IAgentSessionTaintReader? taint,
        ILeasedCredentialMatcher? matcher,
        IBrowserSessionClassifier? classifier = null,
        IDisplayInputApproval? approval = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _killSwitch = killSwitch;
        _taint = taint;
        _matcher = matcher;
        _classifier = classifier;
        _approval = approval;
        _time = time ?? TimeProvider.System;
    }

    private sealed class ReadTimedOutException : Exception;

    /// <inheritdoc />
    public async ValueTask<DisplayVerdict> CheckCaptureAsync(DisplayTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ct.ThrowIfCancellationRequested();
        if (Precheck(target) is { } early)
            return early;
        try
        {
            return await IsFrozenAsync(target.AgentId, ct).ConfigureAwait(false) ? Deny(ReasonFrozen) : DisplayVerdict.Allow;
        }
        catch (Exception e) when (Classify(e, ct) is { } deny)
        {
            return deny;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DisplayVerdict> CheckInputAsync(DisplayTarget target, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(actions);
        ct.ThrowIfCancellationRequested();
        if (Precheck(target) is { } early)
            return early;
        try
        {
            if (await IsFrozenAsync(target.AgentId, ct).ConfigureAwait(false))
                return Deny(ReasonFrozen);

            if (await ContainsCredentialAsync(target.AgentId, actions, ct).ConfigureAwait(false))
                return Deny(ReasonCredential);

            TaintState taint = await Read(token => _taint!.GetActiveTaintAsync(target.AgentId, token), ct).ConfigureAwait(false);
            if (taint != TaintState.Hard)
                return DisplayVerdict.Allow;

            if (await IsAccountBrowserAsync(target, ct).ConfigureAwait(false))
                return Deny(ReasonHardTaintAccount);

            switch (_options.InputUnderHardTaint)
            {
                case InputUnderHardTaint.AccountBrowsers:
                    return DisplayVerdict.Allow;
                case InputUnderHardTaint.Approval:
                    if (_approval is null)
                        return Deny(ReasonApprovalRequired);
                    bool approved = await Read(token => _approval.IsApprovedAsync(target, actions, token), ct).ConfigureAwait(false);
                    return approved ? DisplayVerdict.Allow : Deny(ReasonApprovalRequired);
                default:
                    return Deny(ReasonHardTaintAll);
            }
        }
        catch (Exception e) when (Classify(e, ct) is { } deny)
        {
            return deny;
        }
    }

    private DisplayVerdict? Precheck(DisplayTarget target)
    {
        if (string.IsNullOrWhiteSpace(target.AgentId))
            return Deny(ReasonNoAgent);
        if (!_options.Enabled)
            return Deny(ReasonNotEnabled);
        if (_killSwitch is null || _taint is null || _matcher is null)
            return Deny(ReasonReadsUnavailable);
        return null;
    }

    // Maps a failure to a fixed refusal. Caller cancellation is not a refusal: returning null lets it propagate.
    private static DisplayVerdict? Classify(Exception e, CancellationToken ct)
    {
        if (ct.IsCancellationRequested && e is OperationCanceledException)
            return null;
        return Deny(e is ReadTimedOutException ? ReasonReadTimedOut : ReasonReadFailed);
    }

    private static DisplayVerdict Deny(string reason) => DisplayVerdict.Deny(reason);

    private ValueTask<bool> IsFrozenAsync(string agentId, CancellationToken ct) => Read(token => _killSwitch!.IsFrozenAsync(agentId, token), ct);

    // Checks each typed text on its own, then everything the batch types as one string, in order. The joined string also
    // carries the characters of plain letter, digit and space key strokes, so a value cannot be slipped past the matcher by
    // splitting it across actions or by pressing its characters one key at a time. Shifted digits and punctuation depend on
    // the guest's keyboard layout and are not mapped; neither is a value split across batches (doc 15, question 5).
    private async ValueTask<bool> ContainsCredentialAsync(string agentId, IReadOnlyList<InputAction> actions, CancellationToken ct)
    {
        List<string>? texts = null;
        StringBuilder? typed = null;
        int pieces = 0;
        foreach (InputAction action in actions)
        {
            switch (action)
            {
                case TypeText { Text: { Length: > 0 } text }:
                    (texts ??= []).Add(text);
                    (typed ??= new StringBuilder()).Append(text);
                    pieces++;
                    break;
                case KeyStroke stroke when KeyCharacter(stroke) is { } c:
                    (typed ??= new StringBuilder()).Append(c);
                    pieces++;
                    break;
            }
        }

        if (texts is not null)
        {
            foreach (string text in texts)
            {
                if (await Read(token => _matcher!.ContainsLeasedCredentialAsync(agentId, text, token), ct).ConfigureAwait(false))
                    return true;
            }
        }

        // One piece that is a typed text was checked above; anything else (several pieces, or key strokes alone) is checked joined.
        if (typed is null || (pieces == 1 && texts is not null))
            return false;
        string joined = typed.ToString();
        return await Read(token => _matcher!.ContainsLeasedCredentialAsync(agentId, joined, token), ct).ConfigureAwait(false);
    }

    // The character a key stroke types on a US layout, for the keys whose character does not depend on the layout much:
    // letters (Shift gives upper case), unshifted digits and Space. A stroke with Control, Alt or Meta types nothing.
    internal static char? KeyCharacter(KeyStroke stroke)
    {
        if ((stroke.Modifiers & ~KeyModifiers.Shift) != KeyModifiers.None || stroke.Key is not { } key)
            return null;
        bool shift = (stroke.Modifiers & KeyModifiers.Shift) != KeyModifiers.None;
        if (key.Length == 4 && key.StartsWith("Key", StringComparison.Ordinal) && key[3] is >= 'A' and <= 'Z')
            return shift ? key[3] : char.ToLowerInvariant(key[3]);
        if (!shift && key.Length == 6 && key.StartsWith("Digit", StringComparison.Ordinal) && key[5] is >= '0' and <= '9')
            return key[5];
        if (key == "Space")
            return ' ';
        return null;
    }

    // No classifier, or one that fails or times out, means Account: the strictest class.
    private async ValueTask<bool> IsAccountBrowserAsync(DisplayTarget target, CancellationToken ct)
    {
        if (target.BrowserSessionId is null)
            return false;
        if (_classifier is null)
            return true;
        try
        {
            return await Read(token => _classifier.ClassifyAsync(target, token), ct).ConfigureAwait(false) != BrowserSessionClass.Anonymous;
        }
        catch (Exception e) when (!(ct.IsCancellationRequested && e is OperationCanceledException))
        {
            return true;
        }
    }

    // Runs one read under the timeout. A read that ignores its token cannot hang the gate: the wait ends on timeout or caller cancellation.
    private async ValueTask<T> Read<T>(Func<CancellationToken, ValueTask<T>> read, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ReadTimeoutSeconds), _time);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration = linked.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), interrupted);

        Task<T> task = read(linked.Token).AsTask();
        Task winner = await Task.WhenAny(task, interrupted.Task).ConfigureAwait(false);
        if (winner == task)
            return await task.ConfigureAwait(false);

        // Nobody will await the abandoned read: observe its outcome so it never surfaces as unobserved.
        _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        ct.ThrowIfCancellationRequested();
        throw new ReadTimedOutException();
    }
}
