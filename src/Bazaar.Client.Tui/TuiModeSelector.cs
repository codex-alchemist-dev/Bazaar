namespace Bazaar.Client.Tui;

/// <summary>
/// Chooses between the full-screen TUI and the plain wizard (doc 58 section 4): the plain path is used when stdout (or stdin) is
/// not a TTY, with <c>--plain</c>, with <c>NO_COLOR</c> set, or when a screen-reader flag is given. A pure function of the gathered
/// environment, so every branch is a unit test. Doc 58 does not name <c>TERM=dumb</c>; it is treated as plain too, because a dumb
/// terminal cannot draw a full-screen interface (a safe default, not a setting).
/// </summary>
public static class TuiModeSelector
{
    /// <summary>The flag that forces the plain wizard.</summary>
    public const string PlainFlag = "--plain";

    /// <summary>The flag that says a screen reader is in use. It forces the plain wizard.</summary>
    public const string ScreenReaderFlag = "--screen-reader";

    public static TuiModeDecision Select(TerminalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (HasFlag(environment, PlainFlag))
        {
            return Plain(PlainReason.PlainFlag);
        }

        if (HasFlag(environment, ScreenReaderFlag))
        {
            return Plain(PlainReason.ScreenReaderFlag);
        }

        if (environment.InputRedirected)
        {
            return Plain(PlainReason.InputRedirected);
        }

        if (environment.OutputRedirected)
        {
            return Plain(PlainReason.OutputRedirected);
        }

        // no-color.org: the variable counts when it is set to a non-empty string.
        if (!string.IsNullOrEmpty(environment.NoColor))
        {
            return Plain(PlainReason.NoColor);
        }

        if (string.Equals(environment.Term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return Plain(PlainReason.DumbTerminal);
        }

        return new TuiModeDecision(TuiMode.FullScreen, PlainReason.None);
    }

    /// <summary>Reads the real process environment and arguments.</summary>
    public static TerminalEnvironment FromProcess(IReadOnlyList<string> arguments) => new(
        Console.IsInputRedirected,
        Console.IsOutputRedirected,
        Environment.GetEnvironmentVariable("NO_COLOR"),
        Environment.GetEnvironmentVariable("TERM"),
        arguments);

    private static bool HasFlag(TerminalEnvironment environment, string flag) =>
        environment.Arguments.Any(argument => string.Equals(argument, flag, StringComparison.Ordinal));

    private static TuiModeDecision Plain(PlainReason reason) => new(TuiMode.Plain, reason);
}
