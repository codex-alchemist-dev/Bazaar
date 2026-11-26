namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Sealing through <c>systemd-creds</c> (doc 14, DECIDED 2026-11-19: used wherever systemd is present). The key goes in on stdin and the
/// credential comes back on stdout, so the plaintext never touches the disk. <c>--with-key=tpm2</c> means the TPM alone, never the
/// host-key fallback and never <c>null</c>. <paramref name="pcrs"/> binds the credential to PCR state (shipped default <c>7</c>, the
/// raw PCR-7 policy; the signed policy of doc 14 is not built yet).
/// </summary>
public sealed class SystemdCredsSealer(ITpmToolRunner runner, string pcrs) : ITpmKeySealer
{
    private const string _credentialName = "bazaar-kek";

    public byte[] Seal(byte[] key)
    {
        var args = new List<string> { "encrypt", "--with-key=tpm2", "--name=" + _credentialName };
        if (!string.IsNullOrWhiteSpace(pcrs))
            args.Add("--tpm2-pcrs=" + pcrs);
        args.Add("-");
        args.Add("-");
        return Run(args, key, "seal");
    }

    public byte[] Unseal(byte[] sealedBlob) => Run(["decrypt", "--name=" + _credentialName, "-", "-"], sealedBlob, "unseal");

    private byte[] Run(IReadOnlyList<string> args, byte[] input, string action)
    {
        var result = runner.Run("systemd-creds", args, input, Directory.GetCurrentDirectory());
        if (result.ExitCode != 0)
        {
            Array.Clear(result.StandardOutput);
            throw new KekCustodyException($"systemd-creds could not {action} the KEK (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }
}
