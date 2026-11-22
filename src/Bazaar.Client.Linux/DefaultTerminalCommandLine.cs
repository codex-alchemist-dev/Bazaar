using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux;

/// <summary>
/// The default-terminal command-line flags of the Linux head (doc 30 section 1, "Default terminal"). Each flag runs the
/// per-user registrar, prints the honest status and exits without starting the UI. Only a configuration that names Bazaar's
/// terminal first prints as registered; other launchers are described, never claimed.
/// </summary>
internal static class DefaultTerminalCommandLine
{
    internal const string RegisterFlag = "--register-default-terminal";
    internal const string StatusFlag = "--default-terminal-status";
    internal const string UnregisterFlag = "--unregister-default-terminal";

    /// <summary>
    /// Runs the flag when <paramref name="args"/> holds one. Returns the exit code, or null when no default-terminal flag is
    /// present and the head should start normally.
    /// </summary>
    internal static int? TryRun(string[] args, string? processPath, TextWriter output, TextWriter error, CancellationToken ct = default) =>
        TryRun(args, processPath, output, error, DefaultRegistrar, ct);

    /// <summary>Same as the other overload, with the registrar built by <paramref name="createRegistrar"/> (a test seam).</summary>
    internal static int? TryRun(
        string[] args,
        string? processPath,
        TextWriter output,
        TextWriter error,
        Func<DefaultTerminalSettings, IDefaultTerminalRegistrar> createRegistrar,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(createRegistrar);

        var action = Parse(args);
        if (action is null)
        {
            return null;
        }

        var refusal = DefaultAppCommandLine.RefuseProcessPath(processPath);
        if (refusal is not null)
        {
            error.WriteLine(refusal);
            return DefaultAppCommandLine.ExitNotOk;
        }

        IDefaultTerminalRegistrar registrar;
        try
        {
            registrar = createRegistrar(new DefaultTerminalSettings { ExecPath = processPath! });
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"Default-terminal registration refused: {ex.Message}");
            return DefaultAppCommandLine.ExitNotOk;
        }

        var pending = action.Value switch
        {
            DefaultAppAction.Register => registrar.RegisterAsync(ct),
            DefaultAppAction.Unregister => registrar.UnregisterAsync(ct),
            _ => registrar.QueryAsync(ct),
        };
        var result = pending.GetAwaiter().GetResult();
        DefaultAppCommandLine.Print(result, output);
        return DefaultAppCommandLine.IsSuccess(action.Value, result.State) ? DefaultAppCommandLine.ExitOk : DefaultAppCommandLine.ExitNotOk;
    }

    /// <summary>Which action the arguments ask for, or null. The first default-terminal flag wins.</summary>
    internal static DefaultAppAction? Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var arg in args)
        {
            switch (arg)
            {
                case RegisterFlag:
                    return DefaultAppAction.Register;
                case StatusFlag:
                    return DefaultAppAction.Status;
                case UnregisterFlag:
                    return DefaultAppAction.Unregister;
            }
        }

        return null;
    }

    private static IDefaultTerminalRegistrar DefaultRegistrar(DefaultTerminalSettings settings) =>
        new XdgDefaultTerminalRegistrar(settings, new SystemProcessRunner());
}
