namespace Bazaar.Server.DevSandbox.Linux.Helper.Tests;

public sealed class LandlockRightsTests
{
    [Theory]
    [InlineData(1, 0x1FFFUL)]
    [InlineData(2, 0x3FFFUL)]
    [InlineData(3, 0x7FFFUL)]
    [InlineData(4, 0x7FFFUL)]
    [InlineData(5, 0xFFFFUL)]
    [InlineData(6, 0xFFFFUL)]
    [InlineData(7, 0xFFFFUL)]
    public void Handled_file_system_rights_degrade_with_the_abi(int abi, ulong expected)
    {
        Assert.Equal(expected, LandlockRights.HandledFs(abi));
    }

    [Fact]
    public void Abi_zero_or_below_handles_nothing()
    {
        Assert.Equal(0UL, LandlockRights.HandledFs(0));
        Assert.Equal(0UL, LandlockRights.HandledFs(-1));
        Assert.Equal(0UL, LandlockRights.HandledNet(3));
        Assert.Equal(0UL, LandlockRights.Scopes(5));
    }

    [Fact]
    public void Network_and_scope_rights_appear_at_abi_4_and_6()
    {
        Assert.Equal(0UL, LandlockRights.HandledNet(3));
        Assert.Equal(3UL, LandlockRights.HandledNet(4));
        Assert.Equal(0UL, LandlockRights.Scopes(5));
        Assert.Equal(3UL, LandlockRights.Scopes(6));
    }

    [Theory]
    [InlineData(1, 8u)]
    [InlineData(3, 8u)]
    [InlineData(4, 16u)]
    [InlineData(5, 16u)]
    [InlineData(6, 24u)]
    [InlineData(7, 24u)]
    public void The_ruleset_attribute_size_matches_the_abi(int abi, uint size)
    {
        Assert.Equal(size, LandlockRights.RulesetAttrSize(abi));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void Read_exec_never_grants_write_and_read_write_never_grants_execute(int abi)
    {
        var readExec = LandlockRights.ReadExec(abi);
        var readWrite = LandlockRights.ReadWrite(abi);

        Assert.Equal(LandlockRights.Execute | LandlockRights.ReadFile | LandlockRights.ReadDir, readExec);
        Assert.Equal(0UL, readExec & (LandlockRights.WriteFile | LandlockRights.MakeReg | LandlockRights.RemoveFile | LandlockRights.MakeDir));
        Assert.Equal(0UL, readWrite & LandlockRights.Execute);
        Assert.NotEqual(0UL, readWrite & LandlockRights.WriteFile);
        Assert.NotEqual(0UL, readWrite & LandlockRights.ReadFile);
        Assert.Equal(0UL, readExec & ~LandlockRights.HandledFs(abi));
        Assert.Equal(0UL, readWrite & ~LandlockRights.HandledFs(abi));
    }

    [Fact]
    public void Refer_and_truncate_are_granted_to_the_work_directory_only_on_abis_that_know_them()
    {
        Assert.Equal(0UL, LandlockRights.ReadWrite(1) & (LandlockRights.Refer | LandlockRights.Truncate));
        Assert.NotEqual(0UL, LandlockRights.ReadWrite(2) & LandlockRights.Refer);
        Assert.Equal(0UL, LandlockRights.ReadWrite(2) & LandlockRights.Truncate);
        Assert.NotEqual(0UL, LandlockRights.ReadWrite(3) & LandlockRights.Truncate);
        Assert.NotEqual(0UL, LandlockRights.ReadWrite(5) & LandlockRights.IoctlDev);
        Assert.Equal(0UL, LandlockRights.ReadWrite(4) & LandlockRights.IoctlDev);
    }

    [Fact]
    public void A_rule_on_a_file_is_limited_to_the_rights_the_kernel_accepts_for_files()
    {
        var onFile = LandlockRights.ForPath(LandlockRights.ReadExec(7), 7, isDirectory: false);
        var onDirectory = LandlockRights.ForPath(LandlockRights.ReadExec(7), 7, isDirectory: true);

        Assert.Equal(LandlockRights.Execute | LandlockRights.ReadFile, onFile);
        Assert.Equal(LandlockRights.Execute | LandlockRights.ReadFile | LandlockRights.ReadDir, onDirectory);
        Assert.Equal(0UL, LandlockRights.ForPath(LandlockRights.ReadWrite(7), 7, isDirectory: false) & (LandlockRights.MakeDir | LandlockRights.RemoveDir | LandlockRights.ReadDir));
    }

    [Fact]
    public void A_rule_never_names_a_right_the_ruleset_does_not_handle()
    {
        var all = ulong.MaxValue;
        Assert.Equal(LandlockRights.HandledFs(1), LandlockRights.ForPath(all, 1, isDirectory: true));
        Assert.Equal(LandlockRights.HandledFs(3), LandlockRights.ForPath(all, 3, isDirectory: true));
    }
}
