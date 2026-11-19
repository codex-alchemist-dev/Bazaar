namespace Bazaar.Secrets.Keyring;

/// <summary>Runs <c>secret-tool</c>, the Secret Service command line client. Injected so tests run without a keyring.</summary>
public interface ISecretToolRunner
{
    Task<SecretToolResult> RunAsync(IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct);
}

/// <summary>Outcome of one <c>secret-tool</c> run.</summary>
public sealed record SecretToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Raised when the keyring cannot give a definite answer. The caller must not treat this as "no key yet".</summary>
public sealed class KekCustodyException(string message, Exception? inner = null) : Exception(message, inner);
