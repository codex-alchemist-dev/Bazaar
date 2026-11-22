using Bazaar.Roles.Abstractions;
using Bazaar.Server.WebAdmin.Security;

namespace Bazaar.Server.WebAdmin.Tests.Security;

public sealed class SessionStoreTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid _alice = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid _dev1 = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    private static AdminIdentity Identity(Guid? device = null) => new(_alice, PrincipalKind.Human, device ?? _dev1);

    private static readonly Guid _devX = Guid.Parse("00000000-0000-0000-0000-0000000000d2");

    private sealed class FakeRevocation : IAdminRevocationCheck
    {
        public HashSet<Guid> RevokedDevices { get; } = [];

        public Task<bool> IsRevokedAsync(Guid principalId, Guid deviceId, CancellationToken ct = default) =>
            Task.FromResult(RevokedDevices.Contains(deviceId));
    }

    [Fact]
    public async Task Create_then_validate_returns_session()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var created = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        var s = await store.ValidateAsync(created.CookieValue, _t0.AddMinutes(1), TestContext.Current.CancellationToken);
        Assert.NotNull(s);
        Assert.Equal(_alice, s.PrincipalId);
        Assert.Equal(PrincipalKind.Human, s.Kind);
        Assert.Equal(_dev1, s.DeviceId);
    }

    [Fact]
    public async Task Only_hash_is_stored_as_id()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var created = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.NotEqual(created.CookieValue, created.Session.Id);
        Assert.Equal(64, created.Session.Id.Length);
        Assert.All(created.Session.Id, c => Assert.True(Uri.IsHexDigit(c)));
        Assert.DoesNotContain(created.CookieValue, created.Session.Id, StringComparison.Ordinal);
        // The id is not accepted as a cookie.
        Assert.Null(await store.ValidateAsync(created.Session.Id, _t0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cookie_is_43_char_base64url_and_unique()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var a = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        var b = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.Equal(43, a.CookieValue.Length);
        Assert.All(a.CookieValue, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(a.CookieValue, b.CookieValue);
    }

    [Fact]
    public async Task Unknown_or_empty_cookie_is_null()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.Null(await store.ValidateAsync("nope", _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.ValidateAsync("", _t0, TestContext.Current.CancellationToken));
        Assert.Null(await store.ValidateAsync(new string('a', 5000), _t0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sliding_expiry_extends_with_activity_and_then_lapses()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var c = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        var s1 = await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(10), TestContext.Current.CancellationToken);
        Assert.NotNull(s1);
        Assert.Equal(_t0.AddMinutes(10), s1.LastSeen);
        // 10 + 14 = 24 minutes after start: still alive because the slide moved.
        Assert.NotNull(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(24), TestContext.Current.CancellationToken));
        // Idle for the full sliding window: gone, and stays gone.
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(39), TestContext.Current.CancellationToken));
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(39), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Absolute_expiry_ends_session_despite_activity()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var c = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.Equal(_t0 + AdminSessionOptions.MaxAbsolute, c.Session.AbsoluteExpiry);
        var now = _t0;
        while (now + TimeSpan.FromMinutes(10) < _t0 + AdminSessionOptions.MaxAbsolute)
        {
            now += TimeSpan.FromMinutes(10);
            Assert.NotNull(await store.ValidateAsync(c.CookieValue, now, TestContext.Current.CancellationToken));
        }

        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0 + AdminSessionOptions.MaxAbsolute, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Loose_options_are_clamped_to_floors()
    {
        var loose = new AdminSessionOptions(TimeSpan.FromDays(1), TimeSpan.FromDays(30));
        var store = new InMemoryAdminSessionStore(loose, new NoRevocation());
        var c = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.Equal(_t0 + AdminSessionOptions.MaxAbsolute, c.Session.AbsoluteExpiry);
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0 + AdminSessionOptions.MaxSliding + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Tighter_options_are_honoured()
    {
        var tight = new AdminSessionOptions(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
        var store = new InMemoryAdminSessionStore(tight, new NoRevocation());
        var c = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        Assert.Equal(_t0.AddMinutes(5), c.Session.AbsoluteExpiry);
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revoked_device_invalidates_on_next_validate()
    {
        var rev = new FakeRevocation();
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, rev);
        var c = await store.CreateAsync(Identity(_devX), _t0, TestContext.Current.CancellationToken);
        Assert.NotNull(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(1), TestContext.Current.CancellationToken));
        rev.RevokedDevices.Add(_devX);
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(2), TestContext.Current.CancellationToken));
        rev.RevokedDevices.Clear();
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0.AddMinutes(3), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Revoke_removes_session()
    {
        var store = new InMemoryAdminSessionStore(AdminSessionOptions.Default, new NoRevocation());
        var c = await store.CreateAsync(Identity(), _t0, TestContext.Current.CancellationToken);
        await store.RevokeAsync(c.Session.Id, TestContext.Current.CancellationToken);
        Assert.Null(await store.ValidateAsync(c.CookieValue, _t0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoRevocation_never_revokes()
    {
        Assert.False(await new NoRevocation().IsRevokedAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken));
    }
}
