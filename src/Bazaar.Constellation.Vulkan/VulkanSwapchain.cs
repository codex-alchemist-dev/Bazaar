using System.Runtime.InteropServices;
using Bazaar.Constellation.Rendering;
using Bazaar.Constellation.Rendering.Core;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace Bazaar.Constellation.Vulkan;

/// <summary>
/// Owns the Vulkan instance, surface, logical device and swapchain for one Constellation view.
/// It binds a native window (or a headless surface for tests and offscreen use), picks a device that can
/// present to it, and clears and presents one frame per call. Node drawing comes on top of this in a later slice.
/// Recreation on resize or out-of-date swapchains is handled here, so the renderer never sees a dead surface.
/// </summary>
public sealed unsafe class VulkanSwapchain : IDisposable
{
    private const string _swapchainExtension = "VK_KHR_swapchain";
    private const string _headlessExtension = "VK_EXT_headless_surface";

    private VkInstance _instance;
    private VkInstanceApi _instanceApi = null!;
    private VkSurfaceKHR _surface;
    private VkPhysicalDevice _physicalDevice;
    private uint _queueFamily;
    private VkDevice _device;
    private VkDeviceApi _deviceApi = null!;
    private VkQueue _queue;
    private VkCommandPool _commandPool;
    private VkSemaphore _imageAvailable;
    private VkFence _frameFence;
    private VkSwapchainKHR _swapchain;
    private VkFormat _format;
    private VkExtent2D _extent;
    private VkImage[] _images = [];
    private VkCommandBuffer[] _commandBuffers = [];
    private VkSemaphore[] _renderFinished = [];
    private VkImageView[] _imageViews = [];
    private VkFramebuffer[] _framebuffers = [];
    private NodePipeline? _pipeline;
    private VkFormat _pipelineFormat;
    private byte[] _styleBytes = [];
    private readonly bool _reversedZ;
    private ReversedZDepthTarget? _depthTarget;
    private bool _disposed;

    private VulkanSwapchain(RenderSurface surface, bool reversedZ)
    {
        Surface = surface;
        _reversedZ = reversedZ;
    }

    /// <summary>True when the swapchain renders with a reversed-Z depth attachment (<see cref="ReversedZDepth"/>).</summary>
    public bool ReversedZEnabled => _reversedZ;

    /// <summary>Depth image for the Hi-Z pass, sized to the current extent; null unless reversed-Z is on or while the swapchain has no drawable area.</summary>
    internal VkImage? DepthImage => _depthTarget?.Image;

    /// <summary>Depth-aspect view of <see cref="DepthImage"/>, for <see cref="HiZPyramidPass"/>. Replaced on every resize.</summary>
    internal VkImageView? DepthView => _depthTarget?.View;

    /// <summary>The surface this swapchain presents to.</summary>
    public RenderSurface Surface { get; private set; }

    /// <summary>Device name chosen at creation, for capability reporting.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>Current swapchain size in pixels.</summary>
    public (uint Width, uint Height) Extent => (_extent.width, _extent.height);

    /// <summary>
    /// Creates the instance, surface, device and swapchain. Returns the reason when no device can present to the surface.
    /// </summary>
    /// <param name="reversedZ">Adds a D32 reversed-Z depth attachment (clear 0, greater-or-equal). Off by default.</param>
    public static (VulkanSwapchain? Swapchain, string Reason) TryCreate(RenderSurface surface, bool reversedZ = false)
    {
        var swapchain = new VulkanSwapchain(surface, reversedZ);
        try
        {
            string reason = swapchain.Initialise();
            if (reason.Length > 0)
            {
                swapchain.Dispose();
                return (null, reason);
            }

            return (swapchain, string.Empty);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or NotSupportedException or SEHException)
        {
            swapchain.Dispose();
            return (null, $"Vulkan surface could not be created ({ex.GetType().Name}).");
        }
    }

    /// <summary>Recreates the swapchain at a new pixel size. The surface and device are kept.</summary>
    public string Resize(int widthPixels, int heightPixels)
    {
        ThrowIfDisposed();
        Surface = Surface with { WidthPixels = widthPixels, HeightPixels = heightPixels };
        return RebuildSwapchain();
    }

    /// <summary>
    /// Replaces the style table with packed bytes from <see cref="NodeStylePacking.Pack"/>. The bytes are kept, so a
    /// rebuilt pipeline gets them again. Waits for the device to go idle when it uploads.
    /// </summary>
    public void SetStyles(ReadOnlySpan<byte> styleBytes)
    {
        ThrowIfDisposed();
        _styleBytes = styleBytes.ToArray();
        _pipeline?.UploadStyles(_styleBytes);
    }

    /// <summary>
    /// Acquires the next image, clears it to <paramref name="clear"/> and presents it.
    /// Returns false when the swapchain had to be rebuilt instead, so the caller can retry on the next tick.
    /// </summary>
    public bool ClearAndPresent((float R, float G, float B, float A) color) =>
        Present(color, ReadOnlySpan<byte>.Empty, 0, NodeView.Identity);

    /// <summary>
    /// Acquires the next image, clears it, draws <paramref name="instanceCount"/> node instances from
    /// <paramref name="instanceBytes"/> (packed as <see cref="Rendering.Core.NodeInstanceBuffer"/>), and presents it.
    /// Returns false when the swapchain had to be rebuilt instead, so the caller can retry on the next tick.
    /// </summary>
    public bool Present((float R, float G, float B, float A) color, ReadOnlySpan<byte> instanceBytes, int instanceCount, NodeView view)
    {
        ThrowIfDisposed();
        if (_swapchain.Handle == 0 || _pipeline is null)
        {
            return false;
        }

        VkFence fence = _frameFence;
        _deviceApi.vkWaitForFences(1, &fence, true, ulong.MaxValue);
        VkResult acquire = _deviceApi.vkAcquireNextImageKHR(_swapchain, ulong.MaxValue, _imageAvailable, default, out uint imageIndex);
        if (acquire is VkResult.ErrorOutOfDateKHR)
        {
            RebuildSwapchain();
            return false;
        }

        if (acquire is not (VkResult.Success or VkResult.SuboptimalKHR))
        {
            throw new InvalidOperationException($"Vulkan could not acquire a swapchain image ({acquire}).");
        }

        _deviceApi.vkResetFences(1, &fence);
        _pipeline.Upload(instanceBytes);
        RecordFrame(imageIndex, color, instanceCount, view);

        VkSemaphore waitSemaphore = _imageAvailable;
        VkCommandBuffer commandBuffer = _commandBuffers[imageIndex];
        VkSemaphore signal = _renderFinished[imageIndex];
        var waitStage = VkPipelineStageFlags.ColorAttachmentOutput;
        var submit = new VkSubmitInfo
        {
            sType = VkStructureType.SubmitInfo,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &waitSemaphore,
            pWaitDstStageMask = &waitStage,
            commandBufferCount = 1,
            pCommandBuffers = &commandBuffer,
            signalSemaphoreCount = 1,
            pSignalSemaphores = &signal,
        };
        _deviceApi.vkQueueSubmit(_queue, 1, &submit, fence);

        VkSwapchainKHR swapchain = _swapchain;
        var present = new VkPresentInfoKHR
        {
            sType = VkStructureType.PresentInfoKHR,
            waitSemaphoreCount = 1,
            pWaitSemaphores = &signal,
            swapchainCount = 1,
            pSwapchains = &swapchain,
            pImageIndices = &imageIndex,
        };
        VkResult presented = _deviceApi.vkQueuePresentKHR(_queue, &present);
        if (presented is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
        {
            RebuildSwapchain();
            return false;
        }

        return presented == VkResult.Success;
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
            DestroySwapchainObjects();
            _pipeline?.Dispose();
            _pipeline = null;
            _depthTarget?.Dispose();
            _depthTarget = null;
            if (_frameFence.Handle != 0)
            {
                _deviceApi.vkDestroyFence(_frameFence, null);
            }

            if (_imageAvailable.Handle != 0)
            {
                _deviceApi.vkDestroySemaphore(_imageAvailable, null);
            }

            if (_commandPool.Handle != 0)
            {
                _deviceApi.vkDestroyCommandPool(_commandPool, null);
            }

            _deviceApi.vkDestroyDevice(null);
        }

        if (_surface.Handle != 0)
        {
            _instanceApi.vkDestroySurfaceKHR(_surface, null);
        }

        if (_instance.Handle != 0)
        {
            _instanceApi.vkDestroyInstance(null);
        }
    }

    private string Initialise()
    {
        string reason = CreateInstanceAndSurface();
        if (reason.Length > 0)
        {
            return reason;
        }

        reason = PickDevice();
        if (reason.Length > 0)
        {
            return reason;
        }

        CreateDevice();
        CreateFrameObjects();
        return RebuildSwapchain();
    }

    private string CreateInstanceAndSurface()
    {
        VkResult init = vkInitialize(null);
        if (init != VkResult.Success)
        {
            return $"Vulkan loader did not initialise ({init}).";
        }

        bool headless = Surface.Kind == RenderSurfaceKind.Headless;
        string platformExtension = Surface.Kind switch
        {
            RenderSurfaceKind.Win32 => "VK_KHR_win32_surface",
            RenderSurfaceKind.Xlib => "VK_KHR_xlib_surface",
            RenderSurfaceKind.Wayland => "VK_KHR_wayland_surface",
            RenderSurfaceKind.Android => "VK_KHR_android_surface",
            _ => _headlessExtension,
        };
        string[] extensions = ["VK_KHR_surface", platformExtension];
        if (headless && !VulkanProbe.InstanceExtensionNames().Contains(_headlessExtension))
        {
            return $"Headless surface needs {_headlessExtension}, which this Vulkan loader does not offer.";
        }

        using var names = new NativeStrings(extensions);
        var appInfo = new VkApplicationInfo
        {
            sType = VkStructureType.ApplicationInfo,
            apiVersion = new VkVersion(VulkanProbe.TargetApiVersion),
        };
        var createInfo = new VkInstanceCreateInfo
        {
            sType = VkStructureType.InstanceCreateInfo,
            pApplicationInfo = &appInfo,
            enabledExtensionCount = (uint)extensions.Length,
            ppEnabledExtensionNames = names.Pointers,
        };
        VkResult created = vkCreateInstance(&createInfo, null, out _instance);
        if (created != VkResult.Success)
        {
            return $"Vulkan instance could not be created ({created}).";
        }

        _instanceApi = new VkInstanceApi(in _instance);
        VkResult surfaceResult = headless
            ? CreateHeadlessSurface()
            : CreatePlatformSurface();
        return surfaceResult == VkResult.Success
            ? string.Empty
            : $"Vulkan could not create a surface for {Surface.Kind} ({surfaceResult}).";
    }

    private VkResult CreateHeadlessSurface()
    {
        var info = new VkHeadlessSurfaceCreateInfoEXT { sType = VkStructureType.HeadlessSurfaceCreateInfoEXT };
        VkSurfaceKHR surface = default;
        VkResult result = _instanceApi.vkCreateHeadlessSurfaceEXT(&info, null, &surface);
        _surface = surface;
        return result;
    }

    private VkResult CreatePlatformSurface()
    {
        switch (Surface.Kind)
        {
            case RenderSurfaceKind.Win32:
                {
                    var info = new VkWin32SurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.Win32SurfaceCreateInfoKHR,
                        hinstance = Surface.PlatformDisplay,
                        hwnd = Surface.PlatformWindow,
                    };
                    return _instanceApi.vkCreateWin32SurfaceKHR(&info, null, out _surface);
                }
            case RenderSurfaceKind.Xlib:
                {
                    var info = new VkXlibSurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.XlibSurfaceCreateInfoKHR,
                        dpy = Surface.PlatformDisplay,
                        window = (ulong)Surface.PlatformWindow,
                    };
                    return _instanceApi.vkCreateXlibSurfaceKHR(&info, null, out _surface);
                }
            case RenderSurfaceKind.Wayland:
                {
                    var info = new VkWaylandSurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.WaylandSurfaceCreateInfoKHR,
                        display = Surface.PlatformDisplay,
                        surface = Surface.PlatformWindow,
                    };
                    return _instanceApi.vkCreateWaylandSurfaceKHR(&info, null, out _surface);
                }
            case RenderSurfaceKind.Android:
                {
                    var info = new VkAndroidSurfaceCreateInfoKHR
                    {
                        sType = VkStructureType.AndroidSurfaceCreateInfoKHR,
                        window = Surface.PlatformWindow,
                    };
                    return _instanceApi.vkCreateAndroidSurfaceKHR(&info, null, out _surface);
                }
            default:
                return VkResult.ErrorExtensionNotPresent;
        }
    }

    /// <summary>Takes the first device, best type first, that has a graphics queue able to present and the swapchain extension.</summary>
    private string PickDevice()
    {
        if (_instanceApi.vkEnumeratePhysicalDevices(out uint count) != VkResult.Success || count == 0)
        {
            return "Vulkan found no physical devices.";
        }

        var devices = new VkPhysicalDevice[count];
        _instanceApi.vkEnumeratePhysicalDevices(devices);
        var ranked = devices
            .Select(device => (Device: device, Rank: RankOf(device)))
            .Where(candidate => candidate.Rank >= 0)
            .OrderBy(candidate => candidate.Rank);

        foreach (var (device, _) in ranked)
        {
            if (!TryFindPresentQueue(device, out uint family))
            {
                continue;
            }

            _physicalDevice = device;
            _queueFamily = family;
            _instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);
            DeviceName = Marshal.PtrToStringUTF8((nint)properties.deviceName) ?? string.Empty;
            return string.Empty;
        }

        return "No Vulkan device can present to this surface with a graphics queue and the swapchain extension.";
    }

    private int RankOf(VkPhysicalDevice device)
    {
        _instanceApi.vkGetPhysicalDeviceProperties(device, out VkPhysicalDeviceProperties properties);
        if (!HasDeviceExtension(device, _swapchainExtension))
        {
            return -1;
        }

        return properties.deviceType switch
        {
            VkPhysicalDeviceType.DiscreteGpu => 0,
            VkPhysicalDeviceType.IntegratedGpu => 1,
            VkPhysicalDeviceType.VirtualGpu => 2,
            VkPhysicalDeviceType.Cpu => 3,
            _ => 4,
        };
    }

    private bool TryFindPresentQueue(VkPhysicalDevice device, out uint family)
    {
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, out uint count);
        var families = new VkQueueFamilyProperties[count];
        _instanceApi.vkGetPhysicalDeviceQueueFamilyProperties(device, families);
        for (uint index = 0; index < count; index++)
        {
            if ((families[index].queueFlags & VkQueueFlags.Graphics) == 0)
            {
                continue;
            }

            if (Surface.Kind == RenderSurfaceKind.Headless
                || _instanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(device, index, _surface, out VkBool32 supported) == VkResult.Success && (bool)supported)
            {
                family = index;
                return true;
            }
        }

        family = 0;
        return false;
    }

    private bool HasDeviceExtension(VkPhysicalDevice device, string name)
    {
        if (_instanceApi.vkEnumerateDeviceExtensionProperties(device, out uint count) != VkResult.Success || count == 0)
        {
            return false;
        }

        var extensions = new VkExtensionProperties[count];
        _instanceApi.vkEnumerateDeviceExtensionProperties(device, extensions);
        return extensions.Any(extension => Marshal.PtrToStringUTF8((nint)extension.extensionName) == name);
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            sType = VkStructureType.DeviceQueueCreateInfo,
            queueFamilyIndex = _queueFamily,
            queueCount = 1,
            pQueuePriorities = &priority,
        };
        string[] extensions = [_swapchainExtension];
        using var names = new NativeStrings(extensions);
        var createInfo = new VkDeviceCreateInfo
        {
            sType = VkStructureType.DeviceCreateInfo,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueInfo,
            enabledExtensionCount = 1,
            ppEnabledExtensionNames = names.Pointers,
        };
        VkResult created = _instanceApi.vkCreateDevice(_physicalDevice, &createInfo, null, out _device);
        if (created != VkResult.Success)
        {
            throw new InvalidOperationException($"Vulkan device could not be created ({created}).");
        }

        _deviceApi = new VkDeviceApi(_instanceApi, in _device);
        _deviceApi.vkGetDeviceQueue(_queueFamily, 0, out _queue);
    }

    private void CreateFrameObjects()
    {
        var poolInfo = new VkCommandPoolCreateInfo
        {
            sType = VkStructureType.CommandPoolCreateInfo,
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = _queueFamily,
        };
        _deviceApi.vkCreateCommandPool(&poolInfo, null, out _commandPool);

        var semaphoreInfo = new VkSemaphoreCreateInfo { sType = VkStructureType.SemaphoreCreateInfo };
        _deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out _imageAvailable);

        var fenceInfo = new VkFenceCreateInfo
        {
            sType = VkStructureType.FenceCreateInfo,
            flags = VkFenceCreateFlags.Signaled,
        };
        _deviceApi.vkCreateFence(&fenceInfo, null, out _frameFence);
    }

    /// <summary>Builds the swapchain, its images and one clear command buffer per image.</summary>
    private string RebuildSwapchain()
    {
        _deviceApi.vkDeviceWaitIdle();
        DestroySwapchainObjects();

        _instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_physicalDevice, _surface, out VkSurfaceCapabilitiesKHR capabilities);
        if (capabilities.maxImageExtent.width == 0 || capabilities.maxImageExtent.height == 0)
        {
            return "The surface has no drawable area yet.";
        }

        _extent = capabilities.currentExtent.width != uint.MaxValue
            ? capabilities.currentExtent
            : new VkExtent2D(
                Math.Clamp((uint)Surface.WidthPixels, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
                Math.Clamp((uint)Surface.HeightPixels, capabilities.minImageExtent.height, capabilities.maxImageExtent.height));
        if (_extent.width == 0 || _extent.height == 0)
        {
            return "The surface has no drawable area yet.";
        }

        _format = ChooseFormat();
        if (_pipeline is null || _pipelineFormat != _format)
        {
            _pipeline?.Dispose();
            _pipeline = new NodePipeline(_instanceApi, _deviceApi, _physicalDevice, _format, VkImageLayout.PresentSrcKHR, _reversedZ);
            _pipelineFormat = _format;
            _pipeline.UploadStyles(_styleBytes);
        }

        uint imageCount = Math.Max(capabilities.minImageCount + 1, 2u);
        if (capabilities.maxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.maxImageCount);
        }

        var swapchainInfo = new VkSwapchainCreateInfoKHR
        {
            sType = VkStructureType.SwapchainCreateInfoKHR,
            surface = _surface,
            minImageCount = imageCount,
            imageFormat = _format,
            imageColorSpace = VkColorSpaceKHR.SrgbNonLinear,
            imageExtent = _extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = VkPresentModeKHR.Fifo,
            clipped = true,
            oldSwapchain = default,
        };
        VkResult created = _deviceApi.vkCreateSwapchainKHR(&swapchainInfo, null, out _swapchain);
        if (created != VkResult.Success)
        {
            return $"Vulkan swapchain could not be created ({created}).";
        }

        CreateSwapchainCommandBuffers();
        return string.Empty;
    }

    private VkFormat ChooseFormat()
    {
        _instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, out uint count);
        var formats = new VkSurfaceFormatKHR[count];
        _instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(_physicalDevice, _surface, formats);
        foreach (VkSurfaceFormatKHR format in formats)
        {
            if (format.format == VkFormat.B8G8R8A8Unorm && format.colorSpace == VkColorSpaceKHR.SrgbNonLinear)
            {
                return format.format;
            }
        }

        return formats.Length > 0 ? formats[0].format : VkFormat.B8G8R8A8Unorm;
    }

    private void CreateSwapchainCommandBuffers()
    {
        _deviceApi.vkGetSwapchainImagesKHR(_swapchain, out uint count);
        _images = new VkImage[count];
        _deviceApi.vkGetSwapchainImagesKHR(_swapchain, _images);
        CreateViewsAndFramebuffers();

        var allocate = new VkCommandBufferAllocateInfo
        {
            sType = VkStructureType.CommandBufferAllocateInfo,
            commandPool = _commandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = count,
        };
        _commandBuffers = new VkCommandBuffer[count];
        fixed (VkCommandBuffer* buffers = _commandBuffers)
        {
            _deviceApi.vkAllocateCommandBuffers(&allocate, buffers);
        }

        _renderFinished = new VkSemaphore[count];
        var semaphoreInfo = new VkSemaphoreCreateInfo { sType = VkStructureType.SemaphoreCreateInfo };
        for (int index = 0; index < count; index++)
        {
            _deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out _renderFinished[index]);
        }
    }

    /// <summary>One colour view and one framebuffer per swapchain image, for the node render pass.</summary>
    private void CreateViewsAndFramebuffers()
    {
        _imageViews = new VkImageView[_images.Length];
        _framebuffers = new VkFramebuffer[_images.Length];
        _depthTarget?.Dispose();
        _depthTarget = _reversedZ
            ? new ReversedZDepthTarget(_instanceApi, _deviceApi, _physicalDevice, _extent.width, _extent.height)
            : null;
        VkImageView* attachments = stackalloc VkImageView[2];
        attachments[1] = _depthTarget?.View ?? default;
        for (int index = 0; index < _images.Length; index++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                sType = VkStructureType.ImageViewCreateInfo,
                image = _images[index],
                viewType = VkImageViewType.Image2D,
                format = _format,
                subresourceRange = new VkImageSubresourceRange
                {
                    aspectMask = VkImageAspectFlags.Color,
                    baseMipLevel = 0,
                    levelCount = 1,
                    baseArrayLayer = 0,
                    layerCount = 1,
                },
            };
            _deviceApi.vkCreateImageView(&viewInfo, null, out _imageViews[index]);

            attachments[0] = _imageViews[index];
            var framebufferInfo = new VkFramebufferCreateInfo
            {
                sType = VkStructureType.FramebufferCreateInfo,
                renderPass = _pipeline!.RenderPass,
                attachmentCount = _reversedZ ? 2u : 1u,
                pAttachments = attachments,
                width = _extent.width,
                height = _extent.height,
                layers = 1,
            };
            _deviceApi.vkCreateFramebuffer(&framebufferInfo, null, out _framebuffers[index]);
        }
    }

    /// <summary>Records one render pass per image: clear, then the node instances when there are any.</summary>
    private void RecordFrame(uint imageIndex, (float R, float G, float B, float A) color, int instanceCount, NodeView view)
    {
        VkCommandBuffer commandBuffer = _commandBuffers[imageIndex];
        _deviceApi.vkResetCommandBuffer(commandBuffer, VkCommandBufferResetFlags.None);

        var begin = new VkCommandBufferBeginInfo
        {
            sType = VkStructureType.CommandBufferBeginInfo,
            flags = VkCommandBufferUsageFlags.OneTimeSubmit,
        };
        _deviceApi.vkBeginCommandBuffer(commandBuffer, &begin);
        _pipeline!.Record(commandBuffer, _framebuffers[imageIndex], _extent, color, instanceCount, view);
        _deviceApi.vkEndCommandBuffer(commandBuffer);
    }

    private void DestroySwapchainObjects()
    {
        foreach (VkSemaphore semaphore in _renderFinished)
        {
            if (semaphore.Handle != 0)
            {
                _deviceApi.vkDestroySemaphore(semaphore, null);
            }
        }

        _renderFinished = [];
        if (_commandBuffers.Length > 0 && _commandPool.Handle != 0)
        {
            fixed (VkCommandBuffer* buffers = _commandBuffers)
            {
                _deviceApi.vkFreeCommandBuffers(_commandPool, (uint)_commandBuffers.Length, buffers);
            }
        }

        _commandBuffers = [];
        foreach (VkFramebuffer framebuffer in _framebuffers)
        {
            if (framebuffer.Handle != 0)
            {
                _deviceApi.vkDestroyFramebuffer(framebuffer, null);
            }
        }

        foreach (VkImageView view in _imageViews)
        {
            if (view.Handle != 0)
            {
                _deviceApi.vkDestroyImageView(view, null);
            }
        }

        _framebuffers = [];
        _imageViews = [];
        _images = [];
        if (_swapchain.Handle != 0)
        {
            _deviceApi.vkDestroySwapchainKHR(_swapchain, null);
            _swapchain = default;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Null-terminated UTF-8 extension names kept alive for one Vulkan call.</summary>
    private sealed class NativeStrings : IDisposable
    {
        private readonly nint[] _handles;

        public NativeStrings(string[] values)
        {
            _handles = values.Select(value => Marshal.StringToCoTaskMemUTF8(value)).ToArray();
            Pointers = (byte**)Marshal.AllocCoTaskMem(IntPtr.Size * _handles.Length);
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
