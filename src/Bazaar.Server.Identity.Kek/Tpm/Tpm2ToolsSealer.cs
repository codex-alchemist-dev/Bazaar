using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bazaar.Server.Identity.Kek.Tpm;

/// <summary>
/// Sealing through tpm2-tools, for hosts without systemd only (doc 14, DECIDED 2026-11-19). The key is sealed under an owner-hierarchy
/// primary; the public and private blobs are packed into one value kept in the KEK file. The key travels on stdin and stdout only.
/// The work directory holds public blobs and context handles, never the key, and is removed afterwards. No PCR policy is applied
/// (doc 14 option 1: tied to the one TPM chip), because the decided signed policy needs the owner's recovery-bundle key and is not built.
/// </summary>
public sealed class Tpm2ToolsSealer(ITpmToolRunner runner) : ITpmKeySealer
{
    private static readonly string[] _primaryArgs = ["-C", "o", "-g", "sha256", "-G", "rsa", "-c", "primary.ctx"];

    public byte[] Seal(byte[] key)
    {
        var work = Directory.CreateTempSubdirectory("bazaar-tpm-").FullName;
        try
        {
            Flush(work);
            Run("tpm2_createprimary", _primaryArgs, null, work);
            Flush(work);
            Run("tpm2_create", ["-C", "primary.ctx", "-i", "-", "-u", "seal.pub", "-r", "seal.priv"], key, work);
            Flush(work);
            var pub = File.ReadAllBytes(Path.Combine(work, "seal.pub"));
            var priv = File.ReadAllBytes(Path.Combine(work, "seal.priv"));
            var packed = new byte[4 + pub.Length + priv.Length];
            BinaryPrimitives.WriteInt32BigEndian(packed, pub.Length);
            pub.CopyTo(packed, 4);
            priv.CopyTo(packed, 4 + pub.Length);
            return packed;
        }
        finally
        {
            Cleanup(work);
        }
    }

    public byte[] Unseal(byte[] sealedBlob)
    {
        if (sealedBlob.Length < 4)
            throw new KekCustodyException("The sealed KEK blob is not valid.");
        var pubLength = BinaryPrimitives.ReadInt32BigEndian(sealedBlob);
        if (pubLength < 0 || pubLength > sealedBlob.Length - 4)
            throw new KekCustodyException("The sealed KEK blob is not valid.");

        var work = Directory.CreateTempSubdirectory("bazaar-tpm-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(work, "seal.pub"), sealedBlob.AsSpan(4, pubLength));
            File.WriteAllBytes(Path.Combine(work, "seal.priv"), sealedBlob.AsSpan(4 + pubLength));
            Flush(work);
            Run("tpm2_createprimary", _primaryArgs, null, work);
            Flush(work);
            Run("tpm2_load", ["-C", "primary.ctx", "-u", "seal.pub", "-r", "seal.priv", "-c", "seal.ctx"], null, work);
            Flush(work);
            return Run("tpm2_unseal", ["-c", "seal.ctx"], null, work);
        }
        finally
        {
            Cleanup(work);
        }
    }

    private byte[] Run(string tool, IReadOnlyList<string> args, byte[]? stdin, string work)
    {
        var result = runner.Run(tool, args, stdin, work);
        if (result.ExitCode != 0)
        {
            Array.Clear(result.StandardOutput);
            throw new KekCustodyException($"{tool} failed (exit {result.ExitCode}): {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    /// <summary>Flushes transient objects. Skipping this between steps exhausts the TPM's object slots on a long session.</summary>
    private void Flush(string work) => Run("tpm2_flushcontext", ["-t"], null, work);

    private static void Cleanup(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the directory holds only public blobs and context handles.
        }
    }
}
