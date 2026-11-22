using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bazaar.Server.VmMounts.Abstractions;
using Bazaar.Server.VmMounts.Providers.Smb;

namespace Bazaar.Server.VmMounts.Providers.Tests;

public sealed class SmbScriptTests
{
    private static readonly Guid _agent = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid _mount = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string _account = @"HOST\bazaar-vm1";
    private const string _path = @"D:\Bazaar\agents\x\workspace";

    [Fact]
    public void The_share_name_follows_the_fixed_pattern()
    {
        Assert.Equal("bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee", SmbShareNames.For(_agent, MountKind.Soul, _mount));
        Assert.Equal("bazaar-111111112222-work-aaaaaaaabbbbccccddddeeeeeeeeeeee", SmbShareNames.For(_agent, MountKind.Workspace, _mount));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bazaar-")]
    [InlineData("Bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee\n")]
    [InlineData("bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee; Remove-Item C:\\")]
    [InlineData("bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeeX")]
    [InlineData("bazaar-111111112222-other-aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("bazaar-1111-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee")]
    [InlineData("backup")]
    [InlineData("C$")]
    [InlineData("bazaar-111111112222-soul-aaaaaaaabbbbccccddddeeeeeeeeeeee$")]
    public void Names_outside_the_pattern_are_rejected(string name)
    {
        Assert.False(SmbShareNames.IsValidShare(name));
        Assert.Throws<ArgumentException>(() => SmbScripts.BuildRemove(name));
    }

    [Theory]
    [InlineData(@"HOST\user", true)]
    [InlineData(@"user", false)]
    [InlineData(@"HOST\user" + "\n", false)]
    [InlineData(@"HOST\us er", false)]
    [InlineData(@"HOST\user'; calc", false)]
    [InlineData(@"HOST\user`n", false)]
    [InlineData(@"HOST\", false)]
    [InlineData(@"\user", false)]
    [InlineData(@"Everyone", false)]
    [InlineData(@"HOST\this-name-is-way-longer-than-twenty", false)]
    public void Account_names_are_checked(string account, bool valid) => Assert.Equal(valid, SmbShareNames.IsValidAccount(account));

    [Fact]
    public void The_attach_script_is_pinned_by_hash()
    {
        // Any edit to the script text must be deliberate: update this hash together with the doc 16 note when it changes.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(SmbScripts.Attach)));
        Assert.Equal("05ae4ab1992be2357cc0e73a33914de01d9d7e10c6945b59a63d0e80e8cd615f", hash);
    }

    [Fact]
    public void The_remove_and_list_scripts_are_exactly_the_constants()
    {
        Assert.Equal(
            "$ErrorActionPreference = 'Stop'\n"
            + "Get-SmbShare | ForEach-Object { $_.Name } | Where-Object { $_.StartsWith('bazaar-', [StringComparison]::OrdinalIgnoreCase) }",
            SmbScripts.ListPrefixed);

        var lines = SmbScripts.Remove.Split('\n');
        Assert.Equal("$ErrorActionPreference = 'Stop'", lines[0]);
        Assert.Equal("$p = [Console]::In.ReadLine() | ConvertFrom-Json", lines[1]);
        Assert.Contains(lines, l => l.Contains("Revoke-SmbShareAccess", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Remove-SmbShare -Name $p.Name -Force", StringComparison.Ordinal));
        Assert.Equal("Write-Output 'ok'", lines[^1]);
    }

    [Fact]
    public void The_attach_script_uses_the_share_cmdlets_and_a_single_grant()
    {
        var script = SmbScripts.Attach;
        foreach (var cmdlet in new[] { "New-SmbShare", "Grant-SmbShareAccess", "Revoke-SmbShareAccess", "Remove-SmbShare", "Get-SmbShareAccess", "RequireSecuritySignature" })
            Assert.Contains(cmdlet, script, StringComparison.Ordinal);

        Assert.Contains("EncryptData = $true", script, StringComparison.Ordinal);
        Assert.Contains("FolderEnumerationMode = 'AccessBased'", script, StringComparison.Ordinal);
        // No wide grant: never Full, never Everyone.
        Assert.DoesNotContain("FullAccess", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Everyone", script, StringComparison.Ordinal);
        // Parameters come from stdin; nothing is interpolated.
        Assert.Contains("[Console]::In.ReadLine() | ConvertFrom-Json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Parameters_travel_as_one_json_stdin_line_and_the_script_is_unchanged()
    {
        var name = SmbShareNames.For(_agent, MountKind.Workspace, _mount);
        var invocation = SmbScripts.BuildAttach(name, _path, _account, readOnly: false, "bazaar mount test");

        Assert.Same(SmbScripts.Attach, invocation.Script);
        Assert.EndsWith("\n", invocation.StandardInput, StringComparison.Ordinal);
        Assert.Single(invocation.StandardInput!.TrimEnd('\n').Split('\n'));
        using var json = JsonDocument.Parse(invocation.StandardInput);
        Assert.Equal(name, json.RootElement.GetProperty("Name").GetString());
        Assert.Equal(_path, json.RootElement.GetProperty("Path").GetString());
        Assert.Equal(_account, json.RootElement.GetProperty("Account").GetString());
        Assert.Equal("Change", json.RootElement.GetProperty("Right").GetString());

        // The encoded command decodes back to exactly the constant.
        Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], invocation.Arguments.Take(3));
        Assert.Equal(SmbScripts.Attach, Encoding.Unicode.GetString(Convert.FromBase64String(invocation.Arguments[3])));
        Assert.DoesNotContain(_path, invocation.Script, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_maps_to_Read_and_read_write_to_Change_never_Full()
    {
        var name = SmbShareNames.For(_agent, MountKind.Soul, _mount);
        using var ro = JsonDocument.Parse(SmbScripts.BuildAttach(name, _path, _account, readOnly: true, "d").StandardInput!);
        using var rw = JsonDocument.Parse(SmbScripts.BuildAttach(name, _path, _account, readOnly: false, "d").StandardInput!);
        Assert.Equal("Read", ro.RootElement.GetProperty("Right").GetString());
        Assert.Equal("Change", rw.RootElement.GetProperty("Right").GetString());
    }

    [Theory]
    [InlineData("D:\\a'b")]
    [InlineData("D:\\a\"b")]
    [InlineData("D:\\a`nb")]
    [InlineData("D:\\a$(calc)")]
    [InlineData("D:\\a;b")]
    [InlineData("D:\\a|b")]
    [InlineData("D:\\a*")]
    [InlineData("D:\\a?")]
    [InlineData("D:\\a%PATH%")]
    [InlineData("D:\\a\r\nb")]
    [InlineData("D:\\a\u00e9")]
    [InlineData("D:\\..\\b")]
    [InlineData("D:\\a\\.\\b")]
    [InlineData("relative\\path")]
    [InlineData("\\\\server\\share")]
    [InlineData("\\\\?\\D:\\a")]
    [InlineData("/etc")]
    [InlineData("")]
    public void Hostile_paths_never_reach_a_script(string path)
    {
        var name = SmbShareNames.For(_agent, MountKind.Soul, _mount);
        Assert.Throws<ArgumentException>(() => SmbScripts.BuildAttach(name, path, _account, true, "d"));
    }

    [Fact]
    public void A_hostile_account_or_description_never_reaches_a_script()
    {
        var name = SmbShareNames.For(_agent, MountKind.Soul, _mount);
        Assert.Throws<ArgumentException>(() => SmbScripts.BuildAttach(name, _path, "Everyone", true, "d"));
        Assert.Throws<ArgumentException>(() => SmbScripts.BuildAttach(name, _path, _account, true, "d'; calc"));
        Assert.Throws<ArgumentException>(() => SmbScripts.BuildAttach(name, _path, _account, true, "line\nbreak"));
    }
}
