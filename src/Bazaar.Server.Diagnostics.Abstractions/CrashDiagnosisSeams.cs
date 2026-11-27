namespace Bazaar.Server.Diagnostics;

/// <summary>The label every diagnosis carries (doc 43 section 9.1). The writer rejects any other value.</summary>
public static class CrashDiagnosisLabel
{
    /// <summary>The constant label text.</summary>
    public const string Unverified = "unverified diagnosis";
}

/// <summary>
/// How the diagnosis role is registered with the model gateway (doc 05, doc 43 section 9.1): its own role name and no tools at
/// all. The model can only return text; it cannot file or edit issues, change a group, post to a GC, write to a repo or reach the network.
/// </summary>
public static class CrashDiagnosisRole
{
    /// <summary>The role name in the per-role model policy.</summary>
    public const string Name = "diagnosis";

    /// <summary>The tools the role is registered with. Always empty; a test asserts it.</summary>
    public static IReadOnlyList<string> Tools { get; } = [];
}

/// <summary>What the operator set in <c>telemetry.crash.diagnosis</c>.</summary>
public enum CrashDiagnosisMode
{
    /// <summary>Diagnosis never runs.</summary>
    Off,

    /// <summary>An owner asks for each diagnosis (the default).</summary>
    Ask,

    /// <summary>Diagnosis runs on its own after a group reaches the filing thresholds.</summary>
    Auto,
}

/// <summary>Who started a diagnosis.</summary>
public enum CrashDiagnosisTrigger
{
    /// <summary>An owner clicked Diagnose, after the dialog showed the input size and model label.</summary>
    Owner,

    /// <summary>The server started it in <see cref="CrashDiagnosisMode.Auto"/>.</summary>
    Auto,
}

/// <summary>Settings the diagnosis reads (doc 43 section 7).</summary>
/// <param name="Mode"><c>telemetry.crash.diagnosis</c>.</param>
/// <param name="PostToIssue"><c>telemetry.crash.issue_diagnosis_post</c>.</param>
/// <param name="MaxOutputChars">The longest stored text. Longer output is cut.</param>
public sealed record CrashDiagnosisOptions(CrashDiagnosisMode Mode = CrashDiagnosisMode.Ask, bool PostToIssue = false, int MaxOutputChars = 4000);

/// <summary>The text sent to the model. <see cref="Input"/> is built from the redacted report only.</summary>
/// <param name="System">Fixed instructions. They say the input is untrusted data.</param>
/// <param name="Input">The redacted report as plain lines.</param>
public sealed record CrashDiagnosisPrompt(string System, string Input);

/// <summary>What the model returned.</summary>
/// <param name="Text">The diagnosis text.</param>
/// <param name="ModelLabel">The whitelisted model's label for display, never a provider key.</param>
/// <param name="TokensUsed">Tokens spent, for the budget.</param>
public sealed record CrashDiagnosisModelOutput(string Text, string ModelLabel, long TokensUsed);

/// <summary>The model behind the <see cref="CrashDiagnosisRole"/>. It takes text and returns text; there is no tool surface.</summary>
public interface ICrashDiagnosisModel
{
    /// <summary>Runs one diagnosis. Throws when the model is unavailable.</summary>
    Task<CrashDiagnosisModelOutput> DiagnoseAsync(CrashDiagnosisPrompt prompt, CancellationToken ct = default);
}

/// <summary>The company's daily diagnosis token cap (doc 05 and doc 15 quotas).</summary>
public interface ICrashDiagnosisBudget
{
    /// <summary>Reserves an estimate for one run. False when the daily cap would be exceeded.</summary>
    Task<bool> TryReserveAsync(long estimatedTokens, CancellationToken ct = default);

    /// <summary>Records what the run really used, so the day's total is right.</summary>
    Task SettleAsync(long reservedTokens, long usedTokens, CancellationToken ct = default);
}

/// <summary>A diagnosis as stored.</summary>
/// <param name="DiagnosisId">Unique id.</param>
/// <param name="Fingerprint">The group.</param>
/// <param name="ReportId">The report it was made from, or null.</param>
/// <param name="Label">Always <see cref="CrashDiagnosisLabel.Unverified"/>.</param>
/// <param name="ProducedAt">When it was made.</param>
/// <param name="RoleModel">The model label.</param>
/// <param name="InputHash">SHA-256 of the exact input.</param>
/// <param name="Text">The sanitised text.</param>
/// <param name="PostedToIssue">True once it was posted to the issue.</param>
/// <param name="OccurrenceCount">The group's occurrence count when it was made. Auto mode compares thresholds against it.</param>
public sealed record StoredCrashDiagnosis(
    Guid DiagnosisId,
    string Fingerprint,
    Guid? ReportId,
    string Label,
    DateTimeOffset ProducedAt,
    string RoleModel,
    byte[] InputHash,
    string Text,
    bool PostedToIssue,
    long OccurrenceCount = 0);

/// <summary>Stores diagnoses. Implementations refuse any label other than <see cref="CrashDiagnosisLabel.Unverified"/>.</summary>
public interface ICrashDiagnosisStore
{
    /// <summary>Stores a diagnosis.</summary>
    Task AddAsync(StoredCrashDiagnosis diagnosis, CancellationToken ct = default);

    /// <summary>Returns the newest diagnosis of a group, or null.</summary>
    Task<StoredCrashDiagnosis?> GetLatestAsync(string fingerprint, CancellationToken ct = default);

    /// <summary>
    /// Lists groups, oldest first, that reached a filing threshold (1, 10, 100 or 1000 occurrences, doc 43 section 8.4) which no
    /// stored diagnosis covers yet: no diagnosis at all, or the highest threshold reached is above the one the newest diagnosis saw.
    /// Groups still in backoff after a failed attempt (<see cref="RecordAutoFailureAsync"/>) are left out until their time comes.
    /// </summary>
    Task<IReadOnlyList<string>> ListNeedingAutoDiagnosisAsync(DateTimeOffset now, int max, CancellationToken ct = default);

    /// <summary>
    /// Records a failed automatic attempt so the group waits longer before the next one (10 minutes, then doubling, at most a day).
    /// Storing a diagnosis clears the backoff.
    /// </summary>
    Task RecordAutoFailureAsync(string fingerprint, DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Marks a diagnosis as posted to the issue.</summary>
    Task MarkPostedAsync(Guid diagnosisId, CancellationToken ct = default);
}

/// <summary>Reads back the newest stored report of a group, for diagnosis.</summary>
public interface ICrashReportReader
{
    /// <summary>Returns the newest report of the group by receive time, or null when none is stored.</summary>
    Task<StoredCrashReport?> GetLatestReportAsync(string fingerprint, CancellationToken ct = default);
}

/// <summary>
/// Supplies a short-lived installation token of the system GitHub App, narrowed to issue writes on the one issue repository
/// (doc 21, doc 14). The module never sees a key. The host binds this to the Identity Service once the owner registers the App.
/// </summary>
public interface ICrashGitHubTokenSource
{
    /// <summary>Returns a token for one call. It is not stored or logged.</summary>
    Task<string> GetTokenAsync(CancellationToken ct = default);
}
