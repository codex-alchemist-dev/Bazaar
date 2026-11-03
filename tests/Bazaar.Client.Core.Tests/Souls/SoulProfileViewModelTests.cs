using Bazaar.Client.Core.Souls;
using Bazaar.Contracts.Soul;

namespace Bazaar.Client.Core.Tests.Souls;

public class SoulProfileViewModelTests
{
    private static readonly Guid _soul = Guid.Parse("0196a6f0-0000-7000-8000-000000000101");
    private static readonly Guid _otherSoul = Guid.Parse("0196a6f0-0000-7000-8000-000000000102");
    private static readonly string _hash = new('a', 64);
    private static readonly SoulProfile _profile = new(_soul, "Charles", "Architect", "Haiku 5.5");

    private static SoulVersion Version(
        int version,
        int? parent,
        string author = "dom",
        string reason = "reason",
        string? decision = null,
        string? contentSha = null,
        string preferences = "{}",
        Guid? soul = null) => new(
            soul ?? _soul,
            version,
            parent,
            new string('c', 64),
            preferences,
            contentSha ?? _hash,
            author,
            reason,
            decision,
            new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.Zero).AddHours(version));

    [Fact]
    public void History_runs_oldest_to_newest_even_when_given_out_of_order()
    {
        var model = new SoulProfileViewModel(_profile, [Version(3, 2), Version(1, null), Version(2, 1)]);

        Assert.Equal([1, 2, 3], model.History.Select(row => row.Version));
    }

    [Fact]
    public void Head_is_the_highest_version_and_its_hash_is_shortened()
    {
        var head = Version(2, 1, contentSha: "0123456789abcdef" + new string('0', 48));
        var model = new SoulProfileViewModel(_profile, [Version(1, null), head]);

        Assert.Equal(2, model.CurrentVersion);
        Assert.Equal("v2", model.VersionHeadline);
        Assert.Equal("0123456789ab", model.ContentHashShort);
    }

    [Fact]
    public void Edited_marker_is_off_for_version_one_and_on_after_an_edit()
    {
        var fresh = new SoulProfileViewModel(_profile, [Version(1, null)]);
        var edited = new SoulProfileViewModel(_profile, [Version(1, null), Version(2, 1)]);

        Assert.False(fresh.IsEdited);
        Assert.True(edited.IsEdited);
    }

    [Fact]
    public void Profile_fields_pass_through_unchanged()
    {
        var model = new SoulProfileViewModel(_profile, []);

        Assert.Equal(_soul, model.SoulId);
        Assert.Equal("Charles", model.Name);
        Assert.Equal("Architect", model.Role);
        Assert.Equal("Haiku 5.5", model.ModelBadge);
    }

    [Fact]
    public void Soul_without_versions_shows_no_version_and_no_history()
    {
        var model = new SoulProfileViewModel(_profile, []);

        Assert.Null(model.CurrentVersion);
        Assert.Null(model.ContentHashShort);
        Assert.False(model.IsEdited);
        Assert.Equal("no version", model.VersionHeadline);
        Assert.Empty(model.History);
        Assert.False(model.HasHistory);
        Assert.Equal(PreferencesSummary.NoneRecorded, model.PreferencesSummary);
    }

    [Fact]
    public void Rows_show_parent_decision_and_utc_time()
    {
        var model = new SoulProfileViewModel(_profile,
        [
            Version(1, null, author: "dom", reason: "hired", decision: null),
            Version(2, 1, author: "council", reason: "tone change", decision: "decisions/0007-tone.md"),
        ]);

        var first = model.History[0];
        Assert.Equal("v1", first.VersionLabel);
        Assert.Equal("none", first.ParentLabel);
        Assert.Equal("none", first.DecisionLabel);
        Assert.Equal("2026-10-25 06:00 UTC", first.CreatedLabel);

        var second = model.History[1];
        Assert.Equal("v2", second.VersionLabel);
        Assert.Equal("v1", second.ParentLabel);
        Assert.Equal("council", second.Author);
        Assert.Equal("tone change", second.Reason);
        Assert.Equal("decisions/0007-tone.md", second.DecisionLabel);
    }

    [Fact]
    public void Preferences_summary_comes_from_the_head_version()
    {
        var model = new SoulProfileViewModel(_profile,
        [
            Version(1, null, preferences: "{\"tone\":\"dry\"}"),
            Version(2, 1, preferences: "{\"tone\":\"warm\"}"),
        ]);

        Assert.Equal("tone: warm", model.PreferencesSummary);
    }

    [Fact]
    public void Version_from_another_soul_is_refused()
    {
        var versions = new[] { Version(1, null), Version(1, null, soul: _otherSoul) };

        Assert.Throws<ArgumentException>(() => new SoulProfileViewModel(_profile, versions));
    }

    [Fact]
    public void Repeated_version_number_is_refused()
    {
        var versions = new[] { Version(1, null), Version(1, null) };

        Assert.Throws<ArgumentException>(() => new SoulProfileViewModel(_profile, versions));
    }

    [Fact]
    public void Version_below_one_is_refused()
    {
        var versions = new[] { Version(0, null) };

        Assert.Throws<ArgumentException>(() => new SoulProfileViewModel(_profile, versions));
    }

    [Fact]
    public void Null_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new SoulProfileViewModel(null!, []));
        Assert.Throws<ArgumentNullException>(() => new SoulProfileViewModel(_profile, null!));
    }

    [Fact]
    public async Task History_fed_from_a_source_builds_the_same_card()
    {
        var source = new FakeHistorySource(_soul, [Version(2, 1), Version(1, null)]);

        var versions = await source.GetHistoryAsync(_soul, CancellationToken.None);
        var model = new SoulProfileViewModel(_profile, versions);

        Assert.Equal([1, 2], model.History.Select(row => row.Version));
        Assert.Equal(1, source.Calls);
    }

    private sealed class FakeHistorySource(Guid soulId, IReadOnlyList<SoulVersion> versions) : IVersionHistorySource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<SoulVersion>> GetHistoryAsync(Guid requested, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<SoulVersion>>(requested == soulId ? versions : []);
        }
    }
}
