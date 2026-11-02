using Bazaar.Server.Vms.Environments;

namespace Bazaar.Server.Vms.Tests.Environments;

/// <summary>Strict enum text mapping: every value round-trips and an unknown database value throws.</summary>
public sealed class EnvironmentTextTests
{
    [Fact]
    public void Vm_state_round_trips_for_every_value()
    {
        foreach (var state in Enum.GetValues<VmState>())
            Assert.Equal(state, EnvironmentText.ParseVmState(EnvironmentText.ToDb(state)));
    }

    [Fact]
    public void Golden_image_state_round_trips_for_every_value()
    {
        foreach (var state in Enum.GetValues<GoldenImageState>())
            Assert.Equal(state, EnvironmentText.ParseGoldenImageState(EnvironmentText.ToDb(state)));
    }

    [Fact]
    public void Os_mount_kind_and_mode_use_their_lower_case_database_text()
    {
        Assert.Equal("windows", EnvironmentText.ToDb(VmOs.Windows));
        Assert.Equal("linux", EnvironmentText.ToDb(VmOs.Linux));
        Assert.Equal("soul", EnvironmentText.ToDb(MountKind.Soul));
        Assert.Equal("workspace", EnvironmentText.ToDb(MountKind.Workspace));
        Assert.Equal("ro", EnvironmentText.ToDb(MountMode.Ro));
        Assert.Equal("rw", EnvironmentText.ToDb(MountMode.Rw));

        Assert.Equal(VmOs.Linux, EnvironmentText.ParseVmOs("linux"));
        Assert.Equal(MountKind.Workspace, EnvironmentText.ParseMountKind("workspace"));
        Assert.Equal(MountMode.Rw, EnvironmentText.ParseMountMode("rw"));
    }

    [Theory]
    [InlineData("Bogus")]
    [InlineData("running")]
    [InlineData("")]
    public void Unknown_vm_state_throws(string text)
    {
        Assert.Throws<InvalidDataException>(() => EnvironmentText.ParseVmState(text));
    }

    [Theory]
    [InlineData("Linux")]
    [InlineData("macos")]
    public void Unknown_or_differently_cased_os_throws(string text)
    {
        Assert.Throws<InvalidDataException>(() => EnvironmentText.ParseVmOs(text));
    }

    [Fact]
    public void Unknown_mount_kind_and_mode_throw()
    {
        Assert.Throws<InvalidDataException>(() => EnvironmentText.ParseMountKind("home"));
        Assert.Throws<InvalidDataException>(() => EnvironmentText.ParseMountMode("rwx"));
        Assert.Throws<InvalidDataException>(() => EnvironmentText.ParseGoldenImageState("Ready "));
    }
}
