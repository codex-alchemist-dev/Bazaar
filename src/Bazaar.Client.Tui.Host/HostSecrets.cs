using System.Text;
using Bazaar.Admin;
using Bazaar.Server.Setup;

namespace Bazaar.Client.Tui.Host;

/// <summary>
/// Reads a secret for the plain wizard without echo on a terminal, or one line from redirected input. The value is never written to
/// the output. The same behaviour as the console source of <c>bazaar-admin</c>, which keeps that class to itself.
/// </summary>
public sealed class HiddenConsoleSecretSource : ISecretValueSource
{
    public string? Read(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadLine();
        }

        Console.Error.Write(prompt + ": ");
        var value = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Escape || (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.C or ConsoleKey.D))
            {
                Console.Error.WriteLine();
                return null;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>
/// Fail closed: this program has no secret store, so the wizard learns that none is available and stores nothing. A real store
/// replaces this when the Host adapter over <c>ISecretStore</c> lands (doc 14), the same as <c>bazaar-admin</c>.
/// </summary>
public sealed class UnavailableSecretWriter : ISetupSecretWriter
{
    public bool StoreAvailable => false;

    public Task WriteAsync(string reference, string value, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No secret store is registered in this build.");
}
