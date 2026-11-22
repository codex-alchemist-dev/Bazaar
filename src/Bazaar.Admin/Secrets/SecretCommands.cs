using Bazaar.Server.Setup;

namespace Bazaar.Admin;

/// <summary>
/// <c>bazaar-admin secret set &lt;reference&gt;</c> (doc 45 section 1.6). The reference is checked first, then the store is asked
/// whether it exists, and only then is the value read, so a closed store never prompts for a value. The value goes straight to
/// <see cref="ISetupSecretWriter"/>. No output, and no exception text, contains the value.
/// </summary>
internal static class SecretCommands
{
    /// <summary>Exit code 3: the write failed and nothing was stored (the apply failure code, used here for a failed write).</summary>
    private const int _writeFailedCode = (int)SetupExitCode.ApplyFailedRolledBack;

    private const string _referenceExample = "secret://bazaar/providers/anthropic/key";

    public static async Task<int> SetAsync(
        string reference,
        ISetupSecretWriter writer,
        ISecretValueSource source,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(source);

        if (!SecretReferences.IsValid(reference, SecretReferences.SecretStoreFamilies))
        {
            stderr.WriteLine($"The name must be a secret reference that starts with secret://bazaar/, for example {_referenceExample}.");
            return (int)SetupExitCode.InvalidInput;
        }

        if (!writer.StoreAvailable)
        {
            stderr.WriteLine(SecretStoreUnavailableMessage);
            return (int)SetupExitCode.Held;
        }

        var value = source.Read($"Value for {reference} (typing is hidden): ");
        if (string.IsNullOrEmpty(value))
        {
            stderr.WriteLine("No value was given, so nothing was stored.");
            return (int)SetupExitCode.InvalidInput;
        }

        try
        {
            await writer.WriteAsync(reference, value, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception is not shown: its text could carry the value. The operator gets the fact, not the detail.
            stderr.WriteLine("The secret store did not accept the value, so nothing was stored. No detail is shown, to keep the value out of the output.");
            return _writeFailedCode;
        }

        stdout.WriteLine($"Stored the value for {reference}. The value is not shown.");
        return (int)SetupExitCode.Ok;
    }

    /// <summary>The one line the command prints when no secret store is registered. It names the adapter that provides one.</summary>
    public const string SecretStoreUnavailableMessage =
        "secret set is unavailable: this build has no secret store (doc 14 ISecretStore); the Host adapter provides it. Nothing was read or stored.";
}
