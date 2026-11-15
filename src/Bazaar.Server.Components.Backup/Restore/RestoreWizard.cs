using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bazaar.Server.Components.Backup.Recovery;
using Bazaar.Server.Security.Audit.Checkpoints;

namespace Bazaar.Server.Components.Backup.Restore;

/// <summary>Where a restore stands. Writes are enabled only in <see cref="WritesEnabled"/>.</summary>
public enum RestoreState
{
    NotStarted,
    Running,

    /// <summary>Everything restored and the audit chain verified; the install is read-only until the owner confirms.</summary>
    AwaitingOwnerConfirmation,

    /// <summary>The owner confirmed and writes are on. Terminal.</summary>
    WritesEnabled,

    /// <summary>A step failed. Writes stay off. Terminal.</summary>
    Failed,
}

/// <summary>The owner's explicit consent to restore over a populated install. It names the restore, so it cannot be reused.</summary>
public sealed record OwnerOverwriteConfirmation(string RestoreId, string ConfirmedBy);

/// <summary>The owner's explicit consent to enable writes after a restore. It names the restore, so it cannot be reused.</summary>
public sealed record OwnerWriteConfirmation(string RestoreId, string ConfirmedBy);

/// <summary>One restore request. <see cref="Passphrase"/> is never logged or audited.</summary>
/// <param name="BundlePath">The sealed recovery bundle file.</param>
/// <param name="Passphrase">The passphrase the bundle is sealed under.</param>
/// <param name="PointInTime">Restore the database to this UTC time, or null for the end of the last backup set.</param>
/// <param name="OverwriteConfirmation">Required, and only honoured, when the install already holds data.</param>
public sealed record RestoreWizardRequest(
    string BundlePath,
    string Passphrase,
    DateTimeOffset? PointInTime = null,
    OwnerOverwriteConfirmation? OverwriteConfirmation = null);

/// <summary>Operator settings for the wizard. Defaults are the safe ones.</summary>
public sealed record RestoreWizardOptions
{
    /// <summary>
    /// When false (default), a bundle without a KEK export is refused: encrypted fields would be unreadable. The operator
    /// may allow it for a bundle that is known to carry no encrypted data.
    /// </summary>
    public bool AllowBundleWithoutKekExport { get; init; }
}

/// <summary>Result of a wizard call. <see cref="FailedStep"/> and <see cref="Message"/> are set on failure.</summary>
public sealed record RestoreWizardOutcome(
    string RestoreId,
    RestoreState State,
    string? FailedStep,
    string? Message,
    RestoreAuditVerification? AuditVerification)
{
    public bool IsSuccess => State is RestoreState.AwaitingOwnerConfirmation or RestoreState.WritesEnabled;
}

/// <summary>Audit payload for every wizard event. Ids, step names and counts only; never secrets.</summary>
internal sealed record RestoreAuditPayload(
    string RestoreId,
    string? Step = null,
    string? Reason = null,
    long? FailedSeq = null,
    int? RowsVerified = null,
    string? ConfirmedBy = null,
    DateTimeOffset? PointInTime = null);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RestoreAuditPayload))]
internal sealed partial class RestoreAuditJsonContext : JsonSerializerContext;

/// <summary>
/// The restore wizard for disaster restore and migration (doc 48 section 6): verify the bundle, restore keys, database,
/// Soul folders and Vault, rebuild indexes, verify the audit chain from the last anchored checkpoint, stay read-only,
/// and enable writes only on the owner's explicit confirmation. Every step is audited under the restore id.
/// </summary>
/// <remarks>
/// Fail-closed rules: writes are turned off before the first restore step and stay off on any failure; a populated install
/// is refused unless the owner confirmed that exact restore id and live data could be preserved; a missing anchor, a
/// checkpoint that differs from the bundle's head, or any chain mismatch fails the restore and is audited. Audit events
/// carry the restore id and step names only. One wizard instance runs one restore.
/// </remarks>
public sealed class RestoreWizard
{
    private readonly IRestoreTargetProbe _probe;
    private readonly ILiveDataPreserver? _preserver;
    private readonly IRestoreBackupSource _backups;
    private readonly IRestoreKeyRestorer _keys;
    private readonly IRestoreDatabaseRestorer _database;
    private readonly IRestoreFilesRestorer _files;
    private readonly IRestoreIndexRebuilder _indexes;
    private readonly IRestoreAuditEvidenceSource _evidence;
    private readonly IAuditCheckpointSigner _signer;
    private readonly IRestoreAuditSink _audit;
    private readonly IRestoreWriteGate _gate;
    private readonly RestoreWizardOptions _options;
    private readonly object _lock = new();
    private RestoreState _state = RestoreState.NotStarted;

    public RestoreWizard(
        IRestoreTargetProbe probe,
        ILiveDataPreserver? preserver,
        IRestoreBackupSource backups,
        IRestoreKeyRestorer keys,
        IRestoreDatabaseRestorer database,
        IRestoreFilesRestorer files,
        IRestoreIndexRebuilder indexes,
        IRestoreAuditEvidenceSource evidence,
        IAuditCheckpointSigner signer,
        IRestoreAuditSink audit,
        IRestoreWriteGate gate,
        RestoreWizardOptions? options = null,
        string? restoreId = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _preserver = preserver;
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _indexes = indexes ?? throw new ArgumentNullException(nameof(indexes));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _options = options ?? new RestoreWizardOptions();
        RestoreId = string.IsNullOrWhiteSpace(restoreId) ? Guid.CreateVersion7().ToString("N") : restoreId;
    }

    /// <summary>Identifies this restore in every audit event. The owner quotes it to confirm an overwrite or the final step.</summary>
    public string RestoreId { get; }

    /// <summary>The current state.</summary>
    public RestoreState State
    {
        get
        {
            lock (_lock)
            {
                return _state;
            }
        }
    }

    /// <summary>Runs the restore up to the read-only checkpoint. Writes are still off when this returns.</summary>
    public async Task<RestoreWizardOutcome> RunAsync(RestoreWizardRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.BundlePath);
        ArgumentException.ThrowIfNullOrEmpty(request.Passphrase);
        if (request.PointInTime is { } t && t.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Point in time must be UTC.", nameof(request));
        }

        lock (_lock)
        {
            if (_state != RestoreState.NotStarted)
            {
                throw new InvalidOperationException("This restore already ran. Create a new wizard for another restore.");
            }

            _state = RestoreState.Running;
        }

        var step = "start";
        RecoveryBundlePayload? payload = null;
        try
        {
            // If the start cannot be audited, nothing has been touched yet and the restore stops.
            await AuditAsync("restore.started", new RestoreAuditPayload(RestoreId, PointInTime: request.PointInTime), ct).ConfigureAwait(false);

            step = "verify-bundle";
            var read = RecoveryBundleFile.Read(request.BundlePath, request.Passphrase);
            payload = read.Payload;
            if (payload is null)
            {
                return await FailAsync(step, read.Message ?? "The bundle could not be read.", null).ConfigureAwait(false);
            }

            if (payload.AuditCheckpointHead is not { Length: > 0 } bundleHead)
            {
                return await FailAsync(step, "The bundle names no audit checkpoint head, so the chain cannot be verified.", null).ConfigureAwait(false);
            }

            if (payload.KekExport.Length == 0 && !_options.AllowBundleWithoutKekExport)
            {
                return await FailAsync(step, "The bundle holds no key export and the operator has not allowed that.", null).ConfigureAwait(false);
            }

            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "check-target";
            var populated = await _probe.IsPopulatedAsync(ct).ConfigureAwait(false);
            if (populated)
            {
                var c = request.OverwriteConfirmation;
                if (c is null || !string.Equals(c.RestoreId, RestoreId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(c.ConfirmedBy))
                {
                    return await FailAsync(step, "The install already holds data. Restore needs the owner's confirmation naming this restore id.", null).ConfigureAwait(false);
                }

                if (_preserver is null)
                {
                    return await FailAsync(step, "The install already holds data and no way to preserve it is configured, so it will not be overwritten.", null).ConfigureAwait(false);
                }

                await AuditAsync("restore.overwrite_confirmed", new RestoreAuditPayload(RestoreId, ConfirmedBy: c.ConfirmedBy), ct).ConfigureAwait(false);
            }

            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "verify-backup-sets";
            foreach (var set in payload.BackupSets)
            {
                var found = await _backups.DescribeAsync(set.Name, ct).ConfigureAwait(false);
                if (found is not { } d
                    || d.SizeBytes != set.SizeBytes
                    || !string.Equals(d.Sha256Hex, set.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    return await FailAsync(step, $"Backup set '{set.Name}' is missing or does not match its recorded checksum.", null).ConfigureAwait(false);
                }
            }

            await StepDoneAsync(step, ct).ConfigureAwait(false);

            // Writes go off before the first restore step and stay off until the owner confirms.
            step = "enter-read-only";
            await _gate.EnterReadOnlyAsync(RestoreId, ct).ConfigureAwait(false);
            await StepDoneAsync(step, ct).ConfigureAwait(false);

            if (populated)
            {
                step = "preserve-live-data";
                await _preserver!.PreserveAsync(RestoreId, ct).ConfigureAwait(false);
                await StepDoneAsync(step, ct).ConfigureAwait(false);
            }

            step = "restore-keys";
            if (payload.KekExport.Length > 0)
            {
                await _keys.RestoreKeysAsync(payload.KekExport, ct).ConfigureAwait(false);
            }

            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "restore-database";
            await _database.RestoreDatabaseAsync(payload.BackupSets, request.PointInTime, ct).ConfigureAwait(false);
            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "restore-soul-folders";
            await _files.RestoreSoulFoldersAsync(payload.BackupSets, ct).ConfigureAwait(false);
            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "restore-vault";
            await _files.RestoreVaultAsync(payload.BackupSets, ct).ConfigureAwait(false);
            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "rebuild-indexes";
            await _indexes.RebuildAsync(ct).ConfigureAwait(false);
            await StepDoneAsync(step, ct).ConfigureAwait(false);

            step = "verify-audit-chain";
            const string auditFailed = "restore.audit_verification_failed";
            var evidence = await _evidence.LoadAsync(bundleHead, ct).ConfigureAwait(false);
            if (evidence is null)
            {
                return await FailAsync(step, "No anchored checkpoint matches the bundle's audit head.", null, auditFailed).ConfigureAwait(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(evidence.Anchor.ChainHeadHash, bundleHead))
            {
                return await FailAsync(step, "The anchored checkpoint's head differs from the bundle's audit head.", null, auditFailed).ConfigureAwait(false);
            }

            var verification = RestoreAuditVerifier.Verify(evidence.Anchor, evidence.RowsAfterAnchor, _signer, evidence.Signature);
            if (!verification.IsValid)
            {
                var where = verification.FailedSeq?.ToString(CultureInfo.InvariantCulture) ?? "n/a";
                return await FailAsync(step, $"Audit chain verification failed: {verification.Reason} at row {where}.", verification, auditFailed).ConfigureAwait(false);
            }

            await StepDoneAsync(step, ct).ConfigureAwait(false);

            // Record the read-only checkpoint before the state moves, so a state of awaiting confirmation is always audited.
            await AuditAsync("restore.read_only", new RestoreAuditPayload(RestoreId, RowsVerified: verification.RowsChecked), ct).ConfigureAwait(false);
            lock (_lock)
            {
                _state = RestoreState.AwaitingOwnerConfirmation;
            }

            return new RestoreWizardOutcome(RestoreId, RestoreState.AwaitingOwnerConfirmation, null, null, verification);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _state = RestoreState.Failed;
            }

            throw;
        }
        catch (Exception ex)
        {
            // The message is the exception type only: exception text from restorers could echo paths or values.
            return await FailAsync(step, $"Step failed: {ex.GetType().Name}.", null).ConfigureAwait(false);
        }
        finally
        {
            // Zeroes the KEK export and the owner recovery material.
            payload?.Dispose();
        }
    }

    /// <summary>
    /// Enables writes. Valid only after a successful run, and only for the owner's confirmation naming this restore id.
    /// Any other call is refused and leaves the install read-only.
    /// </summary>
    public async Task<RestoreWizardOutcome> ConfirmWritesAsync(OwnerWriteConfirmation confirmation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        lock (_lock)
        {
            if (_state != RestoreState.AwaitingOwnerConfirmation)
            {
                throw new InvalidOperationException($"Writes cannot be confirmed in state {_state}.");
            }

            if (!string.Equals(confirmation.RestoreId, RestoreId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(confirmation.ConfirmedBy))
            {
                throw new InvalidOperationException("The confirmation does not name this restore or has no owner.");
            }

            // Claim the transition so a second confirmation cannot run the gate twice.
            _state = RestoreState.Running;
        }

        try
        {
            // Audit first: the chain shows the confirmation before the writes it permits.
            await AuditAsync("restore.writes_confirmed", new RestoreAuditPayload(RestoreId, ConfirmedBy: confirmation.ConfirmedBy), ct).ConfigureAwait(false);
            await _gate.EnableWritesAsync(RestoreId, ct).ConfigureAwait(false);
        }
        catch
        {
            lock (_lock)
            {
                _state = RestoreState.AwaitingOwnerConfirmation;
            }

            throw;
        }

        lock (_lock)
        {
            _state = RestoreState.WritesEnabled;
        }

        return new RestoreWizardOutcome(RestoreId, RestoreState.WritesEnabled, null, null, null);
    }

    private Task StepDoneAsync(string step, CancellationToken ct) =>
        AuditAsync("restore.step_completed", new RestoreAuditPayload(RestoreId, Step: step), ct);

    private async Task<RestoreWizardOutcome> FailAsync(string step, string message, RestoreAuditVerification? verification, string kind = "restore.failed")
    {
        lock (_lock)
        {
            _state = RestoreState.Failed;
        }

        // Best effort: the failure is already decided, so an audit error here must not turn it into an exception.
        try
        {
            var payload = new RestoreAuditPayload(RestoreId, Step: step, Reason: verification?.Reason.ToString(), FailedSeq: verification?.FailedSeq);
            await AuditAsync(kind, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Writes stay off whether or not the failure could be recorded.
        }

        return new RestoreWizardOutcome(RestoreId, RestoreState.Failed, step, message, verification);
    }

    private Task AuditAsync(string kind, RestoreAuditPayload payload, CancellationToken ct) =>
        _audit.AppendAsync(new RecoveryAuditEvent(kind, JsonSerializer.Serialize(payload, RestoreAuditJsonContext.Default.RestoreAuditPayload)), ct);
}
