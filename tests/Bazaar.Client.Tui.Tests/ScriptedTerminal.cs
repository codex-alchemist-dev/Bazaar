using Bazaar.Client.Tui;

namespace Bazaar.Client.Tui.Tests;

/// <summary>
/// A terminal that reads from a script and records everything written to it. <see cref="SupportsKeys"/> picks the key widgets or
/// the line widgets. When the script runs out, the input has ended.
/// </summary>
public sealed class ScriptedTerminal : ITerminal
{
    private readonly Queue<string> _lines = new();
    private readonly Queue<TerminalKey> _keys = new();
    private readonly System.Text.StringBuilder _output = new();

    public ScriptedTerminal(bool supportsKeys = false)
    {
        SupportsKeys = supportsKeys;
    }

    public bool SupportsKeys { get; }

    public int Width => 80;

    /// <summary>Everything written, in order. Erased lines are kept too, because a leak on a redrawn line is still a leak.</summary>
    public string Output => _output.ToString();

    public int ErasedLines { get; private set; }

    public ScriptedTerminal Lines(params string[] lines)
    {
        foreach (var line in lines)
        {
            _lines.Enqueue(line);
        }

        return this;
    }

    public ScriptedTerminal Keys(params TerminalKey[] keys)
    {
        foreach (var key in keys)
        {
            _keys.Enqueue(key);
        }

        return this;
    }

    /// <summary>Queues each character as a key, then Enter.</summary>
    public ScriptedTerminal Typed(string text)
    {
        foreach (var character in text)
        {
            _keys.Enqueue(TerminalKey.Of(character));
        }

        _keys.Enqueue(new TerminalKey(TerminalKeyKind.Enter));
        return this;
    }

    public void Write(string text) => _output.Append(text);

    public void WriteLine(string text = "") => _output.Append(text).Append('\n');

    public string? ReadLine() => _lines.Count == 0 ? null : _lines.Dequeue();

    public TerminalKey ReadKey() => _keys.Count == 0 ? new TerminalKey(TerminalKeyKind.EndOfInput) : _keys.Dequeue();

    public void EraseLinesAbove(int count) => ErasedLines += count;
}
