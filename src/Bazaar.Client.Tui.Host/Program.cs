using System.Collections;
using Bazaar.Client.Tui;
using Bazaar.Client.Tui.Host;

// The entry point only: gather the process facts, then run. Everything else is in TuiHostCommandLine, which the tests drive.
var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    if (entry.Key is string name)
    {
        variables[name] = entry.Value as string;
    }
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // On a key terminal Ctrl+C is read as a key by the screens; this covers the plain path and a closed terminal.
    e.Cancel = true;
    cancel.Cancel();
};

var host = new TuiHostEnvironment(
    new SystemTerminal(),
    Console.In,
    Console.Out,
    Console.Error,
    TuiModeSelector.FromProcess(args),
    variables,
    new ServerTuiWiring(),
    new HiddenConsoleSecretSource());
return await TuiHostCommandLine.RunAsync(args, host, cancel.Token);
