using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Contracts.Telemetry;
using Bazaar.Server.Security.Audit;

namespace Bazaar.Server.Diagnostics;

/// <summary>How one diagnosis request ended.</summary>
public enum CrashDiagnosisStatus
{
    /// <summary>A diagnosis was stored. See <see cref="CrashDiagnosisResult.Diagnosis"/>.</summary>
    Produced,

    /// <summary>The mode forbids this trigger, or no model is configured. Nothing ran.</summary>
    Disabled,

    /// <summary>The daily token cap was reached. The group shows "diagnosis paused (budget)".</summary>
    PausedBudget,

    /// <summary>The group or its report no longer exists, for example after retention or erasure.</summary>
    NoReport,

    /// <summary>The report could not be opened or the model failed. Nothing was stored.</summary>
    Failed,
}

/// <summary>The outcome of <see cref="CrashDiagnosisService.DiagnoseAsync"/>.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Diagnosis">The stored diagnosis when <paramref name="Status"/> is <see cref="CrashDiagnosisStatus.Produced"/>.</param>
public sealed record CrashDiagnosisResult(CrashDiagnosisStatus Status, StoredCrashDiagnosis? Diagnosis = null);

/// <summary>
/// The light server-side diagnosis (doc 43 section 9.1). It is read-only by construction: it holds the model, the budget, a
/// reader for stored reports and the diagnosis store, and nothing that can change a group, queue an effect or reach GitHub
/// except the posting path, which is a separate method that only comments the stored, labelled text. Report text is untrusted;
/// it is only ever placed in the model input as data and the output is cleaned to plain text before it is stored.
/// </summary>
public sealed class CrashDiagnosisService
{
    private readonly ICrashIntakeStore _groups;
    private readonly ICrashReportReader _reports;
    private readonly ICrashPayloadSealer _sealer;
    private readonly ICrashDiagnosisModel? _model;
    private readonly ICrashDiagnosisBudget _budget;
    private readonly ICrashDiagnosisStore _diagnoses;
    private readonly ICrashIssueFiler _filer;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _clock;
    private readonly CrashDiagnosisOptions _options;

    /// <summary>Creates the service over its seams. <paramref name="model"/> is null when no model is whitelisted for the role.</summary>
    public CrashDiagnosisService(
        ICrashIntakeStore groups,
        ICrashReportReader reports,
        ICrashPayloadSealer sealer,
        ICrashDiagnosisModel? model,
        ICrashDiagnosisBudget budget,
        ICrashDiagnosisStore diagnoses,
        ICrashIssueFiler filer,
        IAuditLog audit,
        TimeProvider clock,
        CrashDiagnosisOptions options)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(sealer);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(diagnoses);
        ArgumentNullException.ThrowIfNull(filer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _groups = groups;
        _reports = reports;
        _sealer = sealer;
        _model = model;
        _budget = budget;
        _diagnoses = diagnoses;
        _filer = filer;
        _audit = audit;
        _clock = clock;
        _options = options;
    }

    /// <summary>Diagnoses the newest report of a group and stores the labelled text.</summary>
    public async Task<CrashDiagnosisResult> DiagnoseAsync(string fingerprint, CrashDiagnosisTrigger trigger, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (_model is null || _options.Mode == CrashDiagnosisMode.Off || (trigger == CrashDiagnosisTrigger.Auto && _options.Mode != CrashDiagnosisMode.Auto))
        {
            return new CrashDiagnosisResult(CrashDiagnosisStatus.Disabled);
        }

        var group = await _groups.GetGroupAsync(fingerprint, ct).ConfigureAwait(false);
        if (group is null
            || await _reports.GetLatestReportAsync(fingerprint, ct).ConfigureAwait(false) is not { } stored)
        {
            return new CrashDiagnosisResult(CrashDiagnosisStatus.NoReport);
        }

        CrashReport report;
        try
        {
            var plain = await _sealer.OpenAsync(stored.InstallId, SealedPayloadKind.Report, stored.ReportId, stored.Body, ct).ConfigureAwait(false);
            report = JsonSerializer.Deserialize(plain, CrashContractsJsonContext.Default.CrashReport) ?? throw new JsonException("Empty report.");
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return new CrashDiagnosisResult(CrashDiagnosisStatus.Failed);
        }

        var input = DiagnosisText.BuildInput(report);
        var estimate = EstimateTokens(input);
        if (!await _budget.TryReserveAsync(estimate, ct).ConfigureAwait(false))
        {
            await AppendAuditAsync("crash.diagnosis.paused", fingerprint, null, ct).ConfigureAwait(false);
            return new CrashDiagnosisResult(CrashDiagnosisStatus.PausedBudget);
        }

        CrashDiagnosisModelOutput output;
        try
        {
            output = await _model.DiagnoseAsync(new CrashDiagnosisPrompt(DiagnosisText.SystemInstructions, input), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _budget.SettleAsync(estimate, 0, ct).ConfigureAwait(false);
            return new CrashDiagnosisResult(CrashDiagnosisStatus.Failed);
        }

        await _budget.SettleAsync(estimate, output.TokensUsed, ct).ConfigureAwait(false);
        var diagnosis = new StoredCrashDiagnosis(
            Guid.CreateVersion7(),
            fingerprint,
            stored.ReportId,
            CrashDiagnosisLabel.Unverified,
            _clock.GetUtcNow(),
            output.ModelLabel,
            SHA256.HashData(Encoding.UTF8.GetBytes(input)),
            DiagnosisText.Sanitize(output.Text, _options.MaxOutputChars),
            false,
            group.State.OccurrenceCount);
        await _diagnoses.AddAsync(diagnosis, ct).ConfigureAwait(false);
        await AppendAuditAsync("crash.diagnosis.produced", fingerprint, diagnosis, ct).ConfigureAwait(false);
        return new CrashDiagnosisResult(CrashDiagnosisStatus.Produced, diagnosis);
    }

    /// <summary>
    /// The auto pass (doc 43 section 9.1): in <see cref="CrashDiagnosisMode.Auto"/>, diagnoses groups that reached a filing
    /// threshold no diagnosis covers yet, then posts each one when posting is on. It stops at the first paused budget. Returns how
    /// many diagnoses it produced.
    /// </summary>
    public async Task<int> RunAutoAsync(int max = 5, CancellationToken ct = default)
    {
        if (_model is null || _options.Mode != CrashDiagnosisMode.Auto)
        {
            return 0;
        }

        var produced = 0;
        foreach (var fingerprint in await _diagnoses.ListNeedingAutoDiagnosisAsync(max, ct).ConfigureAwait(false))
        {
            var result = await DiagnoseAsync(fingerprint, CrashDiagnosisTrigger.Auto, ct).ConfigureAwait(false);
            if (result.Status == CrashDiagnosisStatus.PausedBudget)
            {
                break;
            }

            if (result.Status == CrashDiagnosisStatus.Produced)
            {
                produced++;
                _ = await PostToIssueAsync(fingerprint, ct).ConfigureAwait(false);
            }
        }

        return produced;
    }

    /// <summary>
    /// Posts the group's newest diagnosis to its issue as a separate comment that repeats the label. Only when
    /// <c>issue_diagnosis_post</c> is on, the issue exists and the diagnosis was not posted before. Returns true when it posted.
    /// </summary>
    public async Task<bool> PostToIssueAsync(string fingerprint, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (!_options.PostToIssue)
        {
            return false;
        }

        var group = await _groups.GetGroupAsync(fingerprint, ct).ConfigureAwait(false);
        var diagnosis = await _diagnoses.GetLatestAsync(fingerprint, ct).ConfigureAwait(false);
        if (group?.State.IssueNumber is not { } number || diagnosis is null || diagnosis.PostedToIssue)
        {
            return false;
        }

        try
        {
            await _filer.CommentAsync(number, $"**{CrashDiagnosisLabel.Unverified}** (model: {diagnosis.RoleModel}). This is a hint, not a verdict.\n\n{diagnosis.Text}", ct).ConfigureAwait(false);
        }
        catch (CrashIssueNotFoundException)
        {
            return false;
        }

        await _diagnoses.MarkPostedAsync(diagnosis.DiagnosisId, ct).ConfigureAwait(false);
        return true;
    }

    private static long EstimateTokens(string input) => (input.Length / 3) + 600;

    private Task AppendAuditAsync(string kind, string fingerprint, StoredCrashDiagnosis? diagnosis, CancellationToken ct)
    {
        var payload = diagnosis is null
            ? JsonSerializer.Serialize(new Dictionary<string, string> { ["fingerprint"] = fingerprint }, AuditPayloadContext.Default.DictionaryStringString)
            : JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["fingerprint"] = fingerprint,
                    ["diagnosisId"] = diagnosis.DiagnosisId.ToString("D"),
                    ["inputSha256"] = Convert.ToHexStringLower(diagnosis.InputHash),
                },
                AuditPayloadContext.Default.DictionaryStringString);
        return _audit.AppendAsync(new AuditEntry(_clock.GetUtcNow(), "crash-diagnosis", kind, payload), ct);
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AuditPayloadContext : System.Text.Json.Serialization.JsonSerializerContext;
