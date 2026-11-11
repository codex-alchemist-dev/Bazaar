using System.Xml.Linq;

namespace Bazaar.Architecture.Tests;

/// <summary>
/// Issue #423: the Android head targets API 37 explicitly. The value is pinned in the project file so a newer installed platform
/// cannot change the manifest's targetSdkVersion silently. These tests read the Android branch of the project file.
/// </summary>
public class AndroidTargetTests
{
    private static XElement AndroidPropertyGroup()
    {
        var doc = XDocument.Load(RepoPaths.ProjectFile("Bazaar.Client.Android"));
        // The Android branch is the one conditioned on BazaarAndroid == 'true' (not the != 'true' plain-net11.0 branch).
        return doc.Descendants("PropertyGroup")
            .Single(g => (string?)g.Attribute("Condition") == "'$(BazaarAndroid)' == 'true'");
    }

    [Fact]
    public void Android_head_pins_target_platform_to_api_37()
    {
        Assert.Equal("37.0", AndroidPropertyGroup().Element("TargetPlatformVersion")?.Value);
    }

    [Fact]
    public void Android_head_keeps_minimum_os_at_35()
    {
        // Minimum OS is a separate decision (docs/design/29-android-client.md). Moving the target must not move it.
        Assert.Equal("35", AndroidPropertyGroup().Element("SupportedOSPlatformVersion")?.Value);
    }
}
