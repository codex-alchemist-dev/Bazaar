using Bazaar.Client.Calcinato.Abstractions;

namespace Bazaar.Client.Calcinato.Modal;

/// <summary>
/// The leader sequence (doc 55, section 5.2). In Normal mode the leader key (Space by default) starts a client-wide
/// mnemonic. Every key after it is appended to the sequence and offered to the bridge, with no motion lookup, so a
/// mnemonic whose first letter is a motion key (<c>ngc1</c>, <c>nrfdrm</c>, <c>unread</c>, <c>intr</c>) can be typed.
/// </summary>
/// <remarks>
/// While the sequence is a proper prefix of a longer mnemonic the engine waits. An exact match that no longer mnemonic
/// extends runs at once. An exact match that is also a prefix runs on Enter, or when the next key cannot extend it. In
/// that case the key that broke the match is discarded. Esc and any other non-text key cancel the sequence. There is no
/// timeout.
/// </remarks>
public sealed partial class ModalEngine
{
    private bool _leading;

    /// <summary>True while a leader sequence is being typed.</summary>
    public bool IsLeading => _leading;

    private void StartLeader(List<ModalEffect> fx)
    {
        _leading = true;
        _typedSequence.Clear();
        Status(fx, "Mnemonic: ");
    }

    private void HandleLeader(TerminalKey key, List<ModalEffect> fx)
    {
        switch (key.Kind)
        {
            case TerminalKeyKind.Text:
                LeaderAppend(key.Text[0], fx);
                return;

            case TerminalKeyKind.Named when key.Name == KeyName.Enter:
                SubmitLeader(fx);
                return;

            case TerminalKeyKind.Named when key.Name == KeyName.Backspace:
                if (_typedSequence.Length == 0)
                {
                    CancelLeader(fx, "Mnemonic cancelled.");
                    return;
                }

                _typedSequence.Length--;
                ShowLeader(fx);
                return;

            default:
                // Escape, arrows and control bytes all cancel: none of them is part of a mnemonic.
                CancelLeader(fx, "Mnemonic cancelled.");
                return;
        }
    }

    private void LeaderAppend(char c, List<ModalEffect> fx)
    {
        string previous = _typedSequence.ToString();
        string next = previous + c;
        if (IsLeaderPrefix(next))
        {
            _typedSequence.Append(c);
            ShowLeader(fx);
            return;
        }

        if (IsLeaderExact(next))
        {
            EndLeader();
            RunMnemonic(next, fx);
            return;
        }

        // The sequence so far was an exact match that the next key cannot extend, so it is the only reading. The key is dropped.
        if (previous.Length > 0 && IsLeaderExact(previous))
        {
            EndLeader();
            RunMnemonic(previous, fx);
            return;
        }

        CancelLeader(fx, $"No command: {next}");
    }

    private void SubmitLeader(List<ModalEffect> fx)
    {
        string typed = _typedSequence.ToString();
        if (typed.Length == 0)
        {
            CancelLeader(fx, "Mnemonic cancelled.");
            return;
        }

        if (IsLeaderExact(typed))
        {
            EndLeader();
            RunMnemonic(typed, fx);
            return;
        }

        CancelLeader(fx, $"No command: {typed}");
    }

    // The built-in intr is part of the leader vocabulary even though the bridge does not own it (Bazaar.Ex reserves i).
    private bool IsLeaderPrefix(string typed) =>
        (_mnemonics?.IsPrefix(typed) ?? false)
        || (typed.Length < _interruptMnemonic.Length && _interruptMnemonic.StartsWith(typed, StringComparison.Ordinal));

    private bool IsLeaderExact(string typed) =>
        typed == _interruptMnemonic || (_mnemonics?.IsExact(typed) ?? false);

    private void ShowLeader(List<ModalEffect> fx) => Status(fx, $"Mnemonic: {_typedSequence}");

    private void EndLeader()
    {
        _leading = false;
        _typedSequence.Clear();
    }

    private void CancelLeader(List<ModalEffect> fx, string text)
    {
        EndLeader();
        Status(fx, text);
    }
}
