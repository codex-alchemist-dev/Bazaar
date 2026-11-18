using Bazaar.Server.Hive.Abstractions;
using Bazaar.Testing;

namespace Bazaar.Server.Hive.Tests;

/// <summary>The grant policy and the copy flow against in-memory fakes (doc 25 Testing, Hive cases).</summary>
public sealed class HiveFlowTests
{
    private static readonly ProjectId _a = new("project-a");
    private static readonly ProjectId _b = new("project-b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly InMemoryGrantStore _grants = new();
    private readonly InMemoryCopyStore _copies = new();
    private readonly FakeSanitizer _sanitizer = new("acme", "sk-");
    private readonly FakeVaultWriter _writer = new();
    private readonly FakeAudit _audit = new();
    private readonly FakeSettings _settings = new(HiveOptions.Default);
    private readonly FakeClock _clock = new();

    private HiveCopyService Copies => new(_grants, _copies, _sanitizer, _writer, _audit, _settings, _clock);

    private HiveGrantService Grants => new(_grants, _copies, _writer, _audit, _clock);

    private HiveGrantPolicy Policy => new(_grants, _settings);

    private static LessonRef Lesson(
        string memory = "m1", string kind = HiveKinds.Procedure, HiveTrust trust = HiveTrust.T2, HiveSourceClass cls = HiveSourceClass.Lesson,
        bool subject = false, string title = "Retry flaky restores", string body = "Retry the restore twice before failing the build.") =>
        new(_a, memory, kind, cls, trust, "agent", subject, title, body);

    private async Task GrantAsync(string kind = HiveKinds.Procedure) => await Grants.GrantAsync(_a, _b, kind, "owner", Ct);

    [Fact]
    public async Task No_grant_means_no_copy()
    {
        Assert.False(await Policy.MayCopyAsync(Lesson(), _b, Ct));

        var result = await Copies.SubmitAsync(Lesson(), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.NoGrant, result.Outcome);
        Assert.Empty(await _copies.ListAsync(Ct));
        Assert.Empty(_writer.Files);
        Assert.Equal(0, _sanitizer.Calls);
    }

    [Fact]
    public async Task A_grant_is_per_kind()
    {
        await GrantAsync(HiveKinds.Procedure);

        Assert.True(await Policy.MayCopyAsync(Lesson(kind: HiveKinds.Procedure), _b, Ct));
        Assert.False(await Policy.MayCopyAsync(Lesson(kind: HiveKinds.Failure), _b, Ct));
        Assert.False(await Policy.MayCopyAsync(Lesson(), new ProjectId("project-c"), Ct));
    }

    [Theory]
    [InlineData(HiveTrust.T0)]
    [InlineData(HiveTrust.T1)]
    public async Task A_T0_or_T1_lesson_is_refused_even_with_a_grant(HiveTrust trust)
    {
        await GrantAsync();

        Assert.False(await Policy.MayCopyAsync(Lesson(trust: trust), _b, Ct));
        var result = await Copies.SubmitAsync(Lesson(trust: trust), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.Refused, result.Outcome);
        Assert.Equal(HiveRefusal.TrustTooLow, result.Refusal);
        Assert.Empty(await _copies.ListAsync(Ct));
    }

    [Theory]
    [InlineData(HiveSourceClass.VaultText)]
    [InlineData(HiveSourceClass.GcText)]
    [InlineData(HiveSourceClass.Preference)]
    [InlineData(HiveSourceClass.Reputation)]
    [InlineData(HiveSourceClass.PrivateMemory)]
    [InlineData(HiveSourceClass.Credential)]
    public async Task Excluded_classes_are_refused(HiveSourceClass cls)
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(cls: cls), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.Refused, result.Outcome);
        Assert.Equal(HiveRefusal.ExcludedClass, result.Refusal);
        Assert.False(await Policy.MayCopyAsync(Lesson(cls: cls), _b, Ct));
    }

    [Fact]
    public async Task A_subject_tagged_item_is_refused()
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(subject: true), _b, Ct);

        Assert.Equal(HiveRefusal.SubjectTagged, result.Refusal);
        Assert.Empty(await _copies.ListAsync(Ct));
    }

    [Fact]
    public async Task A_kind_outside_hive_allowed_kinds_is_refused_and_settings_cannot_widen_the_floor()
    {
        await GrantAsync(HiveKinds.Failure);
        _settings.Current = new HiveOptions(new HashSet<string> { HiveKinds.Procedure }, 30);
        Assert.Equal(HiveRefusal.KindNotAllowed, (await Copies.SubmitAsync(Lesson(kind: HiveKinds.Failure), _b, Ct)).Refusal);

        // A kind the setting adds but the floor does not know is still refused.
        _settings.Current = new HiveOptions(new HashSet<string> { "preference" }, 30);
        var odd = Lesson(kind: "preference");
        Assert.Equal(HiveRefusal.KindNotAllowed, (await Copies.SubmitAsync(odd, _b, Ct)).Refusal);
    }

    [Fact]
    public async Task The_sanitizer_rejection_stops_the_copy()
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(body: "At Acme we restore twice."), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.SanitizerRejected, result.Outcome);
        Assert.Contains("acme", result.Reason, StringComparison.Ordinal);
        Assert.Empty(await _copies.ListAsync(Ct));
        Assert.Empty(_writer.Files);
    }

    [Fact]
    public async Task An_accepted_lesson_is_held_and_not_in_the_vault_until_the_window_ends()
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.Held, result.Outcome);
        Assert.Equal(HiveCopyState.InReview, result.Copy!.State);
        Assert.Equal(_clock.GetUtcNow().AddDays(30), result.Copy.ReviewUntil);
        Assert.Empty(_writer.Files);

        _clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal(0, await Copies.ReleaseDueAsync(Ct));
        Assert.Empty(_writer.Files);

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await Copies.ReleaseDueAsync(Ct));

        var file = Assert.Single(_writer.Files);
        Assert.Equal(result.Copy.Path, file.Key);
        Assert.StartsWith("hive/procedure/retry-flaky-restores-", file.Key, StringComparison.Ordinal);
        Assert.EndsWith(".md", file.Key, StringComparison.Ordinal);
        Assert.Contains("origin_project: \"project-a\"", file.Value, StringComparison.Ordinal);
        Assert.Contains("origin_memory: \"m1\"", file.Value, StringComparison.Ordinal);
        Assert.Contains("trust: T2", file.Value, StringComparison.Ordinal);
        Assert.Contains("origin_kind: \"agent\"", file.Value, StringComparison.Ordinal);
        Assert.Contains("status: active", file.Value, StringComparison.Ordinal);
        Assert.Equal(HiveCopyState.Active, (await _copies.GetAsync(result.Copy.CopyId, Ct))!.State);
    }

    [Fact]
    public async Task Review_days_zero_keeps_every_batch_in_review()
    {
        await GrantAsync();
        _settings.Current = _settings.Current with { GrantReviewDays = 0 };

        var first = await Copies.SubmitAsync(Lesson("m1"), _b, Ct);
        var second = await Copies.SubmitAsync(Lesson("m2", title: "Pin the SDK"), _b, Ct);
        _clock.Advance(TimeSpan.FromDays(3650));

        Assert.Null(first.Copy!.ReviewUntil);
        Assert.Equal(0, await Copies.ReleaseDueAsync(Ct));
        Assert.Empty(_writer.Files);
        Assert.All(await _copies.ListAsync(Ct), c => Assert.Equal(HiveCopyState.InReview, c.State));

        // Only an owner approval releases one.
        Assert.True(await Copies.ApproveAsync(first.Copy.CopyId, "owner", Ct));
        Assert.Single(_writer.Files);
        Assert.Equal(HiveCopyState.InReview, (await _copies.GetAsync(second.Copy!.CopyId, Ct))!.State);
    }

    [Fact]
    public async Task Review_days_zero_stops_release_even_for_copies_held_with_a_window()
    {
        await GrantAsync();
        await Copies.SubmitAsync(Lesson(), _b, Ct);
        _clock.Advance(TimeSpan.FromDays(31));
        _settings.Current = _settings.Current with { GrantReviewDays = 0 };

        Assert.Equal(0, await Copies.ReleaseDueAsync(Ct));
        Assert.Empty(_writer.Files);
    }

    [Fact]
    public async Task A_copy_never_carries_more_than_T2()
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(trust: HiveTrust.T3), _b, Ct);

        Assert.Equal(HiveTrust.T2, result.Copy!.Trust);
    }

    [Fact]
    public async Task Revoking_a_grant_deprecates_active_copies_through_the_writer_and_never_deletes()
    {
        await GrantAsync();
        var held = await Copies.SubmitAsync(Lesson("m1"), _b, Ct);
        _clock.Advance(TimeSpan.FromDays(31));
        await Copies.ReleaseDueAsync(Ct);
        var inReview = await Copies.SubmitAsync(Lesson("m2", title: "Second"), _b, Ct);
        var otherKind = await Grants.GrantAsync(_a, _b, HiveKinds.Failure, "owner", Ct);
        Assert.True(otherKind);

        Assert.True(await Grants.RevokeAsync(_a, _b, HiveKinds.Procedure, "owner", Ct));

        var path = held.Copy!.Path;
        Assert.Contains("status: deprecated", _writer.Files[path], StringComparison.Ordinal);
        Assert.Contains($"set {path} status=deprecated", _writer.Operations);
        Assert.DoesNotContain(_writer.Operations, o => o.StartsWith("delete", StringComparison.Ordinal));
        Assert.Equal(HiveCopyState.Deprecated, (await _copies.GetAsync(held.Copy.CopyId, Ct))!.State);
        // The in-review copy was never in the Vault: it is deprecated in the store, with no front matter edit and no file.
        Assert.Equal(HiveCopyState.Deprecated, (await _copies.GetAsync(inReview.Copy!.CopyId, Ct))!.State);
        Assert.DoesNotContain(inReview.Copy.Path, _writer.Files.Keys);
        // The other kind's grant is untouched.
        Assert.True(await _grants.ExistsAsync(_a, _b, HiveKinds.Failure, Ct));
        Assert.False(await Policy.MayCopyAsync(Lesson("m3"), _b, Ct));
    }

    [Fact]
    public async Task Revoking_a_missing_grant_changes_nothing()
    {
        Assert.False(await Grants.RevokeAsync(_a, _b, HiveKinds.Procedure, "owner", Ct));
        Assert.Empty(_audit.Entries);
    }

    [Fact]
    public async Task A_grant_revoked_during_the_review_window_blocks_release()
    {
        await GrantAsync();
        var held = await Copies.SubmitAsync(Lesson(), _b, Ct);
        // Revoke the grant row only (as an out-of-band change would), so the copy is still in review when the window ends.
        await _grants.RemoveAsync(_a, _b, HiveKinds.Procedure, Ct);
        _clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(0, await Copies.ReleaseDueAsync(Ct));

        Assert.Empty(_writer.Files);
        Assert.Equal(HiveCopyState.Deprecated, (await _copies.GetAsync(held.Copy!.CopyId, Ct))!.State);
    }

    [Fact]
    public async Task Grant_changes_and_copy_events_are_audited()
    {
        await GrantAsync();
        await Grants.GrantAsync(_a, _b, HiveKinds.Procedure, "owner", Ct);
        await Copies.SubmitAsync(Lesson(), _b, Ct);
        await Grants.RevokeAsync(_a, _b, HiveKinds.Procedure, "owner", Ct);

        var kinds = _audit.Entries.Select(e => e.Kind).ToArray();
        Assert.Equal(
            ["hive.grant.added", "hive.copy.held", "hive.grant.revoked", "hive.copy.deprecated"],
            kinds);
        Assert.All(_audit.Entries.Where(e => e.Kind.StartsWith("hive.grant", StringComparison.Ordinal)), e => Assert.Equal("owner", e.Actor));
    }

    [Fact]
    public async Task The_same_lesson_is_not_copied_twice()
    {
        await GrantAsync();
        var first = await Copies.SubmitAsync(Lesson(), _b, Ct);

        var again = await Copies.SubmitAsync(Lesson(), _b, Ct);

        Assert.Equal(HiveSubmitOutcome.AlreadyCopied, again.Outcome);
        Assert.Equal(first.Copy!.CopyId, again.Copy!.CopyId);
    }

    [Fact]
    public async Task The_owner_can_reject_a_held_copy_and_it_never_reaches_the_vault()
    {
        await GrantAsync();
        var held = await Copies.SubmitAsync(Lesson(), _b, Ct);

        Assert.True(await Copies.RejectAsync(held.Copy!.CopyId, "owner", Ct));

        Assert.Empty(_writer.Files);
        Assert.Equal(HiveCopyState.Deprecated, (await _copies.GetAsync(held.Copy.CopyId, Ct))!.State);
        Assert.False(await Copies.ApproveAsync(held.Copy.CopyId, "owner", Ct));
    }

    [Fact]
    public async Task Granting_validates_its_input()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Grants.GrantAsync(_a, _a, HiveKinds.Procedure, "owner", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Grants.GrantAsync(_a, _b, "preference", "owner", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Grants.GrantAsync(_a, _b, HiveKinds.Procedure, " ", Ct));
    }

    [Fact]
    public async Task Text_is_checked_before_a_slug_or_path_is_chosen_and_a_hostile_title_stays_inside_hive()
    {
        await GrantAsync();

        var result = await Copies.SubmitAsync(Lesson(title: "../../agents/x/secret"), _b, Ct);

        Assert.StartsWith("hive/procedure/", result.Copy!.Path, StringComparison.Ordinal);
        Assert.DoesNotContain("..", result.Copy.Path, StringComparison.Ordinal);
    }
}
