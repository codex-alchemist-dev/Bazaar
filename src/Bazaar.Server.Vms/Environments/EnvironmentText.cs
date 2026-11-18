namespace Bazaar.Server.Vms.Environments;

/// <summary>
/// Maps the enums to the text stored in the <c>vms</c> tables and back. Parsing is strict: an unknown value from the
/// database throws, so a schema or code mismatch cannot pass silently.
/// </summary>
public static class EnvironmentText
{
    public static string ToDb(VmOs value) => value switch
    {
        VmOs.Windows => "windows",
        VmOs.Linux => "linux",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(GoldenImageState value) => value.ToString();

    public static string ToDb(VmState value) => value.ToString();

    public static string ToDb(MountKind value) => value switch
    {
        MountKind.Soul => "soul",
        MountKind.Workspace => "workspace",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static string ToDb(MountMode value) => value switch
    {
        MountMode.Ro => "ro",
        MountMode.Rw => "rw",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    public static VmOs ParseVmOs(string text) => text switch
    {
        "windows" => VmOs.Windows,
        "linux" => VmOs.Linux,
        _ => throw Unknown("vm os", text),
    };

    public static GoldenImageState ParseGoldenImageState(string text) => text switch
    {
        "Building" => GoldenImageState.Building,
        "Ready" => GoldenImageState.Ready,
        "Retired" => GoldenImageState.Retired,
        _ => throw Unknown("golden image state", text),
    };

    public static VmState ParseVmState(string text) => text switch
    {
        "Absent" => VmState.Absent,
        "Provisioning" => VmState.Provisioning,
        "Stopped" => VmState.Stopped,
        "Running" => VmState.Running,
        "Saved" => VmState.Saved,
        "Checkpointed" => VmState.Checkpointed,
        "Restored" => VmState.Restored,
        "Retired" => VmState.Retired,
        _ => throw Unknown("vm state", text),
    };

    public static MountKind ParseMountKind(string text) => text switch
    {
        "soul" => MountKind.Soul,
        "workspace" => MountKind.Workspace,
        _ => throw Unknown("mount kind", text),
    };

    public static MountMode ParseMountMode(string text) => text switch
    {
        "ro" => MountMode.Ro,
        "rw" => MountMode.Rw,
        _ => throw Unknown("mount mode", text),
    };

    private static InvalidDataException Unknown(string what, string text) =>
        new($"Unknown {what} '{text}' in the vms tables; the schema and this build disagree.");
}
