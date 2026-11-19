using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>One message from the validation layer, as captured by the debug messenger.</summary>
public sealed record VulkanValidationMessage(string Severity, string Text);

/// <summary>
/// Draws node instances into an offscreen B8G8R8A8 image, then copies the pixels to host memory. It needs no window
/// or surface, so it works without VK_EXT_headless_surface, and it can read the image back for checks.
/// It can also write GPU timestamps around the node draw. Each call submits one frame and waits for it.
/// </summary>
public sealed unsafe class VulkanOffscreenRenderer : IDisposable
{
    /// <summary>Colour format of the offscreen target. Bytes per pixel in memory order: B, G, R, A.</summary>
    public const VkFormat TargetFormat = VkFormat.B8G8R8A8Unorm;

    private const string _validationLayer = "VK_LAYER_KHRONOS_validation";
    private const string _debugUtilsExtension = "VK_EXT_debug_utils";
    private const int _bytesPerPixel = 4;

    private static readonly ConcurrentQueue<VulkanValidationMessage> _validationMessages = new();

    private readonly uint _width;
    private readonly uint _height;
    private VkInstance _instance;
    private VkInstanceApi _instanceApi = null!;
    private VkPhysicalDevice _physicalDevice;
    private VkDevice _device;
    private VkDeviceApi _deviceApi = null!;
    private VkQueue _queue;
    private VkCommandPool _commandPool;
    private VkCommandBuffer _commandBuffer;
    private VkFence _fence;
    private VkImage _image;
    private VkDeviceMemory _imageMemory;
    private VkImageView _imageView;
    private VkFramebuffer _framebuffer;
    private VkBuffer _readBuffer;
    private VkDeviceMemory _readMemory;
    private void* _readMapped;
    private VkQueryPool _queryPool;
    private double _timestampNanosecondsPerTick;
    private bool _timestampsSupported;
    private VkDebugUtilsMessengerEXT _messenger;
    private NodePipeline _pipeline = null!;
    private bool _disposed;

    private VulkanOffscreenRenderer(uint width, uint height)
    {
        _width = width;
        _height = height;
    }

    /// <summary>True when the Khronos validation layer was enabled on this renderer's instance.</summary>
    public bool ValidationLayerEnabled { get; private set; }

    /// <summary>Name of the GPU the renderer runs on.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>True when the GPU reports timestamp queries for graphics work.</summary>
    public bool TimestampsSupported => _timestampsSupported;

    /// <summary>
    /// Creates the renderer. Throws <see cref="InvalidOperationException"/> with the reason when the loader or device is unusable.
    /// </summary>
    public static VulkanOffscreenRenderer Create(uint width, uint height, bool enableValidation = false)
    {
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        var renderer = new VulkanOffscreenRenderer(width, height);
        try
        {
            renderer.Initialise(enableValidation);
            return renderer;
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>Takes and clears the validation messages captured since the last call.</summary>
    public static IReadOnlyList<VulkanValidationMessage> DrainValidationMessages()
    {
        var messages = new List<VulkanValidationMessage>();
        while (_validationMessages.TryDequeue(out VulkanValidationMessage? message))
        {
            messages.Add(message);
        }

        return messages;
    }

    /// <summary>
    /// Replaces the style table with packed bytes from <see cref="NodeStylePacking.Pack"/>. Call before rendering
    /// instances that refer to styles; without a style table, instances are not drawn.
    /// </summary>
    public void SetStyles(ReadOnlySpan<byte> styleBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pipeline.UploadStyles(styleBytes);
    }

    /// <summary>
    /// Clears to <paramref name="clear"/>, draws the instances, and copies the image to host memory.
    /// Read the result with <see cref="ReadPixels"/> after the call returns.
    /// </summary>
    public void Render((float R, float G, float B, float A) clear, ReadOnlySpan<byte> instanceBytes, int instanceCount, NodeView view) =>
        Submit(clear, instanceBytes, instanceCount, view, timed: false);

    /// <summary>
    /// Like <see cref="Render"/>, and returns the GPU time of the node render pass in milliseconds, measured with
    /// timestamp queries. Throws when the device does not report timestamp support.
    /// </summary>
    public double RenderTimed((float R, float G, float B, float A) clear, ReadOnlySpan<byte> instanceBytes, int instanceCount, NodeView view)
    {
        if (!_timestampsSupported)
        {
            throw new NotSupportedException("This Vulkan device does not report timestamp queries for graphics work.");
        }

        Submit(clear, instanceBytes, instanceCount, view, timed: true);
        ulong* ticks = stackalloc ulong[2];
        VkResult result = _deviceApi.vkGetQueryPoolResults(_queryPool, 0, 2, 16, ticks, 8, VkQueryResultFlags.Wait | VkQueryResultFlags.Bit64);
        if (result != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan timestamp read failed ({result}).");
        }

        return (ticks[1] - ticks[0]) * _timestampNanosecondsPerTick / 1_000_000.0;
    }

    /// <summary>The last rendered image, top row first, as B, G, R, A bytes. Valid until the next render or dispose.</summary>
    public ReadOnlySpan<byte> ReadPixels()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new ReadOnlySpan<byte>(_readMapped, (int)(_width * _height * _bytesPerPixel));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_device.Handle != 0)
        {
            _deviceApi.vkDeviceWaitIdle();
            _pipeline?.Dispose();
            if (_readMapped is not null)
            {
                _deviceApi.vkUnmapMemory(_readMemory);
            }

            DestroyIfSet(_readBuffer, b => _deviceApi.vkDestroyBuffer(b, null));
            DestroyIfSet(_readMemory, m => _deviceApi.vkFreeMemory(m, null));
            DestroyIfSet(_framebuffer, f => _deviceApi.vkDestroyFramebuffer(f, null));
            DestroyIfSet(_imageView, v => _deviceApi.vkDestroyImageView(v, null));
            DestroyIfSet(_image, i => _deviceApi.vkDestroyImage(i, null));
            DestroyIfSet(_imageMemory, m => _deviceApi.vkFreeMemory(m, null));
            DestroyIfSet(_queryPool, q => _deviceApi.vkDestroyQueryPool(q, null));
            DestroyIfSet(_fence, f => _deviceApi.vkDestroyFence(f, null));
            DestroyIfSet(_commandPool, p => _deviceApi.vkDestroyCommandPool(p, null));
            _deviceApi.vkDestroyDevice(null);
        }

        if (_messenger.Handle != 0)
        {
            _instanceApi.vkDestroyDebugUtilsMessengerEXT(_messenger, null);
        }

        if (_instance.Handle != 0)
        {
            _instanceApi.vkDestroyInstance(null);
        }
    }

    private void Initialise(bool enableValidation)
    {
        VkResult init = vkInitialize(null);
        if (init != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan loader did not initialise ({init}).");
        }

        CreateInstance(enableValidation);
        PickDevice();
        CreateDevice();
        CreateTargets();
    }

    private void CreateInstance(bool enableValidation)
    {
        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.ApplicationInfo,
            apiVersion = new VkVersion(VulkanProbe.TargetApiVersion),
        };

        bool layerAvailable = enableValidation && HasInstanceLayer(_validationLayer);
        bool debugAvailable = layerAvailable && VulkanProbe.InstanceExtensionNames().Contains(_debugUtilsExtension);
        string[] layers = layerAvailable ? [_validationLayer] : [];
        string[] extensions = debugAvailable ? [_debugUtilsExtension] : [];

        using var layerNames = new NativeStrings(layers);
        using var extensionNames = new NativeStrings(extensions);
        var createInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.InstanceCreateInfo,
            pApplicationInfo = &appInfo,
            enabledLayerCount = (uint)layers.Length,
            ppEnabledLayerNames = layerNames.Pointers,
            enabledExtensionCount = (uint)extensions.Length,
            ppEnabledExtensionNames = extensionNames.Pointers,
        };
        VkResult created = vkCreateInstance(&createInfo, null, out _instance);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan instance could not be created ({created}).");
        }

        _instanceApi = new VkInstanceApi(in _instance);
        ValidationLayerEnabled = layerAvailable;
        if (debugAvailable)
        {
            var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
            {
                sType = VkStructureType.DebugUtilsMessengerCreateInfoEXT,
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = &DebugCallback,
            };
            _instanceApi.vkCreateDebugUtilsMessengerEXT(&messengerInfo, null, out _messenger);
        }
    }

    private static bool HasInstanceLayer(string name)
    {
        uint count = 0;
        if (vkEnumerateInstanceLayerProperties(&count, null) != VkResult.Success || count == 0)
        {
            return false;
        }

        var layers = new VkLayerProperties[count];
        fixed (VkLayerProperties* pointer = layers)
        {
            vkEnumerateInstanceLayerProperties(&count, pointer);
            for (int index = 0; index < count; index++)
            {
                if (Marshal.PtrToStringUTF8((nint)(pointer + index)) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Takes the first device with a graphics queue. Timestamp support is recorded, not required.</summary>
    private void PickDevice()
    {
        if (_instanceApi.vkEnumeratePhysicalDevices(out uint count) != VkResult.Success || count == 0)
        {
            throw new InvalidOperationException("Vulkan found no physical devices.");
        }

        var devices = new VkPhysicalDevice[count];
        _instanceApi.vkEnumeratePhysicalDevices(devices);
        foreach (VkPhysicalDevice device in devices)
        {
            _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out uint familyCount);
            var families = new VkQueueFamilyProperties[familyCount];
            _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);
            for (uint index = 0; index < familyCount; index++)
            {
                if ((families[index].queueFlags & VkQueueFlags.Graphics) == 0)
                {
                    continue;
                }

                _physicalDevice = device;
                _queueFamilyIndex = index;
                _timestampsSupported = families[index].timestampValidBits > 0;
                _instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);
                DeviceName = Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? string.Empty;
                _timestampNanosecondsPerTick = properties.limits.timestampPeriod;
                _timestampsSupported &= properties.limits.timestampComputeAndGraphics;
                return;
            }
        }

        throw new InvalidOperationException("No Vulkan device has a graphics queue.");
    }

    private uint _queueFamilyIndex;

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VkStructureType.DeviceQueueCreateInfo,
            queueFamilyIndex = _queueFamilyIndex,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        var createInfo = new VkDeviceCreateInfo
        {
            sType = VkStructureType.DeviceCreateInfo,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
        };
        VkResult created = _instanceApi.vkCreateDevice(_physicalDevice, &createInfo, null, out _device);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan device could not be created ({created}).");
        }

        _deviceApi = new VkDeviceApi(_instanceApi, in _device);
        _deviceApi.vkGetDeviceQueue(_queueFamilyIndex, 0, out _queue);

        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.CommandPoolCreateInfo,
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = _queueFamilyIndex,
        };
        _deviceApi.vkCreateCommandPool(&poolInfo, null, out _commandPool);

        var allocate = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            commandPool = _commandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };
        fixed (VkCommandBuffer* commandBuffers = &_commandBuffer)
        {
            _deviceApi.vkAllocateCommandBuffers(&allocate, commandBuffers);
        }

        var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
        _deviceApi.vkCreateFence(&fenceInfo, null, out _fence);

        if (_timestampsSupported)
        {
            var queryInfo = new VkQueryPoolCreateInfo
            {
                sType = VkStructureType.QueryPoolCreateInfo,
                queryType = VkQueryType.Timestamp,
                queryCount = 2,
            };
            _deviceApi.vkCreateQueryPool(&queryInfo, null, out _queryPool);
        }
    }

    private void CreateTargets()
    {
        var imageInfo = new VkImageCreateInfo
        {
            sType = VkStructureType.ImageCreateInfo,
            imageType = VkImageType.Image2D,
            format = TargetFormat,
            extent = new VkExtent3D { width = _width, height = _height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            tiling = VkImageTiling.Optimal,
            usage = VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferSrc,
            sharingMode = VkSharingMode.Exclusive,
            initialLayout = VkImageLayout.Undefined,
        };
        _deviceApi.vkCreateImage(&imageInfo, null, out _image);
        _deviceApi.vkGetImageMemoryRequirements(_image, out VkMemoryRequirements imageRequirements);
        _imageMemory = Allocate(imageRequirements, VkMemoryPropertyFlags.DeviceLocal);
        _deviceApi.vkBindImageMemory(_image, _imageMemory, 0);

        var viewInfo = new VkImageViewCreateInfo
        {
            sType = VkStructureType.ImageViewCreateInfo,
            image = _image,
            viewType = VkImageViewType.Image2D,
            format = TargetFormat,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };
        _deviceApi.vkCreateImageView(&viewInfo, null, out _imageView);

        _pipeline = new NodePipeline(_instanceApi, _deviceApi, _physicalDevice, TargetFormat, VkImageLayout.TransferSrcOptimal);

        VkImageView attachment = _imageView;
        var framebufferInfo = new VkFramebufferCreateInfo
        {
            sType = VkStructureType.FramebufferCreateInfo,
            renderPass = _pipeline.RenderPass,
            attachmentCount = 1,
            pAttachments = &attachment,
            width = _width,
            height = _height,
            layers = 1,
        };
        _deviceApi.vkCreateFramebuffer(&framebufferInfo, null, out _framebuffer);

        ulong bytes = (ulong)(_width * _height * _bytesPerPixel);
        var bufferInfo = new VkBufferCreateInfo
        {
            sType = VkStructureType.BufferCreateInfo,
            size = bytes,
            usage = VkBufferUsageFlags.TransferDst,
            sharingMode = VkSharingMode.Exclusive,
        };
        _deviceApi.vkCreateBuffer(&bufferInfo, null, out _readBuffer);
        _deviceApi.vkGetBufferMemoryRequirements(_readBuffer, out VkMemoryRequirements bufferRequirements);
        _readMemory = Allocate(bufferRequirements, VkMemoryPropertyFlags.HostVisible | VkMemoryPropertyFlags.HostCoherent);
        _deviceApi.vkBindBufferMemory(_readBuffer, _readMemory, 0);
        void* mapped;
        _deviceApi.vkMapMemory(_readMemory, 0, bytes, VkMemoryMapFlags.None, &mapped);
        _readMapped = mapped;
    }

    private VkDeviceMemory Allocate(VkMemoryRequirements requirements, VkMemoryPropertyFlags wanted)
    {
        _instanceApi.vkGetPhysicalDeviceMemoryProperties(_physicalDevice, out VkPhysicalDeviceMemoryProperties properties);
        for (uint index = 0; index < properties.memoryTypeCount; index++)
        {
            bool allowed = (requirements.memoryTypeBits & (1u << (int)index)) != 0;
            if (allowed && (properties.memoryTypes[(int)index].propertyFlags & wanted) == wanted)
            {
                var allocate = new VkMemoryAllocateInfo
                {
                    sType = VkStructureType.MemoryAllocateInfo,
                    allocationSize = requirements.size,
                    memoryTypeIndex = index,
                };
                _deviceApi.vkAllocateMemory(&allocate, null, out VkDeviceMemory memory);
                return memory;
            }
        }

        throw new InvalidOperationException($"Vulkan device has no memory type with {wanted}.");
    }

    private void Submit((float R, float G, float B, float A) clear, ReadOnlySpan<byte> instanceBytes, int instanceCount, NodeView view, bool timed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pipeline.Upload(instanceBytes);

        VkFence fence = _fence;
        _deviceApi.vkResetFences(1, &fence);
        _deviceApi.vkResetCommandBuffer(_commandBuffer, VkCommandBufferResetFlags.None);
        var begin = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };
        _deviceApi.vkBeginCommandBuffer(_commandBuffer, &begin);

        if (timed)
        {
            _deviceApi.vkCmdResetQueryPool(_commandBuffer, _queryPool, 0, 2);
            _deviceApi.vkCmdWriteTimestamp(_commandBuffer, VkPipelineStageFlags.TopOfPipe, _queryPool, 0);
        }

        _pipeline.Record(_commandBuffer, _framebuffer, new VkExtent2D(_width, _height), clear, instanceCount, view);

        if (timed)
        {
            _deviceApi.vkCmdWriteTimestamp(_commandBuffer, VkPipelineStageFlags.BottomOfPipe, _queryPool, 1);
        }

        var region = new VkBufferImageCopy
        {
            bufferOffset = 0,
            bufferRowLength = 0,
            bufferImageHeight = 0,
            imageSubresource = new VkImageSubresourceLayers
            {
                aspectMask = VkImageAspectFlags.Color,
                mipLevel = 0,
                baseArrayLayer = 0,
                layerCount = 1,
            },
            imageOffset = new VkOffset3D(0, 0, 0),
            imageExtent = new VkExtent3D { width = _width, height = _height, depth = 1 },
        };
        _deviceApi.vkCmdCopyImageToBuffer(_commandBuffer, _image, VkImageLayout.TransferSrcOptimal, _readBuffer, 1, &region);

        var barrier = new VkBufferMemoryBarrier
        {
            sType = VkStructureType.BufferMemoryBarrier,
            srcAccessMask = VkAccessFlags.TransferWrite,
            dstAccessMask = VkAccessFlags.HostRead,
            srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED,
            buffer = _readBuffer,
            offset = 0,
            size = ulong.MaxValue,
        };
        _deviceApi.vkCmdPipelineBarrier(_commandBuffer, VkPipelineStageFlags.Transfer, VkPipelineStageFlags.Host, default, 0, null, 1, &barrier, 0, null);
        _deviceApi.vkEndCommandBuffer(_commandBuffer);

        VkCommandBuffer commandBuffer = _commandBuffer;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
        };
        _deviceApi.vkQueueSubmit(_queue, 1, &submit, fence);
        _deviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
    }

    [UnmanagedCallersOnly]
    private static uint DebugCallback(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, VkDebugUtilsMessengerCallbackDataEXT* data, void* userData)
    {
        string text = data is null || data->pMessage is null ? string.Empty : Marshal.PtrToStringUTF8((nint)data->pMessage) ?? string.Empty;
        string label = (severity & VkDebugUtilsMessageSeverityFlagsEXT.Error) != 0 ? "error" : "warning";
        _validationMessages.Enqueue(new VulkanValidationMessage(label, text));
        return 0;
    }

    private static void DestroyIfSet<T>(T handle, Action<T> destroy) where T : struct
    {
        if (!EqualityComparer<T>.Default.Equals(handle, default))
        {
            destroy(handle);
        }
    }

    /// <summary>Null-terminated UTF-8 names kept alive for one Vulkan call.</summary>
    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] _handles;

        public NativeStrings(string[] values)
        {
            _handles = values.Select(value => Marshal.StringToCoTaskMemUTF8(value)).ToArray();
            Pointers = (byte**)Marshal.AllocCoTaskMem(IntPtr.Size * Math.Max(_handles.Length, 1));
            for (int index = 0; index < _handles.Length; index++)
            {
                Pointers[index] = (byte*)_handles[index];
            }
        }

        public byte** Pointers { get; }

        public void Dispose()
        {
            foreach (nint handle in _handles)
            {
                Marshal.FreeCoTaskMem(handle);
            }

            Marshal.FreeCoTaskMem((nint)Pointers);
        }
    }
}
