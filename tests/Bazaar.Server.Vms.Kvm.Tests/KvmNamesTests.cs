namespace Bazaar.Server.Vms.Kvm.Tests;

public sealed class KvmNamesTests
{
    [Fact]
    public void Domain_and_checkpoint_names_have_the_fixed_shape()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");

        Assert.Equal("bazaar-0123456789abcdef0123456789abcdef", KvmNames.Domain(id));
        Assert.Equal("bazaar-cp-0123456789abcdef0123456789abcdef", KvmNames.Checkpoint(id));
    }

    [Fact]
    public void Generated_names_pass_their_own_validators()
    {
        var id = Guid.NewGuid();

        Assert.True(KvmNames.IsDomainName(KvmNames.Domain(id)));
        Assert.True(KvmNames.IsCheckpointName(KvmNames.Checkpoint(id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bazaar-0123456789abcdef0123456789abcde")]
    [InlineData("bazaar-0123456789ABCDEF0123456789abcdef")]
    [InlineData("bazaar-cp-0123456789abcdef0123456789abcdef")]
    [InlineData("other-0123456789abcdef0123456789abcdef")]
    [InlineData("bazaar-0123456789abcdef0123456789abcdef; virsh destroy --all")]
    public void A_domain_name_outside_the_fixed_shape_is_refused(string name)
    {
        Assert.False(KvmNames.IsDomainName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bazaar-cp-0123456789abcdef0123456789abcde")]
    [InlineData("bazaar-0123456789abcdef0123456789abcdef")]
    [InlineData("bazaar-cp-0123456789ABCDEF0123456789abcdef")]
    public void A_checkpoint_name_outside_the_fixed_shape_is_refused(string name)
    {
        Assert.False(KvmNames.IsCheckpointName(name));
    }
}
