namespace Bazaar.Server.Vms.Devices.Setup;

/// <summary>Text the setup wizard shows for the optional device-guest section (doc 62 sections 3 and 7).</summary>
public static class DeviceSetupText
{
    /// <summary>Shown before the wizard offers iOS or macOS guests (doc 62 sections 3 and 6).</summary>
    public const string AppleHardwareNotice =
        "Apple's licence allows macOS and the iOS Simulator only on Apple-branded hardware, so iOS and macOS guests run only on a Mac node, a headless Bazaar process on a Mac you own. There is no other way to run them.";
}
