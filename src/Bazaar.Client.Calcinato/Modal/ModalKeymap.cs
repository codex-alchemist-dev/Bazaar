using System.Collections.ObjectModel;

namespace Bazaar.Client.Calcinato.Modal;

/// <summary>Normal-mode actions a single typed character can trigger.</summary>
public enum ModalAction
{
    MoveLeft,
    MoveDown,
    MoveUp,
    MoveRight,
    WordForward,
    WordBackward,
    WordEnd,
    LineStart,
    LineEnd,
    HalfPageDown,
    HalfPageUp,
    PageDown,
    PageUp,
    GoBottom,
    GPrefix,
    SearchForward,
    SearchBackward,
    SearchNext,
    SearchPrevious,
    SetMark,
    JumpMark,
    InsertBefore,
    InsertAfter,
    InsertLineStart,
    InsertLineEnd,
    VisualChar,
    VisualLine,
    Yank,
    CommandLine,
}

/// <summary>
/// Maps typed characters to Normal and Visual actions. A character bound here is a motion or command and is never
/// handed to the mnemonic bridge. An unbound character starts a typed mnemonic instead. Remapping is done by binding
/// or unbinding characters.
/// </summary>
/// <remarks>
/// Digits 1 to 9 are never bindable (they start counts). <c>0</c> is bindable and acts as a count digit only after
/// another digit. Named keys (arrows, Home, End, PageUp, PageDown, Insert, Escape) are fixed.
/// </remarks>
public sealed class ModalKeymap
{
    private readonly Dictionary<char, ModalAction> _bindings = new();

    /// <summary>Creates an empty keymap. Use <see cref="CreateDefault"/> for the shipped bindings.</summary>
    public ModalKeymap()
    {
    }

    /// <summary>The bindings: <c>h j k l w b e 0 $ G g u d f B n N / ? m ' i a I A v V y :</c>.</summary>
    public static ModalKeymap CreateDefault()
    {
        var map = new ModalKeymap();
        map.Bind('h', ModalAction.MoveLeft);
        map.Bind('j', ModalAction.MoveDown);
        map.Bind('k', ModalAction.MoveUp);
        map.Bind('l', ModalAction.MoveRight);
        map.Bind('w', ModalAction.WordForward);
        map.Bind('b', ModalAction.WordBackward);
        map.Bind('e', ModalAction.WordEnd);
        map.Bind('0', ModalAction.LineStart);
        map.Bind('$', ModalAction.LineEnd);
        map.Bind('G', ModalAction.GoBottom);
        map.Bind('g', ModalAction.GPrefix);
        map.Bind('u', ModalAction.HalfPageUp);
        map.Bind('d', ModalAction.HalfPageDown);
        map.Bind('f', ModalAction.PageDown);
        map.Bind('B', ModalAction.PageUp);
        map.Bind('n', ModalAction.SearchNext);
        map.Bind('N', ModalAction.SearchPrevious);
        map.Bind('/', ModalAction.SearchForward);
        map.Bind('?', ModalAction.SearchBackward);
        map.Bind('m', ModalAction.SetMark);
        map.Bind('\'', ModalAction.JumpMark);
        map.Bind('i', ModalAction.InsertBefore);
        map.Bind('a', ModalAction.InsertAfter);
        map.Bind('I', ModalAction.InsertLineStart);
        map.Bind('A', ModalAction.InsertLineEnd);
        map.Bind('v', ModalAction.VisualChar);
        map.Bind('V', ModalAction.VisualLine);
        map.Bind('y', ModalAction.Yank);
        map.Bind(':', ModalAction.CommandLine);
        return map;
    }

    /// <summary>The current bindings. The view is live.</summary>
    public IReadOnlyDictionary<char, ModalAction> Bindings => new ReadOnlyDictionary<char, ModalAction>(_bindings);

    /// <summary>Binds a printable character to an action, replacing any earlier binding for it.</summary>
    /// <exception cref="ArgumentException">The character is a space, a control character, a non-ASCII character or a digit 1 to 9.</exception>
    public void Bind(char key, ModalAction action)
    {
        if (!IsBindable(key))
        {
            throw new ArgumentException($"'{key}' cannot be bound. Use a printable ASCII character other than 1 to 9.", nameof(key));
        }

        _bindings[key] = action;
    }

    /// <summary>Removes a binding so the character can start a mnemonic. Returns false when it was not bound.</summary>
    public bool Unbind(char key) => _bindings.Remove(key);

    /// <summary>Looks up the action for a character.</summary>
    public bool TryGetAction(char key, out ModalAction action) => _bindings.TryGetValue(key, out action);

    /// <summary>A copy with the same bindings.</summary>
    public ModalKeymap Clone()
    {
        var copy = new ModalKeymap();
        foreach (var pair in _bindings)
        {
            copy._bindings[pair.Key] = pair.Value;
        }

        return copy;
    }

    private static bool IsBindable(char key) => key is >= '!' and <= '~' and not (>= '1' and <= '9');
}
