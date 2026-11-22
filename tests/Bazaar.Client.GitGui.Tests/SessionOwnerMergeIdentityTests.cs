using Bazaar.Client.GitGui.Merge;

namespace Bazaar.Client.GitGui.Tests;

public sealed class SessionOwnerMergeIdentityTests
{
    private sealed class Profile(string? line) : IMergeProfileStore
    {
        public string? GetSignOff(Guid humanId) => line;
    }

    private static readonly Guid _id = Guid.NewGuid();

    [Fact]
    public void Default_is_the_dco_line_from_the_principal()
    {
        var identity = SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, " Ada Lovelace ", "ada@example.org"), new UnsetMergeProfileStore());

        Assert.NotNull(identity);
        Assert.Equal(_id, identity.HumanId);
        Assert.Equal("Signed-off-by: Ada Lovelace <ada@example.org>", identity.SignOff);
    }

    [Fact]
    public void Profile_field_overrides_the_default()
    {
        var identity = SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, "Ada", "a@x.org"), new Profile("Signed-off-by: A. L. <al@y.org>"));

        Assert.Equal("Signed-off-by: A. L. <al@y.org>", identity!.SignOff);
    }

    [Theory]
    [InlineData("not a sign-off")]
    [InlineData("Signed-off-by: A <a@x.org>\nSigned-off-by: B <b@x.org>")]
    [InlineData("Signed-off-by: ")]
    public void Invalid_profile_value_falls_back_to_the_default(string line)
    {
        var identity = SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, "Ada", "a@x.org"), new Profile(line));

        Assert.Equal("Signed-off-by: Ada <a@x.org>", identity!.SignOff);
    }

    [Fact]
    public void Display_name_cannot_inject_lines_or_brackets()
    {
        var identity = SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, "Ada\nX <evil>", "a@x.org"));

        Assert.Equal("Signed-off-by: AdaX evil <a@x.org>", identity!.SignOff);
    }

    [Fact]
    public void No_principal_or_no_name_or_email_gives_no_identity()
    {
        Assert.Null(SessionOwnerMergeIdentity.TryCreate(null));
        Assert.Null(SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(Guid.Empty, "Ada", "a@x.org")));
        Assert.Null(SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, "", "a@x.org")));
        Assert.Null(SessionOwnerMergeIdentity.TryCreate(new MergePrincipal(_id, "Ada", " ")));
    }
}
