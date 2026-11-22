namespace Bazaar.Client.Calcinato.Modal;

/// <summary>
/// Hands typed letter sequences from Normal mode to the client-wide mnemonic router (<c>Bazaar.Ex</c>, doc 41). The
/// host implements it. The engine never looks inside a mnemonic.
/// </summary>
public interface IMnemonicBridge
{
    /// <summary>
    /// Runs the mnemonic when the whole typed sequence is one. Returns true when handled; the host does the work and
    /// returns what to speak (or null for a default).
    /// </summary>
    bool TryHandle(string typedSequence, out string? speech);

    /// <summary>
    /// True when a longer mnemonic starts with <paramref name="typedSequence"/>. A sequence that is an exact mnemonic and
    /// also a proper prefix of a longer one waits for more keys, because the engine has no timeout (Esc cancels).
    /// </summary>
    bool IsPrefix(string typedSequence);

    /// <summary>
    /// True when <paramref name="typedSequence"/> is exactly one mnemonic. Used by the leader sequence (doc 55, section
    /// 5.2), which needs to know whether Enter or an unambiguous next key should run it without running it first.
    /// </summary>
    bool IsExact(string typedSequence);
}
