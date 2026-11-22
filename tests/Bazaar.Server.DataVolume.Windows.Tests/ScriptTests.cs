using System.Security.Cryptography;
using System.Text;

namespace Bazaar.Server.DataVolume.Windows.Tests;

/// <summary>The pure script builders and the real seams over a scripted command runner.</summary>
public sealed class ScriptTests
{
    private const string _guid = FakeWorld.Guid;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Diskpart_create_script_is_exact()
    {
        var script = DiskpartScripts.CreateVdisk(@"D:\bazaar\data.vhdx", 4L * 1024 * 1024 * 1024);

        Assert.Equal("create vdisk file=\"D:\\bazaar\\data.vhdx\" maximum=4096 type=expandable\nexit\n", script);
    }

    [Fact]
    public void Diskpart_attach_and_detach_scripts_are_exact()
    {
        Assert.Equal("select vdisk file=\"D:\\bazaar\\data.vhdx\"\nattach vdisk\nexit\n", DiskpartScripts.Attach(@"D:\bazaar\data.vhdx"));
        Assert.Equal("select vdisk file=\"D:\\bazaar\\data.vhdx\"\ndetach vdisk\nexit\n", DiskpartScripts.Detach(@"D:\bazaar\data.vhdx"));
    }

    [Fact]
    public void Diskpart_initialise_script_is_exact_and_trims_the_trailing_backslash()
    {
        var script = DiskpartScripts.InitialiseAndMount(@"D:\bazaar\data.vhdx", @"D:\bazaar\data\");

        Assert.Equal(
            "select vdisk file=\"D:\\bazaar\\data.vhdx\"\nconvert gpt\ncreate partition primary\nformat fs=ntfs quick label=BazaarData\nassign mount=\"D:\\bazaar\\data\"\nexit\n",
            script);
    }

    [Fact]
    public void Diskpart_create_refuses_a_size_below_the_minimum()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskpartScripts.CreateVdisk(@"D:\a.vhdx", 1024));
    }

    [Theory]
    [InlineData("D:\\a\"b\\x.vhdx")]
    [InlineData("D:\\a\nb\\x.vhdx")]
    [InlineData("D:\\a\rb\\x.vhdx")]
    [InlineData("D:\\a\0b\\x.vhdx")]
    [InlineData("D:\\a'b\\x.vhdx")]
    [InlineData("D:\\a&b\\x.vhdx")]
    [InlineData("D:\\a\u00e9\\x.vhdx")]
    [InlineData("x.vhdx")]
    [InlineData("\\\\server\\share\\x.vhdx")]
    [InlineData("D:\\a\\..\\x.vhdx")]
    [InlineData("D:\\a\\x.vhd")]
    [InlineData("")]
    public void Container_paths_that_could_inject_or_are_not_absolute_vhdx_files_are_refused(string path)
    {
        Assert.Throws<ArgumentException>(() => DiskpartScripts.Attach(path));
        Assert.Throws<ArgumentException>(() => DiskpartScripts.Detach(path));
        Assert.Throws<ArgumentException>(() => DiskpartScripts.CreateVdisk(path, 1L << 30));
        Assert.Throws<ArgumentException>(() => DiskImageScripts.IsAttached(path));
    }

    [Theory]
    [InlineData("D:\\a\"b")]
    [InlineData("D:\\a\nassign letter=Z")]
    [InlineData("D:\\a\0")]
    [InlineData("data")]
    public void Mount_folders_that_could_inject_are_refused(string folder)
    {
        Assert.Throws<ArgumentException>(() => DiskpartScripts.InitialiseAndMount(@"D:\a.vhdx", folder));
    }

    [Fact]
    public void Bitlocker_script_text_is_exact_for_a_status_call()
    {
        var invocation = BitLockerScripts.GetLockStatus(_guid);

        var expected = string.Join(
            "\n",
            "$ErrorActionPreference = 'Stop'",
            $"$id = '\\\\?\\Volume{{{_guid}}}\\'",
            "$v = Get-CimInstance -Namespace 'root/CIMV2/Security/MicrosoftVolumeEncryption' -ClassName Win32_EncryptableVolume | Where-Object { $_.DeviceID -ieq $id }",
            "if ($null -eq $v) { throw 'volume not found' }",
            "$r = Invoke-CimMethod -InputObject $v -MethodName 'GetLockStatus' -Arguments @{}",
            "$r | Select-Object ReturnValue,LockStatus | ConvertTo-Json -Compress",
            string.Empty);
        Assert.Equal(expected, invocation.Script);
        Assert.Null(invocation.StandardInput);
    }

    [Fact]
    public void Bitlocker_encrypt_and_lock_scripts_carry_their_parameters()
    {
        Assert.Contains("-MethodName 'Encrypt' -Arguments @{ EncryptionMethod = 7; EncryptionFlags = 1 }", BitLockerScripts.Encrypt(_guid, 7, 1).Script, StringComparison.Ordinal);
        Assert.Contains("-MethodName 'Lock' -Arguments @{ ForceDismount = $false }", BitLockerScripts.Lock(_guid, forceDismount: false).Script, StringComparison.Ordinal);
        Assert.Contains("ForceDismount = $true", BitLockerScripts.Lock(_guid, forceDismount: true).Script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70'; Remove-Item C:\\ #")]
    [InlineData("{0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70}")]
    [InlineData("0B6F2A1E-5C3D-4E7A-9B1F-3A2C4D5E6F70")]
    [InlineData("")]
    public void Bitlocker_scripts_take_only_a_canonicalGuid(string volumeId)
    {
        Assert.Throws<ArgumentException>(() => BitLockerScripts.GetLockStatus(volumeId));
    }

    [Fact]
    public void The_key_is_only_ever_on_stdin()
    {
        var key = RandomKey();
        var base64 = Convert.ToBase64String(key);
        var hex = Convert.ToHexString(key);

        foreach (var invocation in new[] { BitLockerScripts.ProtectWithExternalKey(_guid, key), BitLockerScripts.UnlockWithExternalKey(_guid, key) })
        {
            Assert.Equal(base64 + "\n", invocation.StandardInput);
            Assert.Contains("[Console]::In.ReadLine()", invocation.Script, StringComparison.Ordinal);
            Assert.DoesNotContain(base64, invocation.Script, StringComparison.Ordinal);
            Assert.DoesNotContain(hex, invocation.Script, StringComparison.OrdinalIgnoreCase);

            var command = invocation.ToCommand();
            Assert.Equal(SystemTools.PowerShell, command.Tool);
            Assert.Equal(base64 + "\n", command.StandardInput);
            foreach (var argument in command.Arguments)
            {
                Assert.DoesNotContain(base64, argument, StringComparison.Ordinal);
                Assert.DoesNotContain(hex, argument, StringComparison.OrdinalIgnoreCase);
            }

            // The encoded command decodes back to exactly the script.
            Assert.Equal(["-NoProfile", "-NonInteractive", "-EncodedCommand"], command.Arguments.Take(3));
            Assert.Equal(invocation.Script, Encoding.Unicode.GetString(Convert.FromBase64String(command.Arguments[3])));
        }
    }

    [Fact]
    public async Task Bitlocker_seam_sends_the_key_on_stdin_and_never_in_argv()
    {
        var key = RandomKey();
        var runner = new ScriptedRunner().Answer(0, "{\"ReturnValue\":0}").Answer(0, "{\"ReturnValue\":0}");
        var volumes = new PowerShellBitLockerVolumes(runner);

        await volumes.ProtectKeyWithExternalKeyAsync(_guid, key, Ct);
        await volumes.UnlockWithExternalKeyAsync(_guid, key, Ct);

        var base64 = Convert.ToBase64String(key);
        Assert.Equal(2, runner.Commands.Count);
        foreach (var command in runner.Commands)
        {
            Assert.Equal(base64 + "\n", command.StandardInput);
            Assert.All(command.Arguments, a => Assert.DoesNotContain(base64, a, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Bitlocker_seam_refuses_a_key_that_is_not_32_bytes_without_running_anything()
    {
        var runner = new ScriptedRunner();

        await Assert.ThrowsAsync<ArgumentException>(async () => await new PowerShellBitLockerVolumes(runner).UnlockWithExternalKeyAsync(_guid, new byte[16], Ct));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Bitlocker_seam_parses_the_status_replies()
    {
        var runner = new ScriptedRunner()
            .Answer(0, "{\"ReturnValue\":0,\"ConversionStatus\":1,\"EncryptionPercentage\":100}\r\n")
            .Answer(0, "{\"ReturnValue\":0,\"ProtectionStatus\":1}")
            .Answer(0, "{\"ReturnValue\":0,\"EncryptionMethod\":7}")
            .Answer(0, "{\"ReturnValue\":0,\"LockStatus\":1}");
        var volumes = new PowerShellBitLockerVolumes(runner);

        Assert.Equal(1, await volumes.GetConversionStatusAsync(_guid, Ct));
        Assert.Equal(1, await volumes.GetProtectionStatusAsync(_guid, Ct));
        Assert.Equal(7, await volumes.GetEncryptionMethodAsync(_guid, Ct));
        Assert.Equal(1, await volumes.GetLockStatusAsync(_guid, Ct));
    }

    [Fact]
    public async Task Bitlocker_seam_throws_with_the_return_value_when_a_call_fails()
    {
        var runner = new ScriptedRunner().Answer(0, "{\"ReturnValue\":2150694948}");

        var error = await Assert.ThrowsAsync<BitLockerException>(async () => await new PowerShellBitLockerVolumes(runner).EncryptAsync(_guid, 7, 1, Ct));

        Assert.Equal(2150694948u, error.ReturnValue);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "not json")]
    [InlineData(0, "[1,2]")]
    [InlineData(0, "{\"NoReturnValue\":1}")]
    public async Task Bitlocker_seam_throws_on_a_failed_run_or_unreadable_output(int exitCode, string output)
    {
        var runner = new ScriptedRunner().Answer(exitCode, output, "boom");

        await Assert.ThrowsAsync<BitLockerException>(async () => await new PowerShellBitLockerVolumes(runner).GetLockStatusAsync(_guid, Ct));
    }

    [Fact]
    public async Task Bitlocker_lock_returns_the_raw_value_so_files_in_use_are_an_answer_and_not_a_crash()
    {
        var runner = new ScriptedRunner().Answer(0, "{\"ReturnValue\":2150694946}").Answer(0, "{\"ReturnValue\":0}");
        var volumes = new PowerShellBitLockerVolumes(runner);

        Assert.Equal(2150694946u, await volumes.LockAsync(_guid, forceDismount: false, Ct));
        Assert.Equal(0u, await volumes.LockAsync(_guid, forceDismount: false, Ct));
        Assert.All(runner.Commands, c => Assert.Contains("ForceDismount = $false", Encoding.Unicode.GetString(Convert.FromBase64String(c.Arguments[3])), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Diskpart_seam_sends_the_script_on_stdin_with_no_arguments()
    {
        var runner = new ScriptedRunner().Answer(0, "DiskPart successfully created the virtual disk file.");
        var disks = new DiskpartVirtualDisks(runner);

        await disks.CreateAsync(@"D:\bazaar\data.vhdx", 1L << 30, Ct);

        var command = Assert.Single(runner.Commands);
        Assert.Equal(SystemTools.Diskpart, command.Tool);
        Assert.Empty(command.Arguments);
        Assert.Equal(DiskpartScripts.CreateVdisk(@"D:\bazaar\data.vhdx", 1L << 30), command.StandardInput);
    }

    [Theory]
    [InlineData(1, "")]
    [InlineData(0, "DiskPart has encountered an error: The parameter is incorrect.")]
    public async Task Diskpart_seam_fails_on_a_nonzero_exit_or_an_error_text(int exitCode, string output)
    {
        var runner = new ScriptedRunner().Answer(exitCode, output);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new DiskpartVirtualDisks(runner).AttachAsync(@"D:\a.vhdx", Ct));
    }

    [Theory]
    [InlineData("True\r\n", true)]
    [InlineData("False\r\n", false)]
    public async Task Attached_state_comes_from_get_diskimage(string output, bool expected)
    {
        var runner = new ScriptedRunner().Answer(0, output);

        Assert.Equal(expected, await new DiskpartVirtualDisks(runner).IsAttachedAsync(@"D:\a.vhdx", Ct));
        Assert.Equal(SystemTools.PowerShell, runner.Commands[0].Tool);
    }

    [Fact]
    public async Task Mountvol_gets_the_folder_and_the_delete_switch_as_arguments()
    {
        var runner = new ScriptedRunner();

        await new DiskpartVirtualDisks(runner).RemoveMountPointAsync(@"D:\bazaar\data", Ct);

        var command = Assert.Single(runner.Commands);
        Assert.Equal(SystemTools.Mountvol, command.Tool);
        Assert.Equal([@"D:\bazaar\data\", "/D"], command.Arguments);
    }

    [Fact]
    public void VolumeGuid_canonical_form_is_lowercase_without_braces_or_path_decoration()
    {
        const string Canonical = "0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70";

        Assert.Equal(Canonical, VolumeGuid.Canonicalize(@"\\?\Volume{0B6F2A1E-5C3D-4E7A-9B1F-3A2C4D5E6F70}\"));
        Assert.Equal(Canonical, VolumeGuid.Canonicalize("{0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70}"));
        Assert.Equal(Canonical, VolumeGuid.Canonicalize(Canonical.ToUpperInvariant()));
        Assert.Equal(@"\\?\Volume{0b6f2a1e-5c3d-4e7a-9b1f-3a2c4d5e6f70}\", VolumeGuid.ToVolumePath(Canonical));
        Assert.True(VolumeGuid.IsCanonical(Canonical));
        Assert.False(VolumeGuid.IsCanonical(Canonical.ToUpperInvariant()));
        Assert.False(VolumeGuid.TryCanonicalize("0b6f2a1e5c3d4e7a9b1f3a2c4d5e6f70", out _));
        Assert.False(VolumeGuid.TryCanonicalize(null, out _));
    }

    [Fact]
    public async Task The_command_runner_refuses_a_tool_that_is_not_allow_listed()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () => await new ProcessCommandRunner().RunAsync(new CommandSpec("cmd.exe", ["/c", "dir"]), Ct));
    }

    private static byte[] RandomKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}
