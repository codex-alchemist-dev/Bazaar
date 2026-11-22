using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bazaar.Server.Portability;

/// <summary>
/// One encrypted personal field as stored (doc 04, doc 14). It names the key but carries no key material: there is no
/// property here that could hold a DEK.
/// </summary>
public sealed record PersonalEnvelope(string Field, string KeyId, byte[] Nonce, byte[] Ciphertext);

/// <summary>A subject as the personal store knows it. An erased subject has a tombstone and no envelopes.</summary>
public sealed record PersonalSubjectState(string SubjectId, bool Erased, DateTimeOffset? ErasedAt = null);

/// <summary>Read-only view of the per-subject encrypted fields. The host implements it over the doc 04 store.</summary>
public interface IPersonalFieldSource
{
    /// <summary>Every subject, erased ones included (as tombstones).</summary>
    Task<IReadOnlyList<PersonalSubjectState>> ListSubjectsAsync(CancellationToken ct);

    /// <summary>Envelopes of a subject that is not erased. Never called for an erased subject.</summary>
    Task<IReadOnlyList<PersonalEnvelope>> ReadEnvelopesAsync(string subjectId, CancellationToken ct);
}

/// <summary>
/// Decrypts one envelope with the subject's existing DEK. It is only called for the plaintext path, only for subjects that
/// are not erased, and it must never create a key. Returns null when the key is gone.
/// </summary>
public interface IPersonalFieldDecryptor
{
    Task<string?> DecryptAsync(string subjectId, PersonalEnvelope envelope, CancellationToken ct);
}

/// <summary>Reads the install setting <c>portability.export.allow_plaintext_personal</c> (default false).</summary>
public interface IPersonalExportSettings
{
    Task<bool> AllowPlaintextPersonalAsync(CancellationToken ct);
}

/// <summary>An audit entry for personal-field exports. Subject ids and counts only: no field names, no values.</summary>
public sealed record PersonalExportAuditRecord(
    string Kind,
    string Actor,
    DateTimeOffset At,
    IReadOnlyList<string> Subjects,
    bool Plaintext,
    string? Refusal = null)
{
    public const string ExportKind = "portability.export.personal";
    public const string PlaintextKind = "portability.export.personal.plaintext";
    public const string RefusedKind = "portability.export.personal.refused";
}

/// <summary>Sink for personal-export audit entries. The host wires it to the audit log (doc 15). Throws when it cannot store.</summary>
public interface IPersonalExportAuditSink
{
    Task AppendAsync(PersonalExportAuditRecord record, CancellationToken ct);
}

/// <summary>
/// Result: bundle files (including <c>personal/index.json</c>), a notice for the owner when plaintext was produced, and the
/// paths of the plaintext files among <see cref="Files"/> (empty when none), so the bundle manifest can flag them (doc 44 section 4.6).
/// </summary>
public sealed record PersonalExportResult(
    IReadOnlyList<BundleFile> Files,
    string? PlaintextNotice,
    IReadOnlyList<string>? PlaintextPaths = null);

/// <summary>
/// Personal-field exporter (doc 44 sections 4.6 and 10). Always writes ciphertext envelopes with their key ids and never a
/// DEK. Plaintext needs the owner session flag, the install setting, and an audit entry stored first. Erased subjects are
/// exported as tombstones only, and their key is never requested. Anything doubtful fails closed.
/// </summary>
/// <remarks>
/// The bundle manifest (<see cref="ExportBundleWriter"/>) has no field for subject lists or plaintext flags yet, so this
/// exporter writes <c>personal/index.json</c> (hashed by the bundle manifest) with <c>plaintextPersonal</c> and the subject
/// list. A later slice may mirror it into manifest.json.
/// </remarks>
public sealed class PersonalFieldExporter(
    IPersonalFieldSource source,
    IPersonalFieldDecryptor decryptor,
    IPersonalExportSettings settings,
    IPersonalExportAuditSink audit,
    TimeProvider? time = null)
{
    public const string IndexPath = "personal/index.json";
    public const string OwnerNotice =
        "Plaintext personal data was written to this export. Erasure cannot recall this copy; protect or delete it.";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<PersonalExportResult> ExportAsync(PersonalExportScope scope, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Actor);

        try
        {
            return await RunAsync(scope, ct);
        }
        catch (PersonalExportRefusedException ex) when (ex.Reason != PersonalExportRefusal.AuditUnavailable)
        {
            await TryAuditRefusalAsync(scope, ex.Reason);
            throw;
        }
    }

    private async Task<PersonalExportResult> RunAsync(PersonalExportScope scope, CancellationToken ct)
    {
        if (scope.Role == PersonalExportRole.Agent)
        {
            throw new PersonalExportRefusedException(PersonalExportRefusal.AgentNotAllowed);
        }

        var wantPlaintext = scope.IncludePlaintext;
        if (wantPlaintext)
        {
            // Rule 2: the explicit owner-session flag, then the setting. Both must hold.
            if (scope.Role != PersonalExportRole.Owner || !scope.IsOwnerSession)
            {
                throw new PersonalExportRefusedException(PersonalExportRefusal.PlaintextNeedsOwnerSession);
            }
            if (!await settings.AllowPlaintextPersonalAsync(ct))
            {
                throw new PersonalExportRefusedException(PersonalExportRefusal.PlaintextSettingOff);
            }
        }

        var subjects = await SelectSubjectsAsync(scope, ct);

        // Audit the plaintext decision before any key is used. If the audit cannot be stored, nothing is decrypted.
        if (wantPlaintext)
        {
            await AppendAuditAsync(new PersonalExportAuditRecord(
                PersonalExportAuditRecord.PlaintextKind, scope.Actor, _time.GetUtcNow(),
                [.. subjects.Where(s => !s.Erased).Select(s => s.SubjectId)], true), ct);
        }

        var files = new List<BundleFile>();
        var plaintextPaths = new List<string>();
        var index = new List<object>();
        foreach (var subject in subjects)
        {
            if (subject.Erased)
            {
                // Rules 4 and 5: tombstone only. No envelopes are read and no key is requested.
                var erasedAt = subject.ErasedAt?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
                files.Add(new BundleFile(
                    "personal/" + subject.SubjectId + ".tombstone.json",
                    Utf8(JsonSerializer.Serialize(new { subject = subject.SubjectId, erased = true, erasedAt }))));
                index.Add(new { subject = subject.SubjectId, state = "tombstone", holdsCiphertext = false, plaintextPersonal = false });
                continue;
            }

            var envelopes = await source.ReadEnvelopesAsync(subject.SubjectId, ct);
            foreach (var envelope in envelopes)
            {
                ValidateEnvelope(envelope);
            }
            var ordered = envelopes.OrderBy(e => e.Field, StringComparer.Ordinal).ToList();

            var lines = new StringBuilder();
            foreach (var e in ordered)
            {
                lines.Append(JsonSerializer.Serialize(new
                {
                    field = e.Field,
                    keyId = e.KeyId,
                    nonce = Convert.ToBase64String(e.Nonce),
                    ciphertext = Convert.ToBase64String(e.Ciphertext),
                })).Append('\n');
            }
            files.Add(new BundleFile("personal/" + subject.SubjectId + ".enc.jsonl", Utf8(lines.ToString())));

            if (wantPlaintext)
            {
                var fields = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var e in ordered)
                {
                    var value = await decryptor.DecryptAsync(subject.SubjectId, e, ct)
                        ?? throw new PersonalExportRefusedException(PersonalExportRefusal.PlaintextUnavailable);
                    fields[e.Field] = value;
                }
                var plaintextPath = "personal/" + subject.SubjectId + ".json";
                files.Add(new BundleFile(plaintextPath, Utf8(JsonSerializer.Serialize(new { subject = subject.SubjectId, fields }))));
                plaintextPaths.Add(plaintextPath);
            }

            index.Add(new
            {
                subject = subject.SubjectId,
                state = "ciphertext",
                holdsCiphertext = true,
                keyIds = ordered.Select(e => e.KeyId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                plaintextPersonal = wantPlaintext,
            });
        }

        files.Add(new BundleFile(IndexPath, Utf8(JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            plaintextPersonal = wantPlaintext,
            subjects = index,
        }))));

        await AppendAuditAsync(new PersonalExportAuditRecord(
            PersonalExportAuditRecord.ExportKind, scope.Actor, _time.GetUtcNow(),
            [.. subjects.Select(s => s.SubjectId)], wantPlaintext), ct);

        return new PersonalExportResult(files, wantPlaintext ? OwnerNotice : null, plaintextPaths);
    }

    private async Task<List<PersonalSubjectState>> SelectSubjectsAsync(PersonalExportScope scope, CancellationToken ct)
    {
        var wanted = scope.SubjectIds;
        if (scope.Role == PersonalExportRole.Member)
        {
            // Another member exports only their own subject (doc 44 section 4.1).
            if (string.IsNullOrWhiteSpace(scope.RequesterSubjectId)
                || (wanted is not null && wanted.Any(id => !string.Equals(id, scope.RequesterSubjectId, StringComparison.Ordinal))))
            {
                throw new PersonalExportRefusedException(PersonalExportRefusal.SubjectOutOfScope);
            }
            wanted = new HashSet<string>([scope.RequesterSubjectId], StringComparer.Ordinal);
        }

        var all = await source.ListSubjectsAsync(ct);
        foreach (var s in all)
        {
            ValidateSubjectId(s.SubjectId);
        }
        var selected = wanted is null ? all.ToList() : all.Where(s => wanted.Contains(s.SubjectId)).ToList();
        if (wanted is not null && selected.Count != wanted.Count)
        {
            // A requested subject the store does not know is a doubt, not an empty result.
            throw new PersonalExportRefusedException(PersonalExportRefusal.SubjectOutOfScope);
        }
        return [.. selected.OrderBy(s => s.SubjectId, StringComparer.Ordinal)];
    }

    private static void ValidateSubjectId(string id)
    {
        // The id becomes a file name: letters, digits, '-' and '_' only, so no separator or traversal can get in.
        if (string.IsNullOrEmpty(id) || id.Length > 64 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new PersonalExportRefusedException(PersonalExportRefusal.InvalidSubjectId);
        }
    }

    private static void ValidateEnvelope(PersonalEnvelope e)
    {
        if (e is null || string.IsNullOrWhiteSpace(e.Field) || string.IsNullOrWhiteSpace(e.KeyId)
            || e.Nonce is null || e.Nonce.Length == 0 || e.Ciphertext is null)
        {
            throw new PersonalExportRefusedException(PersonalExportRefusal.InvalidEnvelope);
        }
    }

    private async Task AppendAuditAsync(PersonalExportAuditRecord record, CancellationToken ct)
    {
        try
        {
            await audit.AppendAsync(record, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PersonalExportRefusedException(PersonalExportRefusal.AuditUnavailable);
        }
    }

    private async Task TryAuditRefusalAsync(PersonalExportScope scope, PersonalExportRefusal reason)
    {
        try
        {
            await audit.AppendAsync(
                new PersonalExportAuditRecord(
                    PersonalExportAuditRecord.RefusedKind, scope.Actor, _time.GetUtcNow(), [], scope.IncludePlaintext, reason.ToString()),
                CancellationToken.None);
        }
        catch (Exception)
        {
            // The refusal already stands; a failed audit of it must not mask it.
        }
    }

    private static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);
}
