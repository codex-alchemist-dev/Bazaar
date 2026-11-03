using Bazaar.Server.Council.Eligibility;
using static Bazaar.Server.Tests.Council.Eligibility.EligibilityTestData;

namespace Bazaar.Server.Tests.Council.Eligibility;

public class VoterEligibilityTests
{
    private const string _proposer = "proposer";
    private const string _author = "author";
    private const string _subject = "subject";
    private const string _owner = "owner";

    private static GcMember Agent(string id) => new(id, IsOwner: false);

    private static GcMember[] Members(params GcMember[] members) => members;

    [Fact]
    public void Proposer_is_always_excluded()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent("x1"), Agent("x2")),
            _proposer, null, null, CouncilEligibilityOptions.Defaults);

        Assert.Equal(["x1", "x2"], Sorted(sets.Voters));
    }

    [Fact]
    public void Subject_author_is_excluded_by_default()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent(_author), Agent("x1")),
            _proposer, _author, null, CouncilEligibilityOptions.Defaults);

        Assert.Equal(["x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Subject_author_votes_when_exclude_author_is_disabled()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent(_author), Agent("x1")),
            _proposer, _author, null, new CouncilEligibilityOptions(ExcludeAuthor: false));

        Assert.Equal([_author, "x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Membership_subject_is_excluded_by_default()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent(_subject), Agent("x1")),
            _proposer, null, _subject, CouncilEligibilityOptions.Defaults);

        Assert.Equal(["x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Membership_subject_votes_when_exclude_membership_subject_is_disabled()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent(_subject), Agent("x1")),
            _proposer, null, _subject, new CouncilEligibilityOptions(ExcludeMembershipSubject: false));

        Assert.Equal([_subject, "x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Owner_does_not_vote_by_default()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), new GcMember(_owner, IsOwner: true), Agent("x1")),
            _proposer, null, null, CouncilEligibilityOptions.Defaults);

        Assert.Equal(["x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Owner_votes_when_owner_votes_is_set()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), new GcMember(_owner, IsOwner: true), Agent("x1")),
            _proposer, null, null, new CouncilEligibilityOptions(OwnerVotes: true));

        Assert.Equal([_owner, "x1"], Sorted(sets.Voters));
    }

    [Fact]
    public void Unanimity_over_all_gc_members_is_a_different_set_from_eligible_after_exclusions()
    {
        var members = Members(
            Agent(_proposer), Agent(_author), Agent(_subject), new GcMember(_owner, IsOwner: true), Agent("x1"));

        var eligible = VoterEligibility.Resolve(
            members, _proposer, _author, _subject,
            new CouncilEligibilityOptions(UnanimityElectorate: UnanimityElectorate.EligibleAfterExclusions));
        var all = VoterEligibility.Resolve(
            members, _proposer, _author, _subject,
            new CouncilEligibilityOptions(UnanimityElectorate: UnanimityElectorate.AllGcMembers));

        Assert.Equal(["x1"], Sorted(eligible.UnanimityElectorate));
        Assert.Equal([_author, _owner, _proposer, _subject, "x1"], Sorted(all.UnanimityElectorate));
        Assert.Equal(Sorted(eligible.Voters), Sorted(eligible.UnanimityElectorate));
        Assert.Equal(["x1"], Sorted(all.Voters.Intersect(all.UnanimityElectorate)));
    }

    [Fact]
    public void Unanimity_set_is_a_separate_instance_from_the_voter_set()
    {
        var sets = VoterEligibility.Resolve(
            Members(Agent(_proposer), Agent("x1")), _proposer, null, null, CouncilEligibilityOptions.Defaults);

        Assert.NotSame(sets.Voters, sets.UnanimityElectorate);
    }

    [Fact]
    public void Duplicate_member_ids_are_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            VoterEligibility.Resolve(Members(Agent("x1"), Agent("x1")), _proposer, null, null, CouncilEligibilityOptions.Defaults));
    }
}
