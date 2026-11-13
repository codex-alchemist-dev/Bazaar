using Bazaar.Server.Gc.Create;
using Npgsql;

namespace Bazaar.Server.Tests.Gc.Create;

/// <summary>
/// The GC create service's rules and its deterministic id (doc 38, WP-R7). The rules run before any connection is opened, so
/// these tests need no database: the data source is never used.
/// </summary>
public sealed class GcCreateServiceTests
{
    private static readonly Guid _creator = Guid.Parse("aaaaaaaa-aaaa-7aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid _memberA = Guid.Parse("bbbbbbbb-bbbb-7bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid _memberB = Guid.Parse("cccccccc-cccc-7ccc-8ccc-cccccccccccc");
    private static readonly Guid _gc = Guid.Parse("dddddddd-dddd-7ddd-8ddd-dddddddddddd");

    private static NpgsqlDataSource Unused() => NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused");

    [Fact]
    public void A_deterministic_id_is_stable_for_a_seed_and_differs_between_seeds()
    {
        Assert.Equal(GcCreateService.DeterministicGcId("seed-1"), GcCreateService.DeterministicGcId("seed-1"));
        Assert.NotEqual(GcCreateService.DeterministicGcId("seed-1"), GcCreateService.DeterministicGcId("seed-2"));
        Assert.NotEqual(Guid.Empty, GcCreateService.DeterministicGcId("seed-1"));
    }

    [Fact]
    public async Task A_blank_title_is_refused()
    {
        var service = new GcCreateService(Unused());
        await Assert.ThrowsAsync<GcCreateRefusedException>(() =>
            service.EnsureAsync(new GcCreateRequest(_gc, "   ", _creator, [_memberA]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_request_with_no_members_is_refused()
    {
        var service = new GcCreateService(Unused());
        await Assert.ThrowsAsync<GcCreateRefusedException>(() =>
            service.EnsureAsync(new GcCreateRequest(_gc, "title", _creator, []), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_duplicate_member_is_refused()
    {
        var service = new GcCreateService(Unused());
        await Assert.ThrowsAsync<GcCreateRefusedException>(() =>
            service.EnsureAsync(new GcCreateRequest(_gc, "title", _creator, [_memberA, _memberA]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task More_members_than_the_cap_are_refused()
    {
        var service = new GcCreateService(Unused(), maxMembers: 2);
        await Assert.ThrowsAsync<GcCreateRefusedException>(() =>
            service.EnsureAsync(new GcCreateRequest(_gc, "title", _creator, [_creator, _memberA, _memberB]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_default_member_cap_is_the_ping_fanout_cap_plus_the_creator()
    {
        Assert.Equal(9, new GcCreateService(Unused()).MaxMembers);
        Assert.Equal(GcCreateService.DefaultMaxMembers, new GcCreateService(Unused()).MaxMembers);
    }

    [Fact]
    public void A_zero_member_cap_is_a_configuration_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GcCreateService(Unused(), maxMembers: 0));
    }
}
