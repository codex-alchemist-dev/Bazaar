using Bazaar.Client.DefaultApp.Linux;

namespace Bazaar.Client.Linux;

/// <summary>
/// The default-app command-line flags of the Linux head (doc 30 section 1, "Default-app registration"). Each flag runs the
/// per-user registrar, prints the honest status and exits without starting the UI. A handler is registered and the user is
/// guided; a silent default is never claimed, so only a desktop that reports Bazaar as the default prints as registered.
/// </summary>
internal static class DefaultAppCommandLine
{
    internal const string RegisterFlag = "--register-default-app";
    internal const string StatusFlag = "--default-app-status";
    internal const string UnregisterFlag = "--unregister-default-app";

    /// <summary>Exit code when the desktop reports the requested outcome.</summary>
    internal const int ExitOk = 0;

    /// <summary>Exit code for anything else, including a refused process path.</summary>
    internal const int ExitNotOk = 1;

    /// <summary>
    /// Runs the flag when <paramref name="args"/> holds one. Returns the exit code, or null when no default-app flag is
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
        Func<DefaultAppSettings, IDefaultAppRegistrar> createRegistrar,
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

        var refusal = RefuseProcessPath(processPath);
        if (refusal is not null)
        {
            error.WriteLine(refusal);
            return ExitNotOk;
        }

        IDefaultAppRegistrar registrar;
        try
        {
            registrar = createRegistrar(new DefaultAppSettings { ExecPath = processPath! });
        }
        catch (ArgumentException ex)
        {
            error.WriteLine($"Default-app registration refused: {ex.Message}");
            return ExitNotOk;
        }

        var pending = action.Value switch
        {
            DefaultAppAction.Register => registrar.RegisterAsync(ct),
            DefaultAppAction.Unregister => registrar.UnregisterAsync(ct),
            _ => registrar.QueryAsync(ct),
        };
        var result = pending.GetAwaiter().GetResult();
        Print(result, output);
        return IsSuccess(action.Value, result.State) ? ExitOk : ExitNotOk;
    }

    /// <summary>Which action the arguments ask for, or null. The first default-app flag wins.</summary>
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

    /// <summary>
    /// Why the process path cannot go into a desktop entry, or null when it can. A relative path or the <c>dotnet</c> host
    /// (a <c>dotnet Bazaar.Client.Linux.dll</c> run) would register something that does not launch the client.
    /// </summary>
    internal static string? RefuseProcessPath(string? processPath)
    {
        const string guidance = "Run the installed Bazaar binary by its full path, for example /opt/bazaar/Bazaar.Client.Linux.";
        if (string.IsNullOrEmpty(processPath) || processPath[0] != '/')
        {
            return $"Cannot register: the process path is not an absolute path. {guidance}";
        }

        if (string.Equals(Path.GetFileName(processPath), "dotnet", StringComparison.Ordinal))
        {
            return $"Cannot register: Bazaar is running under the dotnet host, not as its own binary. {guidance}";
        }

        return null;
    }

    /// <summary>
    /// Register and status succeed only when the desktop reports Bazaar as the default. A removal succeeds when Bazaar is no
    /// longer registered by this module (the registrar reports that as not-default).
    /// </summary>
    internal static bool IsSuccess(DefaultAppAction action, DefaultAppState state) =>
        action == DefaultAppAction.Unregister ? state == DefaultAppState.NotDefault : state == DefaultAppState.Registered;

    /// <summary>The honest status text: a state label, what was found, and the user's next step when there is one.</summary>
    internal static void Print(DefaultAppStatus status, TextWriter output)
    {
        var label = status.State switch
        {
            DefaultAppState.Registered => "Registered",
            DefaultAppState.NotDefault => "Not the default",
            _ => "Unavailable",
        };
        output.WriteLine($"{label}: {status.Message}");
        if (!string.IsNullOrWhiteSpace(status.Guidance))
        {
            output.WriteLine(status.Guidance);
        }
    }

    private static IDefaultAppRegistrar DefaultRegistrar(DefaultAppSettings settings) =>
        new XdgDefaultAppRegistrar(settings, new SystemProcessRunner());
}

/// <summary>What a default-app flag asks for.</summary>
public enum DefaultAppAction
{
    Register,
    Status,
    Unregister,
}
