using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>One physical device as the loader reports it.</summary>
public sealed record VulkanDeviceInfo(string Name, string DeviceType, string ApiVersion, bool MeshShaderExtension);

/// <summary>Result of probing the loader and devices. Available means at least one device was found.</summary>
public sealed record VulkanProbeResult(bool Available, string Reason, string InstanceApiVersion, IReadOnlyList<VulkanDeviceInfo> Devices);

/// <summary>
/// Checks what the Vulkan loader offers on this machine, without drawing anything.
/// The highest core version is requested so newer drivers are used as they ship (forward-facing rule, CLAUDE.md).
/// Optional extensions such as mesh shading are reported, never assumed.
/// </summary>
public static unsafe class VulkanProbe
{
    /// <summary>Highest core version requested from the loader. Raise it when the SDK adds a newer one.</summary>
    public static uint TargetApiVersion { get; } = MakeVersion(1, 4, 0);

    public static VulkanProbeResult Run()
    {
        try
        {
            VkResult init = vkInitialize(null);
            if (init != VkResult.Success)
            {
                return Unavailable($"Vulkan loader did not initialise ({init}).");
            }

            uint loaderVersion = vkEnumerateInstanceVersion();
            uint apiVersion = Math.Min(loaderVersion, TargetApiVersion);
            var appInfo = new VkApplicationInfo
            {
                sType = VkStructureType.ApplicationInfo,
                apiVersion = new VkVersion(apiVersion),
            };
            var createInfo = new VkInstanceCreateInfo
            {
                sType = VkStructureType.InstanceCreateInfo,
                pApplicationInfo = &appInfo,
            };

            VkResult created = vkCreateInstance(&createInfo, null, out VkInstance instance);
            if (created != VkResult.Success)
            {
                return Unavailable($"Vulkan instance could not be created ({created}).", VersionString(loaderVersion));
            }

            var api = new VkInstanceApi(in instance);
            try
            {
                return new VulkanProbeResult(
                    Available: false,
                    Reason: string.Empty,
                    InstanceApiVersion: VersionString(apiVersion),
                    Devices: EnumerateDevices(api));
            }
            finally
            {
                api.vkDestroyInstance();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or SEHException)
        {
            return Unavailable($"Vulkan loader is not usable on this machine ({ex.GetType().Name}).");
        }
    }

    private static List<VulkanDeviceInfo> EnumerateDevices(VkInstanceApi api)
    {
        if (api.vkEnumeratePhysicalDevices(out uint count) != VkResult.Success || count == 0)
        {
            return [];
        }

        var handles = new VkPhysicalDevice[count];
        api.vkEnumeratePhysicalDevices(handles);
        var devices = new List<VulkanDeviceInfo>((int)count);
        foreach (VkPhysicalDevice device in handles)
        {
            api.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);
            devices.Add(new VulkanDeviceInfo(
                Name: Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? string.Empty,
                DeviceType: properties.deviceType.ToString(),
                ApiVersion: VersionString((uint)properties.apiVersion),
                MeshShaderExtension: HasExtension(api, device, "VK_EXT_mesh_shader")));
        }

        return devices;
    }

    private static bool HasExtension(VkInstanceApi api, VkPhysicalDevice device, string name)
    {
        if (api.vkEnumerateDeviceExtensionProperties(device, out uint count) != VkResult.Success || count == 0)
        {
            return false;
        }

        var extensions = new VkExtensionProperties[count];
        api.vkEnumerateDeviceExtensionProperties(device, extensions);
        return extensions.Any(extension => Marshal.PtrToStringUTF8((nint)extension.extensionName) == name);
    }

    private static VulkanProbeResult Unavailable(string reason, string instanceVersion = "") =>
        new(false, reason, instanceVersion, []);

    /// <summary>Packs a Vulkan version the way VK_MAKE_API_VERSION does.</summary>
    public static uint MakeVersion(uint major, uint minor, uint patch) => (major << 22) | (minor << 12) | patch;

    public static string VersionString(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FFu}.{version & 0xFFFu}";
}
