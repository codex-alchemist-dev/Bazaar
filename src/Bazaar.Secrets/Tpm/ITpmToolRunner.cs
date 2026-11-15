namespace Bazaar.Secrets.Tpm;

/// <summary>Runs one tpm2-tools command in a working directory. Injected so tests can stand in for a TPM.</summary>
public interface ITpmToolRunner
{
    Task<TpmToolResult> RunAsync(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory, CancellationToken ct);
}

/// <summary>Outcome of one tpm2-tools run. Stdout is raw bytes because unseal writes the secret there.</summary>
public sealed record TpmToolResult(int ExitCode, byte[] StandardOutput, string StandardError);
