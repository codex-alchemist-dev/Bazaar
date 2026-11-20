namespace Bazaar.Server.Vms.Devices.Setup;

/// <summary>Text the setup wizard shows for the optional device-guest section (doc 62 sections 3 and 7).</summary>
public static class DeviceSetupText
{
    /// <summary>Shown before the wizard offers iOS or macOS guests (doc 62 section 3, rule 3).</summary>
    public const string AppleHardwareNotice =
        "Apple's licence allows macOS and the iOS Simulator only on Apple-branded hardware, so iOS and macOS guests need a Mac and are not available yet.";
}
