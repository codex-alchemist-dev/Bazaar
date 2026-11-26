namespace Bazaar.Client.Tui.Host;

/// <summary>Which program the user asked for. <see cref="Auto"/> follows doc 58 section 2: install when no owner exists, ops after.</summary>
public enum TuiHostCommand
{
    Auto,
    Install,
    Ops,
    Help,
}

/// <summary>The parsed command line. <see cref="Error"/> is set for an argument that is not understood.</summary>
public sealed record TuiHostArguments(TuiHostCommand Command, string? Error = null)
{
    public static TuiHostArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var command = TuiHostCommand.Auto;
        var named = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                // The mode flags are read by TuiModeSelector from the same arguments; they are valid here and change nothing.
                case "--plain" or "--screen-reader":
                    break;
                case "-h" or "--help" or "help":
                    return new TuiHostArguments(TuiHostCommand.Help);
                case "install" or "ops":
                    if (named)
                    {
                        return new TuiHostArguments(TuiHostCommand.Auto, "Give one of install or ops, not both.");
                    }

                    named = true;
                    command = arg == "install" ? TuiHostCommand.Install : TuiHostCommand.Ops;
                    break;
                default:
                    return new TuiHostArguments(TuiHostCommand.Auto, $"Unknown argument '{arg}'.");
            }
        }

        return new TuiHostArguments(command);
    }

    public const string Usage =
        """
        bazaar-tui [install | ops] [--plain] [--screen-reader]

          install         the server installer (the setup wizard, one question at a time)
          ops             the resident dashboard: server health, Kodo, logs, events, admin
          (neither)       install when no owner exists yet, ops after that
          --plain         plain text, no full-screen interface (also used when output is not a terminal, NO_COLOR is set or TERM=dumb)
          --screen-reader plain text, read in order

        Exit codes: install passes on the setup exit code (0 ok, 1 invalid input, 2 held, 3 apply failed and rolled back).
        ops: 0 normal exit, 1 sign-in refused or abandoned, 2 usage error or server database unreachable, 3 the dashboard crashed
        (the crash is recorded, and the program waits the bounded restart delay before it exits so a service manager that relaunches at
        once still relaunches no faster than that delay).
        """;
}
