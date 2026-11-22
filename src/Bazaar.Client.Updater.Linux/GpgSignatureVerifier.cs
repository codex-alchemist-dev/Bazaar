namespace Bazaar.Client.Updater.Linux;

/// <summary>
/// Verifies a detached OpenPGP signature with <c>gpg</c> against the one key pinned in the build (doc 30 section 7). Each call
/// uses its own empty <c>GNUPGHOME</c> (owner-only directory), imports the pinned key, checks that it is the key with the
/// pinned fingerprint, then runs <c>--verify</c> and reads the machine-readable status lines. The user's own keyring is never
/// read, so a key the user happens to trust cannot vouch for a release.
/// </summary>
internal sealed class GpgSignatureVerifier
{
    private const UnixFileMode _ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly LinuxUpdaterSettings _settings;
    private readonly IProcessRunner _runner;
    private readonly IUpdateFileSystem _fs;

    public GpgSignatureVerifier(LinuxUpdaterSettings settings, IProcessRunner runner, IUpdateFileSystem fs)
    {
        _settings = settings;
        _runner = runner;
        _fs = fs;
    }

    /// <summary>Returns null when the signature is good, otherwise the failure to report.</summary>
    public async Task<UpdateFailure?> VerifyAsync(string dataPath, string signaturePath, CancellationToken ct)
    {
        // A short path under the temp directory: gpg puts its agent socket in the home directory when the per-user runtime
        // directory is unavailable, and a socket path is limited to about 100 characters. 96 random bits keep the name unguessable.
        string home = Path.Combine(Path.GetTempPath(), "bz" + Guid.NewGuid().ToString("N")[..24]);
        _fs.CreateDirectory(home, _ownerOnly);
        try
        {
            var import = await RunAsync(home, ["--import", _settings.PinnedKeyPath], ct).ConfigureAwait(false);
            if (import.ExitCode != 0)
            {
                return new UpdateFailure(UpdateStatus.SignatureToolFailed, "gpg could not import the pinned key: " + FirstLine(import.StandardError));
            }

            var listing = await RunAsync(home, ["--with-colons", "--fingerprint", "--list-keys"], ct).ConfigureAwait(false);
            if (listing.ExitCode != 0)
            {
                return new UpdateFailure(UpdateStatus.SignatureToolFailed, "gpg could not list the pinned key: " + FirstLine(listing.StandardError));
            }

            var primaries = PrimaryFingerprints(listing.StandardOutput);
            if (primaries.Count != 1 || primaries[0] != _settings.PinnedKeyFingerprint)
            {
                return new UpdateFailure(UpdateStatus.FingerprintMismatch, "The key file is not the pinned key.");
            }

            var verify = await RunAsync(home, ["--status-fd", "1", "--verify", signaturePath, dataPath], ct).ConfigureAwait(false);
            return CheckStatus(verify);
        }
        catch (TimeoutException e)
        {
            return new UpdateFailure(UpdateStatus.SignatureToolFailed, e.Message);
        }
        catch (IOException e)
        {
            return new UpdateFailure(UpdateStatus.SignatureToolFailed, e.Message);
        }
        finally
        {
            _fs.DeleteDirectory(home);
        }
    }

    /// <summary>gpg's diagnostics on one line, at most 300 characters (they hold paths and reasons, never key material).</summary>
    private static string FirstLine(string text)
    {
        string joined = string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return joined.Length == 0 ? "no message" : joined.Length <= 300 ? joined : joined[^300..];
    }

    private Task<ProcessResult> RunAsync(string home, string[] arguments, CancellationToken ct)
    {
        string[] common = ["--batch", "--no-tty", "--no-autostart", "--homedir", home];
        var environment = new Dictionary<string, string> { ["GNUPGHOME"] = home, ["LC_ALL"] = "C" };
        return _runner.RunAsync(new ProcessRequest(_settings.GpgPath, [.. common, .. arguments], environment, _settings.GpgTimeout), ct);
    }

    /// <summary>The <c>fpr</c> record right after each <c>pub</c> record is a primary key; the ones after <c>sub</c> are subkeys.</summary>
    internal static List<string> PrimaryFingerprints(string colons)
    {
        var result = new List<string>();
        string last = string.Empty;
        foreach (string line in colons.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split(':');
            string type = fields[0];
            if (type == "fpr" && last == "pub" && fields.Length > 9)
            {
                result.Add(fields[9].ToLowerInvariant());
            }

            if (type is "pub" or "sub" or "sec" or "ssb")
            {
                last = type;
            }
        }

        return result;
    }

    private UpdateFailure? CheckStatus(ProcessResult verify)
    {
        bool good = false;
        bool validFromPinned = false;
        foreach (string raw in verify.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.TrimEnd('\r');
            const string Prefix = "[GNUPG:] ";
            if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string[] tokens = line[Prefix.Length..].Split(' ');
            switch (tokens[0])
            {
                case "GOODSIG":
                    good = true;
                    break;
                case "VALIDSIG":
                    // The last field of VALIDSIG is the fingerprint of the primary key.
                    validFromPinned = string.Equals(tokens[^1], _settings.PinnedKeyFingerprint, StringComparison.OrdinalIgnoreCase);
                    break;
                case "BADSIG" or "ERRSIG" or "EXPSIG" or "EXPKEYSIG" or "REVKEYSIG" or "NO_PUBKEY":
                    return new UpdateFailure(UpdateStatus.SignatureInvalid, $"gpg reported {tokens[0]}.");
            }
        }

        return verify.ExitCode == 0 && good && validFromPinned
            ? null
            : new UpdateFailure(UpdateStatus.SignatureInvalid, "The signature is not valid for the pinned key.");
    }
}

/// <summary>A refusal with its reason, passed between the steps of an update.</summary>
internal sealed record UpdateFailure(UpdateStatus Status, string Detail);
