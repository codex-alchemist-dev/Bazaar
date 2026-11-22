using Bazaar.Contracts.Coding;
using Bazaar.Server.Luau.Abstractions;

namespace Bazaar.Server.Luau.Tests;

/// <summary>Maps checker results to check_runs rows: verdicts fail closed, output is bounded, ordering is deterministic.</summary>
public class LuauCheckRunMapperTests
{
    private static readonly Guid _id = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset _ranAt = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly LuauToolKind[] _all = [LuauToolKind.Analyze, LuauToolKind.Selene, LuauToolKind.StyLuaCheck];

    private static LuauToolReport Passed(LuauToolKind tool) => new(tool, LuauToolStatus.Passed, 0, null, false);

    private static LuauToolReport Findings(LuauToolKind tool, int exit = 1, bool truncated = false) =>
        new(tool, LuauToolStatus.Findings, exit, null, truncated);

    private static LuauDiagnostic Diag(
        LuauToolKind tool,
        LuauSeverity severity,
        string message,
        string file = "scripts/Main.lua",
        int line = 3,
        int column = 1,
        string code = "rule") =>
        new(tool, file, line, column, severity, code, message);

    private static LuauCheckRunMapping Map(LuauCheckResult result, params LuauToolKind[] requested) =>
        LuauCheckRunMapper.ToCheckRun(result, requested, _id, "abc123", "def456", _ranAt);

    [Fact]
    public void All_tools_clean_passes_with_exit_codes_and_no_reasons()
    {
        var result = new LuauCheckResult([], [Passed(LuauToolKind.Analyze), Passed(LuauToolKind.Selene), Passed(LuauToolKind.StyLuaCheck)]);

        LuauCheckRunMapping mapping = Map(result, _all);

        Assert.True(mapping.Passed);
        Assert.Empty(mapping.Reasons);
        Assert.Equal(
            [
                "luau-analyze: Passed, exit 0, 0 error(s), 0 warning(s)",
                "selene: Passed, exit 0, 0 error(s), 0 warning(s)",
                "stylua --check: Passed, exit 0, 0 error(s), 0 warning(s)",
            ],
            mapping.Summary);
    }

    [Fact]
    public void Maps_only_the_fields_the_check_runs_row_has()
    {
        var result = new LuauCheckResult([], [Passed(LuauToolKind.Analyze)]);

        CheckRunRecord record = Map(result, LuauToolKind.Analyze).Record;

        Assert.Equal(_id, record.Id);
        Assert.Equal("abc123", record.Commit);
        Assert.Equal("def456", record.BaseCommit);
        Assert.Null(record.BuildExitCode);
        Assert.Null(record.TestsPassed);
        Assert.Null(record.TestsFailed);
        Assert.Null(record.TestsSkipped);
        Assert.Null(record.TestCountBase);
        Assert.Empty(record.Flags);
        Assert.Equal(_ranAt, record.RanAt);
    }

    [Fact]
    public void Warnings_only_passes_and_counts_them()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Analyze, LuauSeverity.Warning, "unused local")],
            [Findings(LuauToolKind.Analyze), Passed(LuauToolKind.Selene), Passed(LuauToolKind.StyLuaCheck)]);

        LuauCheckRunMapping mapping = Map(result, _all);

        Assert.True(mapping.Passed);
        Assert.Empty(mapping.Reasons);
        Assert.Equal("luau-analyze: Findings, exit 1, 0 error(s), 1 warning(s)", mapping.Summary[0]);
        Assert.Equal("luau-analyze scripts/Main.lua:3:1 Warning rule: unused local", mapping.Summary[3]);
    }

    [Fact]
    public void Info_only_diagnostics_pass()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Selene, LuauSeverity.Info, "note")],
            [Passed(LuauToolKind.Analyze), Findings(LuauToolKind.Selene, 0), Passed(LuauToolKind.StyLuaCheck)]);

        Assert.True(Map(result, _all).Passed);
    }

    [Fact]
    public void Error_diagnostic_fails_and_names_the_count()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Analyze, LuauSeverity.Error, "type mismatch"), Diag(LuauToolKind.Analyze, LuauSeverity.Error, "unknown global")],
            [Findings(LuauToolKind.Analyze), Passed(LuauToolKind.Selene), Passed(LuauToolKind.StyLuaCheck)]);

        LuauCheckRunMapping mapping = Map(result, _all);

        Assert.False(mapping.Passed);
        Assert.Equal(["luau-analyze: 2 error diagnostic(s)"], mapping.Reasons);
        Assert.Equal("luau-analyze: Findings, exit 1, 2 error(s), 0 warning(s)", mapping.Summary[0]);
    }

    [Fact]
    public void Unavailable_tool_fails_closed_with_its_reason()
    {
        var result = new LuauCheckResult(
            [],
            [
                Passed(LuauToolKind.Analyze),
                new LuauToolReport(LuauToolKind.Selene, LuauToolStatus.Unavailable, null, "Could not start 'selene': not found", false),
                Passed(LuauToolKind.StyLuaCheck),
            ]);

        LuauCheckRunMapping mapping = Map(result, _all);

        Assert.False(mapping.Passed);
        Assert.Equal(["selene: unavailable: Could not start 'selene': not found"], mapping.Reasons);
        Assert.Equal("selene: Unavailable, exit none, 0 error(s), 0 warning(s)", mapping.Summary[1]);
    }

    [Fact]
    public void Timed_out_tool_fails_closed_even_with_no_diagnostics()
    {
        var result = new LuauCheckResult(
            [],
            [
                new LuauToolReport(LuauToolKind.Analyze, LuauToolStatus.TimedOut, null, "luau-analyze timed out after 00:02:00.", false),
                Passed(LuauToolKind.Selene),
                Passed(LuauToolKind.StyLuaCheck),
            ]);

        LuauCheckRunMapping mapping = Map(result, _all);

        Assert.False(mapping.Passed);
        Assert.Equal(["luau-analyze: timed out: luau-analyze timed out after 00:02:00."], mapping.Reasons);
        Assert.Equal("luau-analyze: TimedOut, exit none, 0 error(s), 0 warning(s)", mapping.Summary[0]);
    }

    [Fact]
    public void Tool_error_status_fails_closed()
    {
        var result = new LuauCheckResult(
            [],
            [new LuauToolReport(LuauToolKind.StyLuaCheck, LuauToolStatus.Error, 2, "stylua --check bad config", false)]);

        LuauCheckRunMapping mapping = Map(result, LuauToolKind.StyLuaCheck);

        Assert.False(mapping.Passed);
        Assert.Equal(["stylua --check: failed: stylua --check bad config"], mapping.Reasons);
        Assert.Equal("stylua --check: Error, exit 2, 0 error(s), 0 warning(s)", mapping.Summary[0]);
    }

    [Fact]
    public void Requested_tool_with_no_report_fails_closed()
    {
        var result = new LuauCheckResult([], [Passed(LuauToolKind.Analyze)]);

        LuauCheckRunMapping mapping = Map(result, LuauToolKind.Analyze, LuauToolKind.Selene);

        Assert.False(mapping.Passed);
        Assert.Equal(["selene: did not run"], mapping.Reasons);
        Assert.Equal("selene: not run", mapping.Summary[1]);
    }

    [Fact]
    public void Empty_request_means_every_tool()
    {
        var result = new LuauCheckResult([], [Passed(LuauToolKind.Analyze), Passed(LuauToolKind.Selene)]);

        LuauCheckRunMapping mapping = LuauCheckRunMapper.ToCheckRun(result, [], _id, "abc123", null, _ranAt);

        Assert.False(mapping.Passed);
        Assert.Equal(["stylua --check: did not run"], mapping.Reasons);
        Assert.Equal(3, mapping.Summary.Count);
        Assert.Null(mapping.Record.BaseCommit);
    }

    [Fact]
    public void Diagnostics_of_unrequested_tools_are_left_out()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Selene, LuauSeverity.Error, "not asked for")],
            [Passed(LuauToolKind.Analyze)]);

        LuauCheckRunMapping mapping = Map(result, LuauToolKind.Analyze);

        Assert.True(mapping.Passed);
        Assert.Single(mapping.Summary);
    }

    [Fact]
    public void Truncated_findings_without_errors_fail_closed()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Selene, LuauSeverity.Warning, "style")],
            [new LuauToolReport(LuauToolKind.Selene, LuauToolStatus.Findings, 1, null, OutputTruncated: true)]);

        LuauCheckRunMapping mapping = Map(result, LuauToolKind.Selene);

        Assert.False(mapping.Passed);
        Assert.Equal(["selene: output truncated, so diagnostics may be incomplete"], mapping.Reasons);
        Assert.Equal("selene: Findings, exit 1, 0 error(s), 1 warning(s), output truncated", mapping.Summary[0]);
    }

    [Fact]
    public void Truncated_clean_run_still_passes()
    {
        var result = new LuauCheckResult([], [new LuauToolReport(LuauToolKind.Analyze, LuauToolStatus.Passed, 0, null, true)]);

        Assert.True(Map(result, LuauToolKind.Analyze).Passed);
    }

    [Fact]
    public void Long_message_is_cut_to_the_line_cap_and_marked()
    {
        string huge = new('x', 5000);
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Analyze, LuauSeverity.Warning, huge)],
            [Findings(LuauToolKind.Analyze)]);

        string line = Map(result, LuauToolKind.Analyze).Summary[1];

        Assert.True(line.Length <= LuauCheckRunMapper.MaxLineCharacters);
        Assert.EndsWith("...", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_reason_is_cut_to_the_line_cap()
    {
        string reason = new('r', 5000);
        var result = new LuauCheckResult(
            [],
            [new LuauToolReport(LuauToolKind.Selene, LuauToolStatus.Error, 3, reason, false)]);

        string failure = Map(result, LuauToolKind.Selene).Reasons[0];

        Assert.True(failure.Length <= LuauCheckRunMapper.MaxLineCharacters);
        Assert.EndsWith("...", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_in_untrusted_text_are_replaced()
    {
        var result = new LuauCheckResult(
            [Diag(LuauToolKind.Analyze, LuauSeverity.Warning, "fake\nline\u001b[31m red")],
            [Findings(LuauToolKind.Analyze)]);

        string line = Map(result, LuauToolKind.Analyze).Summary[1];

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\u001b', line);
        Assert.EndsWith("fake line [31m red", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Diagnostic_lines_are_capped_with_a_count_line()
    {
        var diagnostics = Enumerable.Range(1, 30)
            .Select(i => Diag(LuauToolKind.Analyze, LuauSeverity.Warning, $"warning {i}", line: i))
            .ToArray();
        var result = new LuauCheckResult(diagnostics, [Findings(LuauToolKind.Analyze)]);

        IReadOnlyList<string> summary = Map(result, LuauToolKind.Analyze).Summary;

        Assert.Equal(1 + LuauCheckRunMapper.MaxDiagnosticLines + 1, summary.Count);
        Assert.Equal("... 10 more diagnostic(s) not shown", summary[^1]);
    }

    [Fact]
    public void Output_is_deterministic_whatever_order_the_diagnostics_arrive_in()
    {
        var forward = new List<LuauDiagnostic>
        {
            Diag(LuauToolKind.Selene, LuauSeverity.Warning, "b", file: "b.lua", line: 2),
            Diag(LuauToolKind.Analyze, LuauSeverity.Warning, "z", file: "a.lua", line: 9),
            Diag(LuauToolKind.Analyze, LuauSeverity.Error, "e", file: "c.lua", line: 1),
            Diag(LuauToolKind.Analyze, LuauSeverity.Warning, "a", file: "a.lua", line: 9),
        };
        var reversed = forward.AsEnumerable().Reverse().ToArray();
        LuauToolReport[] reports = [Findings(LuauToolKind.Analyze), Findings(LuauToolKind.Selene), Passed(LuauToolKind.StyLuaCheck)];

        LuauCheckRunMapping first = Map(new LuauCheckResult(forward, reports), _all);
        LuauCheckRunMapping second = Map(new LuauCheckResult(reversed, reports), _all);
        LuauCheckRunMapping third = Map(new LuauCheckResult(forward, reports), _all);

        Assert.Equal(first.Summary, second.Summary);
        Assert.Equal(first.Summary, third.Summary);
        Assert.Equal(first.Reasons, second.Reasons);
        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.Record.Commit, second.Record.Commit);
        Assert.Equal(first.Record.RanAt, second.Record.RanAt);
        Assert.Equal("luau-analyze c.lua:1:1 Error rule: e", first.Summary[3]);
        Assert.Equal("luau-analyze a.lua:9:1 Warning rule: a", first.Summary[4]);
    }

    [Fact]
    public void Null_arguments_and_blank_commit_are_rejected()
    {
        var result = new LuauCheckResult([], []);

        Assert.Throws<ArgumentNullException>(() => LuauCheckRunMapper.ToCheckRun(null!, [], _id, "abc", null, _ranAt));
        Assert.Throws<ArgumentNullException>(() => LuauCheckRunMapper.ToCheckRun(result, null!, _id, "abc", null, _ranAt));
        Assert.Throws<ArgumentException>(() => LuauCheckRunMapper.ToCheckRun(result, [], _id, " ", null, _ranAt));
    }
}
