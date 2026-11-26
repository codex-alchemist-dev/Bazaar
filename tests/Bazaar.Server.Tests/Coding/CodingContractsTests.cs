using System.Text.Json;
using Bazaar.Contracts.Coding;
using Bazaar.Server.Security.Policy;

namespace Bazaar.Server.Tests.Coding;

public class CodingContractsTests
{
    [Fact]
    public void ToolCallRecord_round_trips_through_json()
    {
        var record = new ToolCallRecord(
            Id: Guid.Parse("6f1c2a52-0d7e-4b8e-9a4f-1f2e3d4c5b6a"),
            SessionId: "session-1",
            Tool: "shell",
            CapabilityClass: nameof(CapabilityClass.Exec),
            SourceTrust: "agent",
            TaintedAtWrite: true,
            Status: ToolCallStatus.TimedOut,
            ExitCode: null,
            ArgsSha256: new string('a', 64),
            StartedAt: new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero),
            EndedAt: new DateTimeOffset(2026, 11, 1, 9, 15, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(record, CodingJson.Options);
        var back = JsonSerializer.Deserialize<ToolCallRecord>(json, CodingJson.Options);

        Assert.NotNull(back);
        Assert.Equal(record, back);
        Assert.Contains("\"timed_out\"", json);
    }

    [Fact]
    public void ToolCallRecord_with_null_end_round_trips()
    {
        var record = new ToolCallRecord(
            Guid.NewGuid(), "session-2", "edit", nameof(CapabilityClass.Read), "owner", false,
            ToolCallStatus.Refused, 2, new string('b', 64),
            DateTimeOffset.UnixEpoch, null);

        var json = JsonSerializer.Serialize(record, CodingJson.Options);
        var back = JsonSerializer.Deserialize<ToolCallRecord>(json, CodingJson.Options);

        Assert.Equal(record, back);
        Assert.Null(back!.EndedAt);
    }

    [Fact]
    public void CheckRunRecord_round_trips_through_json()
    {
        var record = new CheckRunRecord(
            Id: Guid.Parse("0b9e8d7c-6a5f-4e3d-8c2b-1a0f9e8d7c6b"),
            Commit: "abc123",
            BaseCommit: "def456",
            BuildExitCode: 0,
            TestsPassed: 40,
            TestsFailed: 1,
            TestsSkipped: 2,
            TestCountBase: 43,
            Flags: [CheckFlag.SkipAdded, CheckFlag.TestCountDropped],
            RanAt: new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero));

        var json = JsonSerializer.Serialize(record, CodingJson.Options);
        var back = JsonSerializer.Deserialize<CheckRunRecord>(json, CodingJson.Options);

        Assert.NotNull(back);
        Assert.Equal(record.Id, back.Id);
        Assert.Equal(record.Commit, back.Commit);
        Assert.Equal(record.BaseCommit, back.BaseCommit);
        Assert.Equal(record.BuildExitCode, back.BuildExitCode);
        Assert.Equal(record.TestsPassed, back.TestsPassed);
        Assert.Equal(record.TestsFailed, back.TestsFailed);
        Assert.Equal(record.TestsSkipped, back.TestsSkipped);
        Assert.Equal(record.TestCountBase, back.TestCountBase);
        Assert.Equal(record.Flags, back.Flags);
        Assert.Equal(record.RanAt, back.RanAt);
    }

    [Fact]
    public void CheckFlag_codes_are_stable_wire_values()
    {
        Assert.Equal("skip-added", CheckFlag.SkipAdded);
        Assert.Equal("if-false-added", CheckFlag.IfFalseAdded);
        Assert.Equal("test-removed", CheckFlag.TestRemoved);
        Assert.Equal("assertion-removed", CheckFlag.AssertionRemoved);
        Assert.Equal("test-file-changed", CheckFlag.TestFileChanged);
        Assert.Equal("test-count-dropped", CheckFlag.TestCountDropped);
    }

    [Fact]
    public void CapabilityClass_has_Exec_member()
    {
        Assert.True(Enum.IsDefined(CapabilityClass.Exec));
        Assert.Equal(CapabilityClass.Exec, Enum.Parse<CapabilityClass>("Exec"));
    }
}
