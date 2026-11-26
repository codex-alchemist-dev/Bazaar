namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Runs one external command (a tpm2-tools binary or <c>systemd-creds</c>) in a working directory. Arguments are a list, never a shell
/// string. Injected so tests can stand in for a TPM. The KEK opens once at start-up, so the call is synchronous.
/// </summary>
public interface ITpmToolRunner
{
    TpmToolResult Run(string tool, IReadOnlyList<string> arguments, byte[]? standardInput, string workingDirectory);
}

/// <summary>Outcome of one run. Stdout is raw bytes because unseal and decrypt write the secret there.</summary>
public sealed record TpmToolResult(int ExitCode, byte[] StandardOutput, string StandardError);
