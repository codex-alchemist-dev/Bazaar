namespace Bazaar.ModulePermissions.Tests;

public class ModuleGrantTests
{
    private static ModuleGrant Parse(string text)
    {
        Assert.True(ModuleGrant.TryParse(text, out var permission), text);
        return permission;
    }

    [Theory]
    [InlineData("gc.post", "gc.post", null)]
    [InlineData("data.read:gc", "data.read", "gc")]
    [InlineData("fs:data/weather/*", "fs", "data/weather/*")]
    [InlineData("fs:C:/Data/x", "fs", "C:/Data/x")]
    [InlineData("network:api.example.com", "network", "api.example.com")]
    [InlineData("network:::1", "network", "::1")]
    [InlineData("secret:weather/api-key", "secret", "weather/api-key")]
    [InlineData("device:notify", "device", "notify")]
    public void Written_permissions_parse(string text, string @class, string? scope)
    {
        var permission = Parse(text);

        Assert.Equal(@class, permission.Class);
        Assert.Equal(scope, permission.Scope);
        Assert.Equal(text, permission.ToString());
    }

    [Theory]
    [InlineData("wire:gc.history", "data.read:gc")]
    [InlineData("wire:gc.post", "gc.post")]
    [InlineData("wire:kanban.read", "data.read:kanban")]
    [InlineData("wire:kanban.write", "data.write:kanban")]
    [InlineData("wire:vault.read", "data.read:vault")]
    [InlineData("wire:constellation.read", "data.read:constellation")]
    [InlineData("storage:local", "fs:local/*")]
    public void Client_capability_names_read_as_their_permission(string capability, string expected) =>
        Assert.Equal(expected, Parse(capability).ToString());

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("fs")]
    [InlineData("FS:data")]
    [InlineData("Data.Read:gc")]
    [InlineData("admin")]
    [InlineData("wire:gc.admin")]
    [InlineData("fs.dataRoot")]
    [InlineData("fs:")]
    [InlineData("fs:*")]
    [InlineData("fs:/*")]
    [InlineData("fs:a/../b")]
    [InlineData("fs:a/*/b")]
    [InlineData("fs:a//*")]
    [InlineData("secret:*")]
    [InlineData("network:*")]
    [InlineData("network:*.example.com")]
    [InlineData("network:example.com/*")]
    [InlineData("network:Example.com")]
    [InlineData("network:example.com:443")]
    [InlineData("network:0x7f000001")]
    [InlineData("data.read:gc\n")]
    [InlineData(" data.read:gc")]
    [InlineData("data.read:gc\u200B")]
    public void Anything_else_is_not_a_permission(string text) => Assert.False(ModuleGrant.TryParse(text, out _));

    [Fact]
    public void A_class_alone_is_a_permission_only_where_that_reads_sensibly()
    {
        // "fs" alone would otherwise look like "all of the file system" on the approval card.
        Assert.True(ModuleGrant.TryParse("gc.post", out _));
        Assert.False(ModuleGrant.TryParse("fs", out _));
        Assert.False(ModuleGrant.TryParse("network", out _));
        Assert.False(ModuleGrant.TryParse("secret", out _));
        Assert.False(ModuleGrant.TryParse("process", out _));
    }

    [Theory]
    [InlineData("gc.post", "gc.post", null, true)]
    [InlineData("gc.post", "gc.post", "general", false)]
    [InlineData("data.read:gc", "data.read", "gc", true)]
    [InlineData("data.read:gc", "data.write", "gc", false)]
    [InlineData("data.read:gc", "data.read", null, false)]
    [InlineData("data.read:gc", "data.read", "gc/private", false)]
    [InlineData("secret:weather/*", "secret", "weather/api-key", true)]
    [InlineData("secret:weather/*", "secret", "weather-admin/key", false)]
    [InlineData("secret:weather/*", "secret", "weather/../owner/key", false)]
    [InlineData("network:api.example.com", "network", "API.example.com", true)]
    [InlineData("network:api.example.com", "network", "api.example.com.", false)]
    [InlineData("network:api.example.com", "network", "evil.api.example.com", false)]
    [InlineData("network:api.example.com", "network", "api.example.com.evil.test", false)]
    [InlineData("network:example.com", "network", "example.com/x", false)]
    [InlineData("fs:example.com/*", "network", "example.com/x", false)]
    public void A_permission_covers_only_its_own_class_and_scope(string permission, string @class, string? target, bool expected) =>
        Assert.Equal(expected, Parse(permission).Covers(new ModuleAccess(@class, target)));

    [Theory]
    [InlineData("fs:a/b", "fs:a/b", true)]
    [InlineData("fs:a/b/c", "fs:a/b/*", true)]
    [InlineData("fs:a/b/c/*", "fs:a/b/*", true)]
    [InlineData("fs:a/b/*", "fs:a/b/*", true)]
    [InlineData("fs:a/b", "fs:a/b/*", false)]
    [InlineData("fs:a/*", "fs:a/b/*", false)]
    [InlineData("fs:a/bc/*", "fs:a/b/*", false)]
    [InlineData("fs:a/b/*", "fs:a/b/c", false)]
    [InlineData("fs:a/b", "secret:a/b", false)]
    [InlineData("gc.post", "gc.post", true)]
    [InlineData("network:a.example", "network:a.example", true)]
    [InlineData("network:b.a.example", "network:a.example", false)]
    public void Narrowing_is_told_from_widening(string candidate, string approved, bool expected) =>
        Assert.Equal(expected, Parse(candidate).IsWithin(Parse(approved)));
}
