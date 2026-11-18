#pragma warning disable CA1861 // Test data is written inline as constant arrays.
using Bazaar.Client.Calcinato.Abstractions;
using Bazaar.Client.Calcinato.Modal;

namespace Bazaar.Client.Calcinato.Tests.Modal;

/// <summary>In-memory screen. Lines hold the scrollback first, then the visible rows.</summary>
internal sealed class FakeScreen : IScreen
{
    public FakeScreen(int rows, int cols, IReadOnlyList<string> scrollback, IReadOnlyList<string> visible)
    {
        Rows = rows;
        Cols = cols;
        ScrollbackCount = scrollback.Count;
        Lines.AddRange(scrollback);
        for (int i = 0; i < rows; i++)
        {
            Lines.Add(i < visible.Count ? visible[i] : string.Empty);
        }
    }

    public List<string> Lines { get; } = new();

    public int Cols { get; }

    public int Rows { get; }

    public int ScrollbackCount { get; set; }

    public bool IsAlternateScreen => false;

    public int CursorRow { get; set; }

    public int CursorCol { get; set; }

    public string Title => string.Empty;

    public string GetLineText(int absoluteRow)
    {
        if (absoluteRow < 0 || absoluteRow >= Lines.Count)
        {
            return string.Empty;
        }

        return Lines[absoluteRow].TrimEnd();
    }

    /// <summary>Appends a line to the scrollback, as output that scrolls the screen does.</summary>
    public void Scroll(string line)
    {
        Lines.Insert(ScrollbackCount, line);
        ScrollbackCount++;
    }
}

/// <summary>Mnemonic bridge with a fixed set of mnemonics. Records every call.</summary>
internal sealed class FakeBridge : IMnemonicBridge
{
    private readonly HashSet<string> _mnemonics;

    public FakeBridge(params string[] mnemonics)
    {
        _mnemonics = new HashSet<string>(mnemonics, StringComparer.Ordinal);
    }

    public List<string> Handled { get; } = new();

    public bool TryHandle(string typedSequence, out string? speech)
    {
        if (_mnemonics.Contains(typedSequence))
        {
            Handled.Add(typedSequence);
            speech = $"Ran {typedSequence}";
            return true;
        }

        speech = null;
        return false;
    }

    public bool IsPrefix(string typedSequence) =>
        _mnemonics.Any(m => m.Length > typedSequence.Length && m.StartsWith(typedSequence, StringComparison.Ordinal));

    public bool IsExact(string typedSequence) => _mnemonics.Contains(typedSequence);
}

/// <summary>Suggestion source with a fixed answer. An optional hook runs before the answer returns.</summary>
internal sealed class FakeSuggestions : ISuggestionSource
{
    public string? Answer { get; set; }

    public List<string> Asked { get; } = new();

    public Action? BeforeAnswer { get; set; }

    public ValueTask<string?> SuggestAsync(string inputLine, CancellationToken ct)
    {
        Asked.Add(inputLine);
        BeforeAnswer?.Invoke();
        return ValueTask.FromResult(Answer);
    }
}

/// <summary>Input helpers for driving the engine in tests.</summary>
internal static class Input
{
    public static TerminalKey Esc => TerminalKey.Named(KeyName.Escape);

    public static TerminalKey Enter => TerminalKey.Named(KeyName.Enter);

    public static TerminalKey Named(KeyName name, bool shift = false) => TerminalKey.Named(name, shift);

    /// <summary>Types each character of the text as a separate key press and returns all effects.</summary>
    public static List<ModalEffect> Keys(ModalEngine engine, string text)
    {
        var all = new List<ModalEffect>();
        foreach (char c in text)
        {
            all.AddRange(engine.Handle(TerminalKey.Typed(c)).Effects);
        }

        return all;
    }

    public static List<ModalEffect> Press(ModalEngine engine, TerminalKey key) => engine.Handle(key).Effects.ToList();

    public static byte[] Bytes(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.SendBytes>().SelectMany(b => b.Bytes).ToArray();

    public static string? LastStatus(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.StatusLine>().LastOrDefault()?.Text;

    public static string? LastSpeech(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.Speech>().LastOrDefault()?.Text;

    public static ModalMode? LastMode(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.ModeChanged>().LastOrDefault()?.Mode;

    public static ExCommand? CommandOf(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.Command>().LastOrDefault()?.Request;

    public static string? YankOf(IEnumerable<ModalEffect> effects) =>
        effects.OfType<ModalEffect.Yank>().LastOrDefault()?.Text;

    public static IEnumerable<T> Only<T>(IEnumerable<ModalEffect> effects) where T : ModalEffect => effects.OfType<T>();
}
