namespace Bazaar.Client.Core.Mobile;

/// <summary>
/// Desktop-parity guard (doc 29, Guard against desktop parity). The phone is a development console, not a build tool,
/// so it never renders an editor, terminal or file tree from a server profile.
/// </summary>
public static class PhoneWidgetPolicy
{
    public const string Editor = "editor";
    public const string Terminal = "terminal";
    public const string FileTree = "file-tree";

    public static IReadOnlySet<string> DesktopOnlyKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Editor, Terminal, FileTree };

    /// <summary>The widget kinds that the phone must drop, in their input order.</summary>
    public static IReadOnlyList<string> Violations(IEnumerable<string> widgetKinds) =>
        widgetKinds.Where(DesktopOnlyKinds.Contains).ToList();

    /// <summary>The widget kinds the phone keeps, in their input order.</summary>
    public static IReadOnlyList<string> Keep(IEnumerable<string> widgetKinds) =>
        widgetKinds.Where(kind => !DesktopOnlyKinds.Contains(kind)).ToList();
}
