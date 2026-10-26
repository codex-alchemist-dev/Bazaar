using Bazaar.Contracts.Gc;
using Bazaar.Server.Gc.Members;
using Bazaar.Server.Gc.Model;

namespace Bazaar.Server.Tests.Gc.Members;

public class GcAccessPolicyTests
{
    /// <summary>The viewer relations the matrix covers.</summary>
    public enum Relation
    {
        Owner,
        ActiveMember,
        Observer,
        FormerMember,
        Outsider,
        OwningAgent,
        OtherAgent,
    }

    private const long _leftSeq = 10;

    private static Guid OwnerId => Id(1);
    private static Guid MemberA => Id(2);
    private static Guid MemberB => Id(3);
    private static Guid Observer => Id(4);
    private static Guid Former => Id(5);
    private static Guid Outsider => Id(6);
    private static Guid OwningAgent => Id(7);
    private static Guid OtherAgent => Id(8);
    private static Guid GcId => Id(100);
    private static Guid ParentId => Id(200);

    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    private static GcRecord Gc(string typeKey, bool archived) =>
        new(GcId, typeKey == GcTypeKeys.Thinking ? ParentId : null, typeKey, archived ? DateTimeOffset.UnixEpoch : null);

    private static GcMemberRecord Row(Guid principal, PrincipalKind kind, GcMemberRole role, long joined, long? left) =>
        new(GcId, principal, kind, role, joined, left);

    /// <summary>Membership fixture per type. Thinking GCs hold only the owning agent as a member row.</summary>
    private static IReadOnlyCollection<GcMemberRecord> Members(string typeKey) => typeKey switch
    {
        GcTypeKeys.Thinking =>
        [
            Row(OwningAgent, PrincipalKind.Agent, GcMemberRole.Member, 0, null),
            Row(MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null),
            Row(Former, PrincipalKind.Human, GcMemberRole.Member, 1, _leftSeq),
        ],
        GcTypeKeys.Dm =>
        [
            Row(MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null),
            Row(MemberB, PrincipalKind.Human, GcMemberRole.Member, 1, null),
            Row(Former, PrincipalKind.Human, GcMemberRole.Member, 1, _leftSeq),
        ],
        _ =>
        [
            Row(MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null),
            Row(Observer, PrincipalKind.Human, GcMemberRole.Observer, 1, null),
            Row(Former, PrincipalKind.Human, GcMemberRole.Member, 1, _leftSeq),
        ],
    };

    private static GcViewer Viewer(Relation relation) => relation switch
    {
        Relation.Owner => new GcViewer(OwnerId, PrincipalKind.Owner),
        Relation.ActiveMember => new GcViewer(MemberA, PrincipalKind.Human),
        Relation.Observer => new GcViewer(Observer, PrincipalKind.Human),
        Relation.FormerMember => new GcViewer(Former, PrincipalKind.Human),
        Relation.Outsider => new GcViewer(Outsider, PrincipalKind.Human),
        Relation.OwningAgent => new GcViewer(OwningAgent, PrincipalKind.Agent),
        _ => new GcViewer(OtherAgent, PrincipalKind.Agent),
    };

    public static TheoryData<string, Relation, bool, bool, long?, bool> MatrixRows()
    {
        const long all = GcAccessPolicy.AllEvents;
        var rows = new TheoryData<string, Relation, bool, bool, long?, bool>
        {
            // type, viewer, archived, approved, expected read, expected write
            { "general", Relation.Owner, false, false, all, true },
            { "general", Relation.ActiveMember, false, false, all, true },
            { "general", Relation.Observer, false, false, all, false },
            { "general", Relation.FormerMember, false, false, _leftSeq, false },
            { "general", Relation.Outsider, false, false, null, false },
            { "general", Relation.Outsider, false, true, null, false },
            { "general", Relation.Owner, true, false, all, false },
            { "general", Relation.ActiveMember, true, false, all, false },
            { "general", Relation.FormerMember, true, false, _leftSeq, false },
            { "general", Relation.Observer, true, false, all, false },
            { "dm", Relation.Owner, false, false, all, true },
            { "dm", Relation.ActiveMember, false, false, all, true },
            { "dm", Relation.FormerMember, false, false, _leftSeq, false },
            { "dm", Relation.Outsider, false, false, null, false },
            { "dm", Relation.Outsider, false, true, all, false },
            { "dm", Relation.ActiveMember, true, false, all, false },
            { "dm", Relation.Outsider, true, true, all, false },
            { "dm", Relation.Owner, true, false, all, false },
            { "thinking", Relation.Owner, false, false, all, true },
            { "thinking", Relation.OwningAgent, false, false, all, true },
            { "thinking", Relation.OwningAgent, true, false, all, false },
            { "thinking", Relation.OtherAgent, false, false, null, false },
            { "thinking", Relation.OtherAgent, false, true, all, false },
            { "thinking", Relation.ActiveMember, false, false, null, false },
            { "thinking", Relation.ActiveMember, false, true, all, false },
            { "thinking", Relation.FormerMember, false, false, null, false },
            { "thinking", Relation.Owner, true, false, all, false },
            { "thinking", Relation.Outsider, false, true, all, false },
        };

        return rows;
    }

    [Theory]
    [MemberData(nameof(MatrixRows))]
    public void Access_matrix_matches_spec(string typeKey, Relation relation, bool archived, bool approved, long? expectedRead, bool expectedWrite)
    {
        var gc = Gc(typeKey, archived);
        var members = Members(typeKey);
        var viewer = Viewer(relation);
        IReadOnlySet<Guid> approvedSet = approved ? new HashSet<Guid> { viewer.PrincipalId } : new HashSet<Guid>();

        Assert.Equal(expectedRead, GcAccessPolicy.ReadableUpToSeq(gc, members, viewer, approvedSet));
        Assert.Equal(expectedWrite, GcAccessPolicy.CanWrite(gc, members, viewer));
    }

    [Fact]
    public void Former_member_cannot_read_the_event_after_LeftSeq()
    {
        var gc = Gc(GcTypeKeys.Dm, archived: false);
        var members = Members(GcTypeKeys.Dm);
        var viewer = Viewer(Relation.FormerMember);

        var readable = GcAccessPolicy.ReadableUpToSeq(gc, members, viewer, new HashSet<Guid>());

        Assert.Equal(_leftSeq, readable);
        Assert.False(readable is { } upTo && upTo >= _leftSeq + 1);
    }

    [Fact]
    public void Archived_dm_keeps_the_former_member_bound()
    {
        var gc = Gc(GcTypeKeys.Dm, archived: true);
        var members = Members(GcTypeKeys.Dm);

        Assert.Equal(_leftSeq, GcAccessPolicy.ReadableUpToSeq(gc, members, Viewer(Relation.FormerMember), new HashSet<Guid>()));
        Assert.False(GcAccessPolicy.CanWrite(gc, members, Viewer(Relation.FormerMember)));
    }

    [Fact]
    public void Latest_row_wins_when_a_principal_rejoins()
    {
        var gc = Gc(GcTypeKeys.General, archived: false);
        var members = new List<GcMemberRecord>
        {
            Row(Former, PrincipalKind.Human, GcMemberRole.Member, 1, _leftSeq),
            Row(Former, PrincipalKind.Human, GcMemberRole.Member, 12, null),
        };

        Assert.Equal(GcAccessPolicy.AllEvents, GcAccessPolicy.ReadableUpToSeq(gc, members, Viewer(Relation.FormerMember), new HashSet<Guid>()));
    }

    [Fact]
    public void Rows_of_other_GCs_are_ignored()
    {
        var gc = Gc(GcTypeKeys.General, archived: false);
        var otherGc = new GcMemberRecord(Id(999), MemberA, PrincipalKind.Human, GcMemberRole.Member, 1, null);

        Assert.Null(GcAccessPolicy.ReadableUpToSeq(gc, [otherGc], Viewer(Relation.ActiveMember), new HashSet<Guid>()));
        Assert.False(GcAccessPolicy.CanWrite(gc, [otherGc], Viewer(Relation.ActiveMember)));
    }
}
