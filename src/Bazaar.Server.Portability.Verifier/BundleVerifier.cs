using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability.Verifier;

/// <summary>
/// Keys the caller trusts. A signing key pins the manifest signature to a key the reader already holds; without it the
/// bundle's own <c>manifest.pub</c> is used, which proves the bundle is self-consistent but not who made it. The audit key
/// verifies the checkpoint and proof signatures (a different key from the export key; doc 44 section 4.6). Both are ECDSA
/// P-256 public keys as DER SubjectPublicKeyInfo, with fixed-size (P1363) SHA-256 signatures.
/// </summary>
public sealed record VerifyOptions(byte[]? PinnedSigningKey = null, byte[]? AuditKey = null);

/// <summary>
/// Offline verifier for an exported bundle directory (doc 44 sections 5, 6.1 and 6.3, work package 3). It reads files one at a
/// time and in chunks, never the whole bundle, and needs no server, database or network. Anything that does not fit is a
/// problem; a bundle verifies only when no problem was found.
/// </summary>
public static class BundleVerifier
{
    /// <summary>What this verifier does not check. Listed in every report so a pass is not read as more.</summary>
    internal static readonly string[] NotCheckedList =
    [
        "bundle_parts_and_splitting: the writer does not split bundles yet, so only the manifest's ordinal file order is checked",
        "encrypted_archive: decrypt first; this tool reads a bundle directory",
        "gc_event_hash_recompute: doc 01 has not fixed the GC event hash rule, so only the links, head and counts are checked",
        "key_history: doc 44 section 6.3 key rotation history has no file format yet",
        "vault_git_fsck: git fsck on vault/repo.bundle needs git; only the file hash is checked",
    ];

    private const string _gcFolder = "gc/";
    private const string _auditFolder = "audit/";
    private const int _bufferSize = 81920;

    /// <summary>Verifies the bundle in <paramref name="bundleDirectory"/>. Throws <see cref="DirectoryNotFoundException"/> when it does not exist.</summary>
    public static VerifyReport Verify(string bundleDirectory, VerifyOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleDirectory));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The bundle directory does not exist.");
        }

        return new Run(root, options ?? new VerifyOptions(), ct).Execute();
    }

    private sealed record Entry(string Path, string Sha256, long Bytes);

    private sealed class Run(string root, VerifyOptions options, CancellationToken ct)
    {
        private readonly List<VerifyProblem> _problems = [];
        private readonly List<VerifyCheck> _checks = [];
        private readonly List<Entry> _entries = [];
        private readonly HashSet<string> _listed = new(StringComparer.Ordinal);
        private string? _schemaVersion;
        private string? _instanceId;
        private long _filesHashed;
        private long _bytesHashed;
        private List<string> _unlisted = [];
        private AuditSummary? _audit;
        private GcSummary? _gc;

        public VerifyReport Execute()
        {
            var manifestBytes = ReadManifest();
            var parsed = manifestBytes is not null && Step("manifest", () => ParseManifest(manifestBytes));
            if (manifestBytes is null)
            {
                _checks.Add(new VerifyCheck("manifest", "failed"));
            }

            if (manifestBytes is not null)
            {
                Step("signature", () => CheckSignature(manifestBytes));
            }

            if (parsed)
            {
                Step("files", CheckFiles);
                Step("unlisted_files", CheckUnlisted);
                StepIf("gc_chains", _entries.Any(e => e.Path.StartsWith(_gcFolder, StringComparison.Ordinal)), CheckGc);
                StepIf("audit_chain", _entries.Any(e => IsAuditChainFile(e.Path)), CheckAuditChain);
                StepIf("audit_proofs", _entries.Any(e => e.Path.StartsWith(AuditArchiveFormat.ProofsFolder, StringComparison.Ordinal)), CheckProofs);
            }

            var problems = _problems
                .OrderBy(p => p.Code, StringComparer.Ordinal)
                .ThenBy(p => p.Path, StringComparer.Ordinal)
                .ThenBy(p => p.Detail, StringComparer.Ordinal)
                .ToList();
            return new VerifyReport(
                VerifyReport.FormatId,
                problems.Count == 0,
                _schemaVersion,
                _instanceId,
                options.PinnedSigningKey is null ? "bundle" : "pinned",
                _entries.Count,
                _filesHashed,
                _bytesHashed,
                _unlisted,
                _checks,
                problems,
                _audit,
                _gc,
                NotCheckedList);
        }

        private static bool IsAuditChainFile(string path) =>
            path is AuditArchiveFormat.EntriesPath or AuditArchiveFormat.CheckpointsPath or AuditArchiveFormat.ArchivePath;

        private bool Step(string name, Action action)
        {
            var before = _problems.Count;
            action();
            var ok = _problems.Count == before;
            _checks.Add(new VerifyCheck(name, ok ? "passed" : "failed"));
            return ok;
        }

        private void StepIf(string name, bool applies, Action action)
        {
            if (applies)
            {
                Step(name, action);
            }
            else
            {
                _checks.Add(new VerifyCheck(name, "skipped"));
            }
        }

        private void Add(string code, string? path, string detail) => _problems.Add(new VerifyProblem(code, path, detail));

        // ---- manifest ----

        private byte[]? ReadManifest()
        {
            var path = Resolve(ExportBundleWriter.ManifestFileName);
            if (path is null || !File.Exists(path))
            {
                Add("manifest_missing", ExportBundleWriter.ManifestFileName, "manifest.json is missing.");
                return null;
            }

            return File.ReadAllBytes(path);
        }

        private void ParseManifest(byte[] manifest)
        {
            try
            {
                using var doc = JsonDocument.Parse(manifest);
                var rootElement = doc.RootElement;
                if (rootElement.ValueKind != JsonValueKind.Object)
                {
                    Add("manifest_malformed", null, "The manifest is not a JSON object.");
                    return;
                }

                _schemaVersion = ReadString(rootElement, "schemaVersion");
                _instanceId = ReadString(rootElement, "instanceId");
                if (_schemaVersion is null || !_schemaVersion.StartsWith("1.", StringComparison.Ordinal))
                {
                    Add("manifest_schema_unsupported", null, "The manifest schema version is missing or is not 1.x.");
                }

                if (!rootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                {
                    Add("manifest_malformed", null, "The manifest has no files array.");
                    return;
                }

                ReadEntries(files);
                CheckTotals(rootElement);
            }
            catch (JsonException)
            {
                Add("manifest_malformed", null, "The manifest is not valid JSON.");
            }
        }

        private void ReadEntries(JsonElement files)
        {
            string? previous = null;
            foreach (var element in files.EnumerateArray())
            {
                if (!TryReadEntry(element, out var entry))
                {
                    Add("manifest_entry_invalid", null, "A manifest entry is malformed.");
                    continue;
                }

                if (!IsSafePath(entry.Path))
                {
                    Add("manifest_entry_invalid", entry.Path, "A manifest path is not a safe relative path.");
                    continue;
                }

                if (entry.Path == ExportBundleWriter.ManifestFileName || BundleSigner.IsSignatureFile(entry.Path))
                {
                    Add("manifest_entry_invalid", entry.Path, "The manifest lists a reserved file.");
                    continue;
                }

                if (!_listed.Add(entry.Path))
                {
                    Add("manifest_entry_duplicate", entry.Path, "A path is listed twice.");
                    continue;
                }

                if (previous is not null && string.CompareOrdinal(previous, entry.Path) >= 0)
                {
                    Add("manifest_order", entry.Path, "Files are not in ordinal path order.");
                }

                previous = entry.Path;
                _entries.Add(entry);
            }
        }

        private void CheckTotals(JsonElement rootElement)
        {
            if (rootElement.TryGetProperty("fileCount", out var count)
                && (!count.TryGetInt64(out var c) || c != _entries.Count))
            {
                Add("manifest_totals", null, "fileCount does not match the file list.");
            }

            if (rootElement.TryGetProperty("totalBytes", out var total)
                && (!total.TryGetInt64(out var t) || t != _entries.Sum(e => e.Bytes)))
            {
                Add("manifest_totals", null, "totalBytes does not match the file list.");
            }
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool TryReadEntry(JsonElement element, out Entry entry)
        {
            entry = new Entry(string.Empty, string.Empty, 0);
            if (element.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var path = ReadString(element, "path");
            var sha = ReadString(element, "sha256");
            if (path is null || sha is null || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower)
                || !element.TryGetProperty("bytes", out var bytes) || !bytes.TryGetInt64(out var size) || size < 0)
            {
                return false;
            }

            entry = new Entry(path, sha, size);
            return true;
        }

        // ---- signature ----

        private void CheckSignature(byte[] manifest)
        {
            var signaturePath = Resolve(BundleSigner.SignatureFileName);
            if (signaturePath is null || !File.Exists(signaturePath))
            {
                Add("manifest_unsigned", BundleSigner.SignatureFileName, "The bundle has no manifest signature.");
                return;
            }

            byte[] key;
            if (options.PinnedSigningKey is not null)
            {
                key = options.PinnedSigningKey;
            }
            else
            {
                var publicPath = Resolve(BundleSigner.PublicKeyFileName);
                if (publicPath is null || !File.Exists(publicPath))
                {
                    Add("manifest_unsigned", BundleSigner.PublicKeyFileName, "The bundle has no public key and none was given.");
                    return;
                }

                key = File.ReadAllBytes(publicPath);
            }

            if (!BundleSigner.VerifyManifest(manifest, File.ReadAllBytes(signaturePath), key))
            {
                Add("signature_invalid", BundleSigner.SignatureFileName, "The manifest signature does not verify with the signing key.");
            }
        }

        // ---- files ----

        private void CheckFiles()
        {
            foreach (var entry in _entries)
            {
                ct.ThrowIfCancellationRequested();
                var path = Resolve(entry.Path);
                if (path is null)
                {
                    Add("file_unsafe", entry.Path, "The path is a link or leaves the bundle directory.");
                    continue;
                }

                if (!File.Exists(path))
                {
                    Add("file_missing", entry.Path, "The file is listed in the manifest but is not in the bundle.");
                    continue;
                }

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[_bufferSize];
                long size = 0;
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    size += read;
                }

                _filesHashed++;
                _bytesHashed += size;
                if (size != entry.Bytes)
                {
                    Add("size_mismatch", entry.Path, "The file size differs from the manifest.");
                }

                if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Add("sha256_mismatch", entry.Path, "The file hash differs from the manifest.");
                }
            }
        }

        private void CheckUnlisted()
        {
            var found = new List<string>();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative == ExportBundleWriter.ManifestFileName || BundleSigner.IsSignatureFile(relative) || _listed.Contains(relative))
                {
                    continue;
                }

                found.Add(relative);
                Add("file_not_in_manifest", relative, "The file is in the bundle but the manifest does not list it.");
            }

            found.Sort(StringComparer.Ordinal);
            _unlisted = found;
        }

        // ---- GC chains ----

        private void CheckGc()
        {
            var ids = _entries
                .Where(e => e.Path.StartsWith(_gcFolder, StringComparison.Ordinal))
                .Select(e => e.Path.Split('/'))
                .Where(parts => parts.Length >= 3)
                .Select(parts => parts[1])
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();

            long events = 0;
            foreach (var id in ids)
            {
                events += CheckOneGc(id);
            }

            _gc = new GcSummary(ids.Count, events);
        }

        private long CheckOneGc(string id)
        {
            var folder = _gcFolder + id + "/";
            var eventsPath = folder + "events.jsonl";
            var headPath = folder + "head.json";
            var hasEvents = _listed.Contains(eventsPath);
            if (!hasEvents || !_listed.Contains(headPath))
            {
                Add("gc_file_missing", hasEvents ? headPath : eventsPath, "A GC needs events.jsonl and head.json.");
                return 0;
            }

            var (count, lastSeq, lastHash) = ReplayGcEvents(eventsPath);
            ReadHead(headPath, out var headSeq, out var headHash, out var counts);
            if (headSeq is null)
            {
                return count;
            }

            if (headSeq != lastSeq)
            {
                Add("gc_head_mismatch", headPath, "head.json last_seq is not the last event's seq.");
            }

            if (!string.Equals(headHash ?? string.Empty, lastHash ?? string.Empty, StringComparison.Ordinal))
            {
                Add("gc_head_mismatch", headPath, "head.json head_hash is not the last event's hash.");
            }

            CheckCount(headPath, counts, "events", count);
            foreach (var name in new[] { "messages", "versions", "mentions" })
            {
                var path = folder + name + ".jsonl";
                if (_listed.Contains(path))
                {
                    CheckCount(headPath, counts, name, CountLines(path));
                }
                else if (counts is not null && counts.TryGetValue(name, out var declared) && declared > 0)
                {
                    Add("gc_file_missing", path, "head.json counts rows for a file that is not in the bundle.");
                }
            }

            return count;
        }

        private void CheckCount(string headPath, Dictionary<string, long>? counts, string name, long actual)
        {
            if (counts is null || !counts.TryGetValue(name, out var declared))
            {
                Add("gc_head_mismatch", headPath, "head.json has no row count for " + name + ".");
            }
            else if (declared != actual)
            {
                Add("gc_head_mismatch", headPath, "head.json row count for " + name + " does not match the file.");
            }
        }

        private (long Count, long LastSeq, string? LastHash) ReplayGcEvents(string relativePath)
        {
            var path = Resolve(relativePath);
            if (path is null || !File.Exists(path))
            {
                return (0, 0, null);
            }

            long count = 0;
            long lastSeq = 0;
            string? lastHash = null;
            foreach (var line in ReadLines(path))
            {
                ct.ThrowIfCancellationRequested();
                if (line is null || !TryReadEvent(line, out var seq, out var prev, out var hash))
                {
                    Add("gc_event_malformed", relativePath, "An event line is not valid (seq, prev_event_hash and event_hash are required).");
                    return (count, lastSeq, lastHash);
                }

                if (seq != lastSeq + 1)
                {
                    Add("gc_sequence_broken", relativePath, "An event is missing, repeated or out of order.");
                    return (count, lastSeq, lastHash);
                }

                if (!string.Equals(prev ?? string.Empty, lastHash ?? string.Empty, StringComparison.Ordinal))
                {
                    Add("gc_chain_broken", relativePath, "An event's prev_event_hash is not the previous event's hash.");
                    return (count, lastSeq, lastHash);
                }

                count++;
                lastSeq = seq;
                lastHash = hash;
            }

            return (count, lastSeq, lastHash);
        }

        private static bool TryReadEvent(string line, out long seq, out string? prev, out string? hash)
        {
            seq = 0;
            prev = null;
            hash = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("seq", out var s) || !s.TryGetInt64(out seq))
                {
                    return false;
                }

                // gc.jsonl.v1 (what the exporter writes) spells the chain prevHash and hash; the older spelling is still read.
                var prevName = e.TryGetProperty("prevHash", out _) ? "prevHash" : "prev_event_hash";
                var hashName = e.TryGetProperty("hash", out _) ? "hash" : "event_hash";
                if (!TryOptionalString(e, prevName, out prev))
                {
                    return false;
                }

                prev = NormalizeGenesis(prev);
                hash = ReadString(e, hashName);
                return !string.IsNullOrEmpty(hash);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>The all-zero genesis hash gc.jsonl.v1 gives a file's first line means the same as no previous hash.</summary>
        private static string? NormalizeGenesis(string? value) =>
            !string.IsNullOrEmpty(value) && value.All(c => c == '0') ? null : value;

        private static bool TryOptionalString(JsonElement element, string name, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            value = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return value is not null;
        }

        /// <summary>
        /// Reads head.json in either spelling: <c>last_seq</c>, <c>head_hash</c> and a <c>counts</c> object, or what
        /// <c>GcExportHead</c> writes (<c>headSeq</c>, <c>eventHeadHash</c> and flat <c>eventCount</c>, <c>messageCount</c>,
        /// <c>versionCount</c> and, when mentions are exported, <c>mentionCount</c>). Doc 44 names the content, not the fields.
        /// </summary>
        private void ReadHead(string relativePath, out long? lastSeq, out string? headHash, out Dictionary<string, long>? counts)
        {
            lastSeq = null;
            headHash = null;
            counts = null;
            var path = Resolve(relativePath);
            if (path is null || !File.Exists(path))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var e = doc.RootElement;
                var seqName = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("headSeq", out _) ? "headSeq" : "last_seq";
                var hashName = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("eventHeadHash", out _) ? "eventHeadHash" : "head_hash";
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(seqName, out var s) || !s.TryGetInt64(out var seq)
                    || !TryOptionalString(e, hashName, out headHash))
                {
                    Add("gc_head_malformed", relativePath, "head.json needs a last sequence, a head hash and row counts.");
                    return;
                }

                headHash = NormalizeGenesis(headHash);
                lastSeq = seq;
                if (e.TryGetProperty("counts", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    counts = [];
                    foreach (var property in c.EnumerateObject())
                    {
                        if (property.Value.TryGetInt64(out var n))
                        {
                            counts[property.Name] = n;
                        }
                    }
                }
                else if (e.TryGetProperty("eventCount", out _))
                {
                    counts = [];
                    foreach (var (name, field) in new[] { ("events", "eventCount"), ("messages", "messageCount"), ("versions", "versionCount"), ("mentions", "mentionCount") })
                    {
                        if (e.TryGetProperty(field, out var v) && v.TryGetInt64(out var n))
                        {
                            counts[name] = n;
                        }
                    }
                }
                else
                {
                    Add("gc_head_malformed", relativePath, "head.json needs a last sequence, a head hash and row counts.");
                    lastSeq = null;
                }
            }
            catch (JsonException)
            {
                Add("gc_head_malformed", relativePath, "head.json is not valid JSON.");
            }
        }

        private long CountLines(string relativePath)
        {
            var path = Resolve(relativePath);
            if (path is null || !File.Exists(path))
            {
                return 0;
            }

            return ReadLines(path).LongCount(l => l is null || l.Length > 0);
        }

        // ---- audit ----

        private AuditKeyVerifier? OpenAuditKey(string context)
        {
            if (options.AuditKey is null)
            {
                Add("audit_key_required", null, "The audit checkpoint key is needed to check " + context + "; give it with --audit-key.");
                return null;
            }

            var verifier = AuditKeyVerifier.TryCreate(options.AuditKey);
            if (verifier is null)
            {
                Add("audit_key_invalid", null, "The audit key is not an ECDSA P-256 public key.");
            }

            return verifier;
        }

        private void CheckAuditChain()
        {
            foreach (var required in new[] { AuditArchiveFormat.EntriesPath, AuditArchiveFormat.CheckpointsPath, AuditArchiveFormat.ArchivePath })
            {
                if (!_listed.Contains(required))
                {
                    Add("audit_file_missing", required, "The audit archive is missing a required file.");
                    return;
                }
            }

            using var verifier = OpenAuditKey("the audit chain");
            if (verifier is null)
            {
                return;
            }

            var marker = AuditArchiveFormat.TryReadArchive(File.ReadAllBytes(Resolve(AuditArchiveFormat.ArchivePath)!));
            if (marker is null || marker.Format != AuditArchiveFormat.ArchiveFormatId || marker.PartOfLiveChain)
            {
                Add("audit_archive_invalid", AuditArchiveFormat.ArchivePath, "The archive marker is missing, unknown or claims to be part of the live chain.");
                return;
            }

            var checkpoints = AuditArchiveFormat.TryReadCheckpoints(File.ReadAllBytes(Resolve(AuditArchiveFormat.CheckpointsPath)!));
            if (checkpoints is null)
            {
                Add("audit_malformed", AuditArchiveFormat.CheckpointsPath, "The checkpoint file is not valid.");
                return;
            }

            var tracker = new RowTracker();
            AuditReplayResult result;
            using (var stream = new FileStream(Resolve(AuditArchiveFormat.EntriesPath)!, FileMode.Open, FileAccess.Read, FileShare.Read, _bufferSize, FileOptions.SequentialScan))
            {
                result = AuditChainReplay.Replay(tracker.Track(AuditArchiveFormat.StreamRows(stream), ct), checkpoints, verifier, marker.StartCheckpointLastSeq is null);
            }

            if (!result.IsValid)
            {
                var failure = result.FirstMismatch!;
                Add("audit_chain_" + failure.Kind.ToString().ToLowerInvariant(), AuditArchiveFormat.EntriesPath, $"seq {failure.Seq}: {failure.Detail}");
                return;
            }

            _audit = new AuditSummary(result.RowsChecked, result.CheckpointsChecked, result.UncheckpointedTailRows, 0);
            var firstSeq = tracker.Count == 0 ? 0 : tracker.FirstSeq;
            var lastSeq = tracker.Count == 0 ? 0 : tracker.LastSeq;
            if (marker.RowCount != result.RowsChecked || marker.CheckpointCount != result.CheckpointsChecked
                || marker.UncheckpointedTailRows != result.UncheckpointedTailRows
                || marker.FirstRowSeq != firstSeq || marker.LastRowSeq != lastSeq)
            {
                Add("audit_archive_mismatch", AuditArchiveFormat.ArchivePath, "The archive marker's counts or range do not match the rows and checkpoints (a checkpoint or row was removed).");
            }
        }

        private void CheckProofs()
        {
            using var verifier = OpenAuditKey("the inclusion proofs");
            if (verifier is null)
            {
                return;
            }

            var checkpoints = new Dictionary<string, AuditCheckpointRecord>(StringComparer.Ordinal);
            if (_listed.Contains(AuditArchiveFormat.CheckpointsPath)
                && AuditArchiveFormat.TryReadCheckpoints(File.ReadAllBytes(Resolve(AuditArchiveFormat.CheckpointsPath)!)) is { } read)
            {
                foreach (var checkpoint in read)
                {
                    checkpoints.TryAdd(checkpoint.Id, checkpoint);
                }
            }

            long checkedProofs = 0;
            foreach (var entry in _entries.Where(e => e.Path.StartsWith(AuditArchiveFormat.ProofsFolder, StringComparison.Ordinal)))
            {
                ct.ThrowIfCancellationRequested();
                var path = Resolve(entry.Path);
                if (path is null || !File.Exists(path))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                var verdict = AuditInclusionProofVerifier.Verify(bytes, verifier);
                checkedProofs++;
                if (!verdict.IsValid)
                {
                    Add("audit_proof_invalid", entry.Path, verdict.Reason);
                    continue;
                }

                // A proof against a checkpoint that is also in checkpoints.jsonl must agree with it exactly.
                using var doc = JsonDocument.Parse(bytes);
                var id = ReadString(doc.RootElement, "checkpoint_id");
                var root = ReadString(doc.RootElement, "root");
                if (id is not null && checkpoints.TryGetValue(id, out var known)
                    && (root is null || !AuditHashing.HashesEqual(Convert.FromBase64String(root), known.MerkleRoot)))
                {
                    Add("audit_proof_invalid", entry.Path, "The proof's root differs from the exported checkpoint.");
                }
            }

            _audit = _audit is null ? new AuditSummary(0, 0, 0, checkedProofs) : _audit with { ProofsChecked = checkedProofs };
        }

        // ---- helpers ----

        private static IEnumerable<string?> ReadLines(string path)
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, _bufferSize);
            while (true)
            {
                string? line = null;
                var invalid = false;
                try
                {
                    line = reader.ReadLine();
                }
                catch (DecoderFallbackException)
                {
                    invalid = true;
                }

                if (invalid)
                {
                    yield return null;
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                if (line.Length > 0)
                {
                    yield return line;
                }
            }
        }

        /// <summary>The full path of a bundle-relative path, or null when it is a link or leaves the bundle directory.</summary>
        private string? Resolve(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            var current = root;
            foreach (var segment in relative.Split('/'))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                if (info.Exists && info.LinkTarget is not null)
                {
                    return null;
                }
            }

            return full;
        }

        private static bool IsSafePath(string path)
        {
            if (path.Length == 0 || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal)
                || path.StartsWith('/') || Path.IsPathRooted(path))
            {
                return false;
            }

            return path.Split('/').All(s => s.Length > 0 && s is not ("." or ".."));
        }
    }

    /// <summary>Counts the rows that pass through a replay, and notes the first and last seq, without holding them.</summary>
    private sealed class RowTracker
    {
        public long Count { get; private set; }

        public long FirstSeq { get; private set; }

        public long LastSeq { get; private set; }

        public IEnumerable<AuditRow> Track(IEnumerable<AuditRow> rows, CancellationToken ct)
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row is not null)
                {
                    if (Count == 0)
                    {
                        FirstSeq = row.Seq;
                    }

                    Count++;
                    LastSeq = row.Seq;
                }

                yield return row!;
            }
        }
    }

    /// <summary>The audit key as a checkpoint signature verifier: ECDSA P-256, SHA-256, fixed-size signatures.</summary>
    private sealed class AuditKeyVerifier : IAuditSignatureVerifier, IDisposable
    {
        private readonly ECDsa _key;

        private AuditKeyVerifier(ECDsa key) => _key = key;

        public static AuditKeyVerifier? TryCreate(byte[] publicKey)
        {
            var key = ECDsa.Create();
            try
            {
                key.ImportSubjectPublicKeyInfo(publicKey, out _);
                if (key.KeySize == 256)
                {
                    return new AuditKeyVerifier(key);
                }
            }
            catch (CryptographicException)
            {
            }

            key.Dispose();
            return null;
        }

        public bool Verify(ReadOnlySpan<byte> signedBytes, ReadOnlySpan<byte> signature)
        {
            try
            {
                return _key.VerifyData(signedBytes, signature, HashAlgorithmName.SHA256);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }

        public void Dispose() => _key.Dispose();
    }
}
