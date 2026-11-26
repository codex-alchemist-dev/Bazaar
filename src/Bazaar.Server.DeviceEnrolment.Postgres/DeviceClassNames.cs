namespace Bazaar.Server.DeviceEnrolment.Postgres;

/// <summary>The <c>device.class</c> column values (remote/0001 CHECK list) for each <see cref="DeviceClass"/>.</summary>
internal static class DeviceClassNames
{
    public static string Name(DeviceClass deviceClass) => deviceClass switch
    {
        DeviceClass.Windows => "windows",
        DeviceClass.Linux => "linux",
        DeviceClass.Android => "android",
        DeviceClass.Terminal => "terminal",
        DeviceClass.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(deviceClass)),
    };
}
