using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Bazaar.Contracts.Soul;
using static Bazaar.Server.Souls.Import.BundleRejectionCode;

namespace Bazaar.Server.Souls.Import;

/// <summary>
/// Verifies a received Soul bundle and decides whether it may enter quarantine (docs/design/07-soul-system.md, "Import";
/// docs/design/15-security-taint-and-approvals.md). The checks run in order and fail closed:
/// <list type="number">
/// <item>Parse <c>manifest.json</c> strictly. An unknown format version is rejected. Stops on failure.</item>
/// <item>Verify the signature over the exact manifest bytes. Stops on failure.</item>
/// <item>Check every path (absolute, "..", backslash, drive letter, duplicate) and match files to manifest entries both ways.</item>
/// <item>Check each file's byte length and lowercase SHA-256 against its manifest entry.</item>
/// <item>Enforce the total and per-file byte caps.</item>
/// </list>
/// Steps 3 to 5 keep going after a failure so the caller sees every reason. Nothing is executed and nothing is written here:
/// a valid result only means origin and integrity check out. A valid signature does not make the content safe.
/// </summary>
// TODO(component): move to AddBazaarSouls() when the component model (PR #125) merges.
public sealed class BundleImporter
{
    /// <summary>The only bundle format version this importer reads.</summary>
    public const int SupportedFormatVersion = 1;

    private readonly IBundleSignatureVerifier _verifier;
    private readonly BundleImportOptions _options;

    /// <summary>Creates an importer. Throws <see cref="ArgumentOutOfRangeException"/> if a cap is not positive.</summary>
    /// <param name="verifier">Checks bundle signatures.</param>
    /// <param name="options">Size caps. Null uses the safe defaults.</param>
    public BundleImporter(IBundleSignatureVerifier verifier, BundleImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        _verifier = verifier;
        _options = options ?? new BundleImportOptions();
        _options.Validate();
    }

    /// <summary>Runs the quarantine checks. Pure: it reads the inputs and returns a result.</summary>
    /// <param name="manifestBytes">The exact bytes of <c>manifest.json</c>, as received.</param>
    /// <param name="signature">The detached signature over <paramref name="manifestBytes"/>, or null if none was sent.</param>
    /// <param name="files">Every bundle file except the manifest and signature. Duplicate paths are allowed here so they can be rejected.</param>
    /// <returns><see cref="BundleQuarantined"/> when every check passes, otherwise <see cref="BundleRejected"/>.</returns>
    public BundleImportResult Import(ReadOnlyMemory<byte> manifestBytes, BundleSignature? signature, IReadOnlyList<BundleFileContent> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        // Step 1: parse. Nothing else can be trusted without a parsed manifest.
        if (manifestBytes.Length > _options.MaxManifestBytes)
            return Single(ManifestTooLarge);
        if (!TryParseManifest(manifestBytes, out var manifest, out var parseCode))
            return Single(parseCode);

        // Step 2: the signature covers the exact bytes that were parsed.
        if (signature is null || string.IsNullOrEmpty(signature.KeyId) || string.IsNullOrEmpty(signature.SignatureBase64))
            return Single(SignatureMissing);
        switch (_verifier.Verify(manifestBytes.Span, signature))
        {
            case BundleSignatureStatus.Valid:
                break;
            case BundleSignatureStatus.UnknownKey:
                return Single(UnknownSigningKey);
            default:
                return Single(SignatureInvalid);
        }

        // Steps 3 to 5 collect every failure they find.
        var rejections = new List<BundleRejection>();
        CheckFiles(manifest, files, rejections);
        return rejections.Count == 0
            ? new BundleQuarantined(manifest)
            : new BundleRejected(rejections);
    }

    private static BundleRejected Single(BundleRejectionCode code) => new([new BundleRejection(code)]);

    private static bool TryParseManifest(ReadOnlyMemory<byte> bytes, [NotNullWhen(true)] out BundleManifest? manifest, out BundleRejectionCode code)
    {
        manifest = null;
        code = ManifestMalformed;
        try
        {
            using (var document = JsonDocument.Parse(bytes))
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var version))
                    return false;
                if (version.ValueKind != JsonValueKind.Number)
                    return false;
                if (!version.TryGetInt32(out var value) || value != SupportedFormatVersion)
                {
                    code = UnknownFormatVersion;
                    return false;
                }
            }

            var parsed = JsonSerializer.Deserialize(bytes.Span, BundleManifestJsonContext.Default.BundleManifest);
            if (parsed is null || !IsWellFormed(parsed))
                return false;
            manifest = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Checks the required fields that the deserializer cannot enforce on its own.</summary>
    private static bool IsWellFormed(BundleManifest manifest) =>
        manifest.SoulId != Guid.Empty
        && manifest.SoulVersion >= 1
        && IsSha256Hex(manifest.ContentSha256)
        && !string.IsNullOrWhiteSpace(manifest.Exporter)
        && manifest.Files is not null
        && manifest.Exclusions is not null
        && manifest.Files.All(f => f is not null && f.Path is not null && f.Sha256 is not null)
        && manifest.Exclusions.All(e => e is not null);

    private void CheckFiles(BundleManifest manifest, IReadOnlyList<BundleFileContent> files, List<BundleRejection> rejections)
    {
        // Step 3, manifest side. Paths are checked here too, so a bad path in the manifest is reported even without a file.
        var declared = new Dictionary<string, BundleFile>(StringComparer.Ordinal);
        var declaredFolded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (PathViolation(entry.Path) is { } pathCode)
                rejections.Add(new BundleRejection(pathCode, entry.Path));
            if (declaredFolded.Add(entry.Path))
                declared.Add(entry.Path, entry);
            else
                rejections.Add(new BundleRejection(PathDuplicate, entry.Path));
        }

        // Steps 3 to 5, file side.
        var received = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var file in files)
        {
            long size = file.Content.Length;
            total += size;
            if (PathViolation(file.Path) is { } pathCode)
                rejections.Add(new BundleRejection(pathCode, file.Path));
            if (!received.Add(file.Path))
            {
                rejections.Add(new BundleRejection(PathDuplicate, file.Path));
                continue;
            }
            if (size > _options.MaxFileBytes)
                rejections.Add(new BundleRejection(FileTooLarge, file.Path));
            if (!declared.TryGetValue(file.Path, out var entry))
            {
                rejections.Add(new BundleRejection(FileNotInManifest, file.Path));
                continue;
            }
            matched.Add(file.Path);
            CheckDigest(entry, file.Content.Span, file.Path, rejections);
        }

        if (total > _options.MaxTotalBytes)
            rejections.Add(new BundleRejection(TotalTooLarge));
        foreach (var entry in declared.Values)
        {
            if (!matched.Contains(entry.Path))
                rejections.Add(new BundleRejection(ManifestEntryMissingFile, entry.Path));
        }
    }

    private static void CheckDigest(BundleFile entry, ReadOnlySpan<byte> content, string path, List<BundleRejection> rejections)
    {
        if (entry.Size < 0 || entry.Size != content.Length)
            rejections.Add(new BundleRejection(SizeMismatch, path));
        if (!IsSha256Hex(entry.Sha256))
        {
            rejections.Add(new BundleRejection(DigestMalformed, path));
            return;
        }
        var actual = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(actual, entry.Sha256, StringComparison.Ordinal))
            rejections.Add(new BundleRejection(DigestMismatch, path));
    }

    /// <summary>Returns the first path rule the path breaks, or null when the path is acceptable.</summary>
    private static BundleRejectionCode? PathViolation(string path)
    {
        if (path.Length == 0)
            return PathInvalid;
        if (path.Contains('\\'))
            return PathBackslash;
        if (path[0] == '/')
            return PathAbsolute;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            return PathDriveLetter;
        if (path.Contains("..", StringComparison.Ordinal))
            return PathTraversal;
        if (path.Contains(':'))
            return PathInvalid;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                return PathInvalid;
        }
        foreach (var c in path)
        {
            if (char.IsControl(c))
                return PathInvalid;
        }
        return null;
    }

    /// <summary>True for exactly 64 lowercase hex characters.</summary>
    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        foreach (var c in value)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }
        return true;
    }
}
