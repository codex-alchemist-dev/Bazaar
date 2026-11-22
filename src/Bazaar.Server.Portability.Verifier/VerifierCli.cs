using System.Text;

namespace Bazaar.Server.Portability.Verifier;

/// <summary>
/// The command line of <c>bazaar-verify</c>. Exit codes: 0 verified, 1 failed, 2 usage (bad arguments, or a path that cannot
/// be read). The JSON report goes to stdout (or to <c>--output</c>); usage text and errors go to stderr.
/// </summary>
public static class VerifierCli
{
    /// <summary>Exit code: the bundle verified.</summary>
    public const int Verified = 0;

    /// <summary>Exit code: the bundle failed verification.</summary>
    public const int Failed = 1;

    /// <summary>Exit code: usage error.</summary>
    public const int Usage = 2;

    /// <summary>Usage text, shown on stderr.</summary>
    public const string UsageText =
        "usage: bazaar-verify <bundle-directory> [--signing-key <file>] [--audit-key <file>] [--output <file>]\n"
        + "  --signing-key  trusted export public key (ECDSA P-256, DER or PEM); default: the bundle's own manifest.pub\n"
        + "  --audit-key    audit checkpoint public key (ECDSA P-256, DER or PEM); needed when the bundle holds audit data\n"
        + "  --output       write the JSON report to this file instead of stdout\n"
        + "exit codes: 0 verified, 1 failed, 2 usage\n";

    /// <summary>Runs the tool and returns the exit code.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        string? bundle = null;
        string? signingKey = null;
        string? auditKey = null;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--signing-key" when i + 1 < args.Length:
                    signingKey = args[++i];
                    break;
                case "--audit-key" when i + 1 < args.Length:
                    auditKey = args[++i];
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case var a when a.StartsWith("--", StringComparison.Ordinal):
                    return Fail(stderr, "unknown or incomplete option: " + a);
                case var a when bundle is null:
                    bundle = a;
                    break;
                default:
                    return Fail(stderr, "only one bundle directory may be given");
            }
        }

        if (bundle is null)
        {
            return Fail(stderr, "a bundle directory is required");
        }

        VerifyReport report;
        try
        {
            var options = new VerifyOptions(
                signingKey is null ? null : ReadPublicKey(signingKey),
                auditKey is null ? null : ReadPublicKey(auditKey));
            report = BundleVerifier.Verify(bundle, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return Fail(stderr, e.Message);
        }

        var json = report.ToJson();
        if (output is null)
        {
            stdout.Write(json);
        }
        else
        {
            try
            {
                File.WriteAllText(output, json, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return Fail(stderr, e.Message);
            }
        }

        return report.Verified ? Verified : Failed;
    }

    /// <summary>Reads a public key file as DER SubjectPublicKeyInfo. A PEM file (PUBLIC KEY) is decoded to DER.</summary>
    internal static byte[] ReadPublicKey(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.ASCII.GetString(bytes);
        if (!text.Contains("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal))
        {
            return bytes;
        }

        var body = text
            .Replace("-----BEGIN PUBLIC KEY-----", string.Empty, StringComparison.Ordinal)
            .Replace("-----END PUBLIC KEY-----", string.Empty, StringComparison.Ordinal);
        return Convert.FromBase64String(string.Concat(body.Where(c => !char.IsWhiteSpace(c))));
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.Write("bazaar-verify: " + message + "\n" + UsageText);
        return Usage;
    }
}
