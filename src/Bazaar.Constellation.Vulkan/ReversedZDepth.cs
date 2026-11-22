using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Reversed-Z depth setup: a 32-bit float depth buffer where 1 is the near plane and 0 is far (infinitely far for an
/// infinite far plane). Floats are densest near zero, so reversing the range spends that precision on the far field,
/// where perspective's own 1/z already starves it. Depth is cleared to 0 (far), the test is greater-or-equal, and the
/// Hi-Z pyramid keeps the MIN of each footprint (the farthest surface). Off by default: the 2D path has no depth.
/// </summary>
public static class ReversedZDepth
{
    /// <summary>Float depth format; the design's logarithmic-depth fallback applies when a device lacks it.</summary>
    public const VkFormat Format = VkFormat.D32Sfloat;

    /// <summary>Depth clear value: 0 is the far plane in reversed-Z.</summary>
    public const float ClearDepth = 0f;

    /// <summary>Depth compare: a fragment passes when it is nearer (greater) or equal.</summary>
    public const VkCompareOp CompareOp = VkCompareOp.GreaterOrEqual;

    /// <summary>Depth for a point <paramref name="distance"/> in front of the eye with an infinite far plane: near / distance, so 1 at the near plane, falling toward 0.</summary>
    public static float NdcDepth(float near, float distance)
    {
        if (near <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(near), "Near plane must be positive.");
        }

        return distance <= near ? 1f : near / distance;
    }

    /// <summary>Clear value for the depth attachment: depth 0, stencil 0.</summary>
    public static VkClearValue ClearValue()
    {
        return new VkClearValue(new VkClearDepthStencilValue(ClearDepth, 0));
    }

    /// <summary>Render-pass attachment: cleared on load and kept, since the Hi-Z pass reads it next.</summary>
    public static VkAttachmentDescription Attachment(VkImageLayout finalLayout) => new()
    {
        format = Format,
        samples = VkSampleCountFlags.Count1,
        loadOp = VkAttachmentLoadOp.Clear,
        storeOp = VkAttachmentStoreOp.Store,
        stencilLoadOp = VkAttachmentLoadOp.DontCare,
        stencilStoreOp = VkAttachmentStoreOp.DontCare,
        initialLayout = VkImageLayout.Undefined,
        finalLayout = finalLayout,
    };

    /// <summary>Pipeline depth state: test on, greater-or-equal, writes on unless <paramref name="write"/> is false.</summary>
    public static VkPipelineDepthStencilStateCreateInfo DepthStencilState(bool write = true) => new()
    {
        sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
        depthTestEnable = true,
        depthWriteEnable = write,
        depthCompareOp = CompareOp,
        depthBoundsTestEnable = false,
        stencilTestEnable = false,
        minDepthBounds = 0f,
        maxDepthBounds = 1f,
    };
}

/// <summary>A depth image, memory and view for reversed-Z rendering, also sampleable so the Hi-Z pass can read it.</summary>
internal sealed unsafe class ReversedZDepthTarget : IDisposable
{
    private readonly VkDeviceApi _deviceApi;
    private readonly VkDeviceMemory _memory;
    private bool _disposed;

    public ReversedZDepthTarget(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice, uint width, uint height)
    {
        _deviceApi = deviceApi;
        Width = width;
        Height = height;
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = ReversedZDepth.Format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.DepthStencilAttachment | VkImageUsageFlags.Sampled,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        VkResult created = deviceApi.vkCreateImage(&imageInfo, null, out VkImage image);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan depth image could not be created ({created}).");
        }

        Image = image;
        deviceApi.vkGetImageMemoryRequirements(Image, out VkMemoryRequirements requirements);
        _memory = VulkanMemory.AllocateDeviceLocal(instanceApi, deviceApi, physicalDevice, requirements);
        deviceApi.vkBindImageMemory(Image, _memory, 0);

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = Image,
            viewType = VkImageViewType.Image2D,
            format = ReversedZDepth.Format,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };
        VkResult viewCreated = deviceApi.vkCreateImageView(&viewInfo, null, out VkImageView view);
        if (viewCreated != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan depth image view could not be created ({viewCreated}).");
        }

        View = view;
    }

    public uint Width { get; }

    public uint Height { get; }

    public VkImage Image { get; }

    public VkImageView View { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _deviceApi.vkDestroyImageView(View, null);
        _deviceApi.vkDestroyImage(Image, null);
        _deviceApi.vkFreeMemory(_memory, null);
    }
}

/// <summary>Device-local memory allocation shared by the depth and Hi-Z images.</summary>
internal static unsafe class VulkanMemory
{
    public static VkDeviceMemory AllocateDeviceLocal(VkInstanceApi instanceApi, VkDeviceApi deviceApi, VkPhysicalDevice physicalDevice, VkMemoryRequirements requirements)
    {
        instanceApi.vkGetPhysicalDeviceMemoryProperties(physicalDevice, out VkPhysicalDeviceMemoryProperties properties);
        for (uint index = 0; index < properties.memoryTypeCount; index++)
        {
            bool allowed = (requirements.memoryTypeBits & (1u << (int)index)) != 0;
            if (allowed && properties.memoryTypes[(int)index].propertyFlags.HasFlag(VkMemoryPropertyFlags.DeviceLocal))
            {
                var allocate = new VkMemoryAllocateInfo
                {
                    sType = VkStructureType.MemoryAllocateInfo,
                    allocationSize = requirements.size,
                    memoryTypeIndex = index,
                };
                VkResult result = deviceApi.vkAllocateMemory(&allocate, null, out VkDeviceMemory memory);
                if (result != VkResult.Success)
                {
                    throw new InvalidOperationException($"Vulkan could not allocate device-local memory ({result}).");
                }

                return memory;
            }
        }

        throw new InvalidOperationException("Vulkan device has no device-local memory type for this image.");
    }
}
