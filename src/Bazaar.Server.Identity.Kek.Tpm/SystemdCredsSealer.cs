using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Seals through <c>systemd-creds</c> (doc 14 DECIDED: use it where systemd is present; less custom code). The secret goes in on stdin
/// and the blob comes out on stdout, so no plaintext touches the disk or the argument list, and no shell is involved. A direct
/// tpm2-tools path for non-systemd hosts is a later, separate sealer.
/// </summary>
public sealed class SystemdCredsSealer(string executable = "systemd-creds") : ITpmSealer
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(30);

    /// <summary>The <c>encrypt</c> arguments for a policy. Public so a test can pin them.</summary>
    internal static IReadOnlyList<string> EncryptArguments(TpmOptions options)
    {
        var args = new List<string> { "encrypt", "--with-key=tpm2", $"--tpm2-pcrs={options.Pcrs}", "--name=bazaar-kek" };
        if (options.PublicKeyPath is { } publicKey)
            args.Add($"--tpm2-public-key={publicKey}");
        args.Add("-");
        args.Add("-");
        return args;
    }

    internal static IReadOnlyList<string> DecryptArguments() => ["decrypt", "--name=bazaar-kek", "-", "-"];

    public byte[] Seal(ReadOnlySpan<byte> plaintext, TpmOptions options) => Run(EncryptArguments(options), plaintext);

    public byte[] Unseal(ReadOnlySpan<byte> sealedBytes, TpmOptions options) => Run(DecryptArguments(), sealedBytes);

    private byte[] Run(IReadOnlyList<string> arguments, ReadOnlySpan<byte> stdin)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CryptographicException("systemd-creds did not start.");
        }
        catch (Win32Exception ex)
        {
            throw new CryptographicException("systemd-creds is not available on this host.", ex);
        }

        using (process)
        {
            process.StandardInput.BaseStream.Write(stdin);
            process.StandardInput.Close();
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(_timeout))
            {
                process.Kill(entireProcessTree: true);
                throw new CryptographicException("systemd-creds timed out.");
            }

            copy.GetAwaiter().GetResult();
            error.GetAwaiter().GetResult();
            // The error text can describe the TPM state but never holds the secret; it is not surfaced, only the exit code.
            if (process.ExitCode != 0)
                throw new CryptographicException($"systemd-creds exited with code {process.ExitCode}.");
            return output.ToArray();
        }
    }
}

/// <summary>Detects a VM through <c>systemd-detect-virt --vm</c>: exit 0 means a hypervisor is present.</summary>
public sealed class SystemdVirtualizationProbe(string executable = "systemd-detect-virt") : IVirtualizationProbe
{
    public TpmCustodyClass Detect()
    {
        var start = new ProcessStartInfo(executable, "--vm --quiet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("systemd-detect-virt did not start.");
            process.WaitForExit(TimeSpan.FromSeconds(10));
            if (!process.HasExited)
            {
                process.Kill();
                throw new InvalidOperationException("systemd-detect-virt timed out; cannot tell whether this host is a VM.");
            }

            // 0: a VM; 1: none. Anything else is a failure to detect, which fails closed rather than guessing hardware.
            return process.ExitCode switch
            {
                0 => TpmCustodyClass.Virtual,
                1 => TpmCustodyClass.Hardware,
                var code => throw new InvalidOperationException($"systemd-detect-virt failed with exit code {code}."),
            };
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException("systemd-detect-virt is not available, so the TPM custody class cannot be told.", ex);
        }
    }
}
