using Bazaar.Diagnostics.Fingerprinting;
using Bazaar.Diagnostics.Grouping;

namespace Bazaar.Diagnostics.Tests.Grouping;

public class GroupTransitionTests
{
    private static readonly DateTimeOffset _now = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
    private static readonly FilingPolicy _budget = new(10, 0);
    private static readonly FilingPolicy _noBudget = new(10, 10);

    private static FingerprintInput Input() => new(
        "BZ-NET-0001",
        "System.InvalidOperationException",
        [new FingerprintFrame("Bazaar.Client.Core", "Bazaar.Client.Core.Session", "Connect", true)],
        null);

    private static Occurrence Seen(string version = "0.1.0+a1b2c3d", string? hint = null, string device = "desktop", string os = "windows") =>
        new(Input(), version, device, os, hint);

    private static GroupUpdate First(FilingPolicy? policy = null) => GroupTransition.Apply(null, Seen(), policy ?? _budget, _now);

    [Fact]
    public void New_group_files_an_issue_when_budget_allows()
    {
        var update = First();

        Assert.Equal(1, update.State.OccurrenceCount);
        Assert.Equal(GroupStatus.Open, update.State.Status);
        var file = Assert.IsType<FileIssue>(Assert.Single(update.Actions));
        Assert.Equal(update.State.Fingerprint, file.Fingerprint);
        Assert.Equal(Fingerprint.Compute(Input()), update.State.Fingerprint);
    }

    [Fact]
    public void New_group_is_deferred_when_budget_is_used_up()
    {
        var update = First(_noBudget);

        Assert.Equal(GroupStatus.FilingDeferred, update.State.Status);
        Assert.IsType<QueueDeferred>(Assert.Single(update.Actions));
    }

    [Fact]
    public void Deferred_group_files_once_budget_returns()
    {
        var deferred = First(_noBudget).State;

        var update = GroupTransition.Apply(deferred, Seen(), _budget, _now.AddDays(1));

        Assert.Equal(2, update.State.OccurrenceCount);
        Assert.Equal(GroupStatus.Open, update.State.Status);
        Assert.IsType<FileIssue>(Assert.Single(update.Actions));
    }

    [Fact]
    public void Second_occurrence_with_an_issue_adds_no_comment()
    {
        var state = First().State.WithIssueNumber(7);

        var update = GroupTransition.Apply(state, Seen(), _budget, _now.AddMinutes(1));

        Assert.Equal(2, update.State.OccurrenceCount);
        Assert.Empty(update.Actions);
    }

    [Fact]
    public void Comment_is_added_at_the_tenth_occurrence_and_carries_no_stack()
    {
        var state = First().State.WithIssueNumber(7) with { OccurrenceCount = 9 };

        var update = GroupTransition.Apply(state, Seen(), _budget, _now.AddDays(1));

        var comment = Assert.IsType<AddComment>(Assert.Single(update.Actions));
        Assert.Equal(10, comment.OccurrenceCount);
        Assert.Contains("Seen 10 times", comment.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Connect", comment.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void At_most_one_comment_per_group_per_day()
    {
        var state = First().State.WithIssueNumber(7) with { OccurrenceCount = 9, LastCommentDate = DateOnly.FromDateTime(_now.UtcDateTime) };

        var update = GroupTransition.Apply(state, Seen(), _budget, _now.AddHours(1));

        Assert.Empty(update.Actions);
    }

    [Fact]
    public void Regression_at_or_above_the_fixed_version_reopens_the_issue()
    {
        var state = First().State.WithIssueNumber(7).WithIssueClosed("0.2.0");

        var update = GroupTransition.Apply(state, Seen("0.2.0+deadbee"), _budget, _now.AddDays(1));

        var reopen = Assert.IsType<Reopen>(Assert.Single(update.Actions));
        Assert.Equal("0.2.0", reopen.FixedInVersion);
        Assert.Equal("0.2.0+deadbee", reopen.OccurrenceVersion);
        Assert.Contains("0.2.0", reopen.Text, StringComparison.Ordinal);
        Assert.Equal(GroupStatus.ResolvedIn, update.State.Status);
        Assert.False(update.State.IssueClosed);
    }

    [Fact]
    public void Older_version_only_increases_the_count()
    {
        var state = First().State.WithIssueNumber(7).WithIssueClosed("0.2.0");

        var update = GroupTransition.Apply(state, Seen("0.1.5"), _budget, _now.AddDays(1));

        Assert.Empty(update.Actions);
        Assert.True(update.State.IssueClosed);
        Assert.Equal(2, update.State.OccurrenceCount);
    }

    [Fact]
    public void Issue_closed_without_a_fixed_version_is_never_reopened()
    {
        var state = First().State.WithIssueNumber(7).WithIssueClosed(null);

        var update = GroupTransition.Apply(state, Seen("9.9.9"), _budget, _now.AddDays(1));

        Assert.Empty(update.Actions);
    }

    [Fact]
    public void Deleted_issue_is_filed_again()
    {
        var state = First().State.WithIssueNumber(7).WithIssueDeleted();

        var update = GroupTransition.Apply(state, Seen(), _budget, _now.AddDays(1));

        Assert.IsType<FileIssue>(Assert.Single(update.Actions));
    }

    [Fact]
    public void Version_range_follows_semver_precedence_not_arrival_order()
    {
        var state = First().State;
        state = GroupTransition.Apply(state, Seen("0.10.0"), _budget, _now).State;
        state = GroupTransition.Apply(state, Seen("0.9.0"), _budget, _now).State;

        Assert.Equal("0.1.0+a1b2c3d", state.FirstSeenVersion);
        Assert.Equal("0.10.0", state.LastSeenVersion);
    }

    [Fact]
    public void Device_classes_and_os_families_are_aggregated()
    {
        var state = First().State;
        state = GroupTransition.Apply(state, Seen(device: "phone", os: "android"), _budget, _now).State;

        Assert.Equal(["desktop", "phone"], state.DeviceClasses);
        Assert.Equal(["android", "windows"], state.OsFamilies);
    }

    [Fact]
    public void Mismatched_device_hint_is_recorded_and_the_server_value_is_used()
    {
        var update = GroupTransition.Apply(null, Seen(hint: "fp1:00000000000000000000000000000000"), _budget, _now);

        var mismatch = Assert.IsType<RecordMismatch>(update.Actions[0]);
        Assert.Equal(update.State.Fingerprint, mismatch.ServerFingerprint);
        Assert.IsType<FileIssue>(update.Actions[1]);
    }

    [Fact]
    public void Matching_device_hint_records_nothing()
    {
        var update = GroupTransition.Apply(null, Seen(hint: Fingerprint.Compute(Input())), _budget, _now);

        Assert.DoesNotContain(update.Actions, action => action is RecordMismatch);
    }

    [Fact]
    public void Stored_group_for_another_fingerprint_is_refused()
    {
        var other = GroupState.Create("fp1:ffffffffffffffffffffffffffffffff", "0.1.0", _now);

        Assert.Throws<ArgumentException>(() => GroupTransition.Apply(other, Seen(), _budget, _now));
    }
}
