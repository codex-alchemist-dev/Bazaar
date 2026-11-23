using System.Text.RegularExpressions;
using Bazaar.Contracts.Coding;
using Bazaar.Server.Coding.Checks;
using Bazaar.Server.Coding.Workspace;
using Bazaar.Server.CodingPermissions;
using Bazaar.Server.CodingStore;

namespace Bazaar.Server.CodingRunner.Handlers;

/// <summary>Limits for <c>run_checks</c>.</summary>
public sealed record RunChecksToolOptions
{
    /// <summary>The timeout for a command that names none. Doc 17 default: 15 minutes.</summary>
    public TimeSpan DefaultCommandTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest timeout any command may have. Doc 16's exec maximum: 2 hours.</summary>
    public TimeSpan MaxCommandTimeout { get; init; } = TimeSpan.FromHours(2);

    /// <summary>Most commands in one run.</summary>
    public int MaxCommands { get; init; } = 16;

    /// <summary>Throws when a value is out of range.</summary>
    public void Validate()
    {
        if (DefaultCommandTimeout <= TimeSpan.Zero || MaxCommandTimeout < DefaultCommandTimeout || MaxCommandTimeout > TimeSpan.FromHours(2))
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultCommandTimeout), "Timeouts must be positive, default at most max, max at most 2 hours.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(MaxCommands, 1);
    }
}

/// <summary>
/// The <c>run_checks</c> tool (doc 17: server-owned verification; the agent requests, never edits). It runs the host-configured
/// check commands in the agent's guest VM, reads test counts from the test tool's summary, runs the Coding module's scanners
/// over the host-supplied changed files, computes the verdict itself, appends a <see cref="CheckRunRecord"/> and returns the summary.
/// A timeout, a cancel, a guest fault or counts that cannot be read give verdict <c>error</c>, never a pass.
/// </summary>
public sealed partial class RunChecksToolHandler : ICodingToolHandler
{
    /// <summary>The tool name.</summary>
    public const string ToolName = "run_checks";

    private const int _logCap = 1_024;

    private static readonly IReadOnlySet<string> _defaultAllowed = new HashSet<string>(StringComparer.Ordinal) { FlagCodes.TestFileChanged };

    private readonly ICheckSubjectSource _subjects;
    private readonly IGuestWorktreeSource _guests;
    private readonly ICheckRunStore _store;
    private readonly RunChecksToolOptions _options;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler. The options are validated here.</summary>
    public RunChecksToolHandler(
        ICheckSubjectSource subjects,
        IGuestWorktreeSource guests,
        ICheckRunStore store,
        RunChecksToolOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(guests);
        ArgumentNullException.ThrowIfNull(store);
        _subjects = subjects;
        _guests = guests;
        _store = store;
        _options = options ?? new RunChecksToolOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => ToolName;

    /// <inheritdoc />
    public ToolCapabilityClass CapabilityClass => ToolCapabilityClass.Read;

    /// <inheritdoc />
    public bool WritesWorktree => true;

    /// <inheritdoc />
    public async Task<CodingToolOutput> ExecuteAsync(CodingToolCall toolCall, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        try
        {
            _ = new CodingToolArgs(toolCall.Args);
        }
        catch (CodingToolArgumentException ex)
        {
            return ex.ToOutput();
        }

        if (string.IsNullOrWhiteSpace(toolCall.WorktreeKey))
        {
            return CodingToolResults.Failure("no_worktree", "The call names no worktree this agent holds.", CodingToolResults.MalformedExitCode);
        }

        CheckSubject? subject;
        GuestWorktreeTarget? target;
        try
        {
            subject = await _subjects.ResolveAsync(toolCall.AgentId, toolCall.WorktreeKey, cancellationToken).ConfigureAwait(false);
            target = await _guests.ResolveAsync(toolCall.AgentId, toolCall.WorktreeKey, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return CodingToolResults.Failure("checks_unavailable", "The checks for this worktree could not be prepared.");
        }

        if (target is null)
        {
            return CodingToolResults.Failure("no_worktree", "The call names no worktree this agent holds.", CodingToolResults.MalformedExitCode);
        }

        if (subject is null || !ValidSubject(subject))
        {
            return CodingToolResults.Failure("checks_unavailable", "No valid check configuration exists for this worktree.");
        }

        var exitCodes = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var command in subject.Commands)
        {
            exitCodes[command.Tool] = null;
        }

        var run = new RunState();
        try
        {
            await RunCommandsAsync(subject, target, exitCodes, run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is recorded: an error row goes in with no token, then the cancel continues up to the runner.
            run.Error = true;
            run.Cancelled = true;
            await TryAppendAsync(Build(subject, exitCodes, run, subject.ChangedFiles), CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        var record = Build(subject, exitCodes, run, subject.ChangedFiles);
        var recorded = await TryAppendAsync(record, cancellationToken).ConfigureAwait(false);
        if (!recorded)
        {
            return CodingToolResults.Failure("check_run_not_recorded", "The check run could not be stored, so no verdict is claimed.");
        }

        var passed = record.Verdict == CheckVerdict.Pass;
        var outcome = new ExecOutcome(passed ? 0 : 1, TimedOut: run.TimedOut);
        return new CodingToolOutput(outcome, CodingToolResults.Json(w =>
        {
            w.WriteString("verdict", record.Verdict);
            w.WriteString("check_run_id", record.Id.ToString("D"));
            w.WriteString("commit", record.Commit);
            w.WriteStartObject("tool_exit_codes");
            foreach (var (tool, code) in exitCodes)
            {
                if (code is { } value)
                {
                    w.WriteNumber(tool, value);
                }
                else
                {
                    w.WriteNull(tool);
                }
            }

            w.WriteEndObject();
            WriteCount(w, "tests_passed", record.TestsPassed);
            WriteCount(w, "tests_failed", record.TestsFailed);
            WriteCount(w, "tests_skipped", record.TestsSkipped);
            WriteCount(w, "test_count_base", record.TestCountBase);
            w.WriteStartArray("flags");
            foreach (var flag in run.FlagDetails)
            {
                w.WriteStartObject();
                w.WriteString("code", flag.Code);
                w.WriteString("path", flag.Path);
                w.WriteString("detail", flag.Detail);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            if (run.ErrorReason is { } reason)
            {
                w.WriteString("error_reason", reason);
            }

            w.WriteBoolean("timed_out", run.TimedOut);
        }));
    }

    private static void WriteCount(System.Text.Json.Utf8JsonWriter w, string name, int? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    private bool ValidSubject(CheckSubject s)
    {
        if (string.IsNullOrWhiteSpace(s.Commit) || s.Commands.Count == 0 || s.Commands.Count > _options.MaxCommands || s.TestCountTolerance < 0)
        {
            return false;
        }

        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in s.Commands)
        {
            if (string.IsNullOrWhiteSpace(c.Tool) || !tools.Add(c.Tool) || c.Argv.Count == 0 || c.Argv.Any(a => string.IsNullOrWhiteSpace(a) || a.Contains('\0')))
            {
                return false;
            }

            if (c.Timeout is { } t && (t <= TimeSpan.Zero || t > _options.MaxCommandTimeout))
            {
                return false;
            }
        }

        return s.Commands.Count(c => c.Kind == CheckToolKind.Build) == 1;
    }

    private async Task RunCommandsAsync(CheckSubject subject, GuestWorktreeTarget target, Dictionary<string, int?> exitCodes, RunState run, CancellationToken cancellationToken)
    {
        foreach (var command in subject.Commands)
        {
            var scanner = command.Kind == CheckToolKind.Test ? new TestSummaryScanner() : null;
            GuestCommandResult result;
            try
            {
                result = await GuestCommand.RunAsync(
                    target,
                    command.Argv,
                    command.Cwd,
                    command.Timeout ?? _options.DefaultCommandTimeout,
                    _logCap,
                    scanner is null ? null : scanner.Observe,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
                run.Error = true;
                run.ErrorReason ??= "check_cwd_invalid";
                return;
            }

            if (result.TimedOut)
            {
                run.Error = true;
                run.TimedOut = true;
                run.ErrorReason ??= "timed_out";
                return;
            }

            if (result.Faulted || result.ExitCode is null)
            {
                run.Error = true;
                run.ErrorReason ??= "guest_unavailable";
                return;
            }

            exitCodes[command.Tool] = result.ExitCode;
            if (scanner is not null)
            {
                run.TestCounts.Add(scanner.Complete());
            }

            if (result.ExitCode != 0 && command.Kind == CheckToolKind.Build)
            {
                // A failed build makes the later tools meaningless; they stay null.
                return;
            }
        }
    }

    private CheckRunRecord Build(CheckSubject subject, Dictionary<string, int?> exitCodes, RunState run, IReadOnlyList<ChangedFile> files)
    {
        var buildCommand = subject.Commands.First(c => c.Kind == CheckToolKind.Build);
        var buildExit = exitCodes[buildCommand.Tool];

        var findings = new List<FlagFinding>();
        findings.AddRange(FakeSuccessScanner.Scan(files));
        findings.AddRange(TestFileChangeFlagger.Flag(files));

        int? passed = null, failed = null, skipped = null;
        var testCommands = subject.Commands.Where(c => c.Kind == CheckToolKind.Test).ToList();
        var ranTests = testCommands.Count > 0 && testCommands.All(c => exitCodes[c.Tool] is not null);
        var buildFailed = buildExit is not null and not 0;
        if (ranTests && run.TestCounts.All(c => c is not null) && run.TestCounts.Count == testCommands.Count)
        {
            passed = run.TestCounts.Sum(c => c!.Passed);
            failed = run.TestCounts.Sum(c => c!.Failed);
            skipped = run.TestCounts.Sum(c => c!.Skipped);
        }

        FlagFinding? countFinding = null;
        if (subject.BaseTestCount is { } baseCount && passed is not null)
        {
            countFinding = TestCountGate.Evaluate(baseCount, passed.Value + failed!.Value + skipped!.Value, subject.TestCountTolerance);
            if (countFinding is not null)
            {
                findings.Add(countFinding);
            }
        }

        string verdict;
        if (run.Error)
        {
            verdict = CheckVerdict.Error;
        }
        else if (subject.BaseTestCount is not null && passed is null && !buildFailed)
        {
            // The count gate cannot be applied, so the run cannot be called a pass or a clean fail.
            verdict = CheckVerdict.Error;
            run.Error = true;
            run.ErrorReason ??= "test_counts_unreadable";
        }
        else
        {
            var allowed = subject.AllowedFlagCodes ?? _defaultAllowed;
            var summaryPass = CheckSummary.Summarize(buildExit, findings.Where(f => f.Code != FlagCodes.TestCountDropped).ToList(), countFinding, allowed);
            var allZero = exitCodes.Values.All(c => c == 0);
            verdict = summaryPass && allZero ? CheckVerdict.Pass : CheckVerdict.Fail;
        }

        run.FlagDetails = findings;
        var codes = findings.Select(f => f.Code).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return new CheckRunRecord(
            Guid.NewGuid(),
            subject.Commit,
            subject.BaseCommit,
            buildExit,
            passed,
            failed,
            skipped,
            subject.BaseTestCount,
            codes,
            _time.GetUtcNow(),
            verdict,
            new Dictionary<string, int?>(exitCodes, StringComparer.Ordinal));
    }

    private async Task<bool> TryAppendAsync(CheckRunRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await _store.AppendAsync(record, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.CanBeCanceled || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class RunState
    {
        public bool Error { get; set; }

        public bool Cancelled { get; set; }

        public bool TimedOut { get; set; }

        public string? ErrorReason { get; set; }

        public List<TestCounts?> TestCounts { get; } = [];

        public IReadOnlyList<FlagFinding> FlagDetails { get; set; } = [];
    }

    internal sealed record TestCounts(int Passed, int Failed, int Skipped);

    /// <summary>
    /// Reads strict test summaries from output: the VSTest line (<c>Failed: n, Passed: n, Skipped: n, Total: n</c>) and the
    /// Microsoft Testing Platform block (<c>total</c>, <c>failed</c>, <c>succeeded</c>, <c>skipped</c>). The counts must add up
    /// to the total, or they are treated as unreadable. Output is the agent's own process output, so this is a measurement of
    /// the tool's report, not a proof; a structured results file is the stronger source (OPEN).
    /// </summary>
    internal sealed partial class TestSummaryScanner
    {
        private readonly System.Text.StringBuilder _partial = new();
        private int _passed;
        private int _failed;
        private int _skipped;
        private int _total;
        private bool _any;
        private bool _inBlock;
        private int _blockTotal = -1;
        private int _blockFailed = -1;
        private int _blockPassed = -1;
        private int _blockSkipped = -1;

        public void Observe(OutputChunk chunk)
        {
            if (chunk.Stream != "stdout")
            {
                return;
            }

            _partial.Append(chunk.Text);
            string buffered = _partial.ToString();
            int newline;
            while ((newline = buffered.IndexOf('\n')) >= 0)
            {
                Line(buffered[..newline].TrimEnd('\r'));
                buffered = buffered[(newline + 1)..];
            }

            _partial.Clear().Append(buffered.Length > 4_096 ? string.Empty : buffered);
        }

        public TestCounts? Complete()
        {
            if (_partial.Length > 0)
            {
                Line(_partial.ToString().TrimEnd('\r'));
                _partial.Clear();
            }

            FlushBlock();
            return _any && _passed + _failed + _skipped == _total ? new TestCounts(_passed, _failed, _skipped) : null;
        }

        private void Line(string line)
        {
            var vs = VsTestLine().Match(line);
            if (vs.Success)
            {
                FlushBlock();
                Add(Num(vs.Groups[2].Value), Num(vs.Groups[1].Value), Num(vs.Groups[3].Value), Num(vs.Groups[4].Value));
                return;
            }

            if (line.StartsWith("Test run summary:", StringComparison.Ordinal))
            {
                FlushBlock();
                _inBlock = true;
                return;
            }

            if (!_inBlock)
            {
                return;
            }

            var m = BlockLine().Match(line);
            if (!m.Success)
            {
                FlushBlock();
                return;
            }

            var value = Num(m.Groups[2].Value);
            switch (m.Groups[1].Value)
            {
                case "total": _blockTotal = value; break;
                case "failed": _blockFailed = value; break;
                case "succeeded": _blockPassed = value; break;
                default: _blockSkipped = value; break;
            }
        }

        private static int Num(string digits) => int.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);

        private void FlushBlock()
        {
            if (_inBlock && _blockTotal >= 0 && _blockFailed >= 0 && _blockPassed >= 0 && _blockSkipped >= 0)
            {
                Add(_blockPassed, _blockFailed, _blockSkipped, _blockTotal);
            }

            _inBlock = false;
            _blockTotal = _blockFailed = _blockPassed = _blockSkipped = -1;
        }

        private void Add(int passed, int failed, int skipped, int total)
        {
            _any = true;
            _passed += passed;
            _failed += failed;
            _skipped += skipped;
            _total += total;
        }

        [GeneratedRegex(@"^(?:Passed|Failed)!\s+-\s+Failed:\s*(\d{1,9}),\s*Passed:\s*(\d{1,9}),\s*Skipped:\s*(\d{1,9}),\s*Total:\s*(\d{1,9})")]
        private static partial Regex VsTestLine();

        [GeneratedRegex(@"^\s+(total|failed|succeeded|skipped):\s*(\d{1,9})\s*$")]
        private static partial Regex BlockLine();
    }
}
