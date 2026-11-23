using Bazaar.Server.Identity;
using Bazaar.Server.Identity.Mount;

namespace Bazaar.Server.Tests.Identity.Mount;

public class MountAllowListTests
{
    public static TheoryData<CredentialKind> ForbiddenKinds()
    {
        var data = new TheoryData<CredentialKind>();
        foreach (var kind in Enum.GetValues<CredentialKind>())
        {
            if (kind != CredentialKind.Cookie)
            {
                data.Add(kind);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ForbiddenKinds))]
    public void Every_non_cookie_kind_is_refused(CredentialKind kind)
    {
        Assert.False(MountAllowList.IsPermitted(kind));
        Assert.Throws<ArgumentException>(() => MountAllowList.EnsureCredentialPermitted(kind));
    }

    [Theory]
    [MemberData(nameof(ForbiddenKinds))]
    public void A_cookie_profile_entry_carrying_a_forbidden_kind_is_refused(CredentialKind kind)
    {
        var entry = new MountEntry("profile.json", MountedFileKind.CookieProfile, kind, "{}"u8.ToArray());

        Assert.Throws<ArgumentException>(() => MountAllowList.EnsureEntryAllowed(entry));
    }

    [Fact]
    public void Cookie_is_the_only_permitted_kind()
    {
        Assert.True(MountAllowList.IsPermitted(CredentialKind.Cookie));
        Assert.Equal(1, Enum.GetValues<CredentialKind>().Count(MountAllowList.IsPermitted));
    }

    [Fact]
    public void A_cookie_profile_entry_with_cookie_kind_is_allowed()
    {
        var entry = new MountEntry("github-profile.json", MountedFileKind.CookieProfile, CredentialKind.Cookie, "{}"u8.ToArray());

        MountAllowList.EnsureEntryAllowed(entry);
    }

    [Fact]
    public void A_cookie_profile_entry_without_a_credential_kind_is_refused()
    {
        var entry = new MountEntry("profile.json", MountedFileKind.CookieProfile, null, "{}"u8.ToArray());

        Assert.Throws<ArgumentException>(() => MountAllowList.EnsureEntryAllowed(entry));
    }

    [Fact]
    public void Metadata_and_canary_entries_carry_no_credential_kind()
    {
        MountAllowList.EnsureEntryAllowed(new MountEntry("services.json", MountedFileKind.Metadata, null, "{}"u8.ToArray()));
        MountAllowList.EnsureEntryAllowed(new MountEntry("canary-1.session.json", MountedFileKind.Canary, null, "{}"u8.ToArray()));

        Assert.Throws<ArgumentException>(() =>
            MountAllowList.EnsureEntryAllowed(new MountEntry("services.json", MountedFileKind.Metadata, CredentialKind.Cookie, "{}"u8.ToArray())));
    }

    [Theory]
    [InlineData("api-key.json")]
    [InlineData("dek.bin")]
    [InlineData("passport.json")]
    [InlineData("audit.log")]
    [InlineData("TOTP-seed.txt")]
    [InlineData("recovery-codes.txt")]
    [InlineData("../escape.json")]
    [InlineData("nested/file.json")]
    [InlineData(".hidden")]
    public void Unsafe_or_forbidden_file_names_are_refused(string fileName)
    {
        var entry = new MountEntry(fileName, MountedFileKind.Metadata, null, "{}"u8.ToArray());

        Assert.Throws<ArgumentException>(() => MountAllowList.EnsureEntryAllowed(entry));
    }
}
