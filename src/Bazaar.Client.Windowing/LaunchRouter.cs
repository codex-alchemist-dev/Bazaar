namespace Bazaar.Client.Windowing;

/// <summary>
/// What one launch asks for. The head builds it from the launcher's message; this module does not reference the launcher project.
/// </summary>
/// <param name="OpenPaths">Files to open, one editor window each. Untrusted: they arrive from another process over a named pipe.</param>
/// <param name="OpenTerminal">True to open a standalone local terminal window.</param>
public sealed record WindowLaunch(IReadOnlyList<string> OpenPaths, bool OpenTerminal);

/// <summary>
/// Turns a launch request into the windows to open (doc 28 process model: a second launch hands its paths to the running process).
/// Paths come from another process, so <see cref="Route"/> filters them. Pure: no I/O, never throws.
/// </summary>
public static class LaunchRouter
{
    /// <summary>Longest accepted path, in characters.</summary>
    public const int MaxPathLength = 1024;

    /// <summary>Most editor windows one launch opens.</summary>
    public const int MaxEditorWindows = 16;

    /// <summary>Title of the main shell window when the router has to open it.</summary>
    public const string MainTitle = "Bazaar";

    /// <summary>Title of a standalone terminal window.</summary>
    public const string TerminalTitle = "Terminal";

    private static readonly string[] _devicePrefixes = [@"\\.\", @"\\?\", "//./", "//?/"];

    /// <summary>
    /// Returns the windows to open for <paramref name="launch"/>, in order: the main window when it is missing, then one
    /// editor per accepted path, then the terminal. A launch that asks for nothing returns an empty list when the main
    /// window exists; the head then only activates it.
    /// </summary>
    /// <remarks>
    /// A path is dropped when it is null, empty or whitespace, contains a control character (including NUL), is longer than
    /// <see cref="MaxPathLength"/>, is not fully qualified (relative, drive-relative, or root-relative such as <c>\foo</c>),
    /// starts with a <c>\\.\</c> or <c>\\?\</c> device prefix, or names no file (for example <c>C:\dir\</c>). UNC paths are
    /// allowed. Duplicates are compared case-insensitively. At most <see cref="MaxEditorWindows"/> editors open; dropped
    /// paths do not count toward the cap. Titles are the file name only.
    /// </remarks>
    public static IReadOnlyList<WindowSpec> Route(WindowLaunch launch, bool mainWindowExists)
    {
        var windows = new List<WindowSpec>();
        if (!mainWindowExists)
        {
            windows.Add(new WindowSpec(WindowKind.Main, MainTitle));
        }

        var requested = launch?.OpenPaths ?? [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int editors = 0;
        foreach (string? raw in requested)
        {
            if (editors == MaxEditorWindows)
            {
                break;
            }

            if (!IsAcceptable(raw) || !seen.Add(raw))
            {
                continue;
            }

            windows.Add(new WindowSpec(WindowKind.Editor, Path.GetFileName(raw), Path: raw));
            editors++;
        }

        if (launch?.OpenTerminal == true)
        {
            windows.Add(new WindowSpec(WindowKind.Terminal, TerminalTitle));
        }

        return windows;
    }

    private static bool IsAcceptable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength)
        {
            return false;
        }

        foreach (char c in path)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        foreach (string prefix in _devicePrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetFileName(path).Length > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
