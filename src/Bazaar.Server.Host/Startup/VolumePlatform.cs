using Bazaar.Server.DataVolume;
using Bazaar.Server.DataVolume.Linux;
using Bazaar.Server.DataVolume.Windows;

namespace Bazaar.Server.Host.Startup;

/// <summary>The data volume operators and providers of this OS, shared by <c>volume</c> and <c>storage</c>: one place per OS (doc 63 section 4).</summary>
internal static class VolumePlatform
{
    public static (List<IDataVolumeOperator> Operators, List<IDataVolumeProvider> Providers) Create()
    {
        var operators = new List<IDataVolumeOperator>();
        var providers = new List<IDataVolumeProvider> { new DevPlainDataVolumeProvider() };

        if (OperatingSystem.IsLinux())
        {
            operators.Add(new Luks2DataVolumeOperator());
            providers.Add(new Luks2DataVolumeProvider());
        }
        else if (OperatingSystem.IsWindows())
        {
            operators.Add(BitLockerVhdxDataVolumeOperator.CreateDefault());
            providers.Add(BitLockerVhdxDataVolumeProvider.CreateDefault());
        }

        return (operators, providers);
    }
}
