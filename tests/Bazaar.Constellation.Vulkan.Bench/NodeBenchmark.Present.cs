using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vortice.Vulkan;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>
/// Presentation timing of one cell, in milliseconds. Cadence is the interval between successive vkQueuePresentKHR returns. Render latency
/// is from the input sample (taken right after the swapchain image is acquired) to the CPU seeing the frame's GPU work complete.
/// The driver has no present_wait or present_timing and DwmGetCompositionTimingInfo is refused for this window, so the time the frame
/// reaches the display is not observable: the wait to the next vblank and the compositor are not included.
/// </summary>
public sealed record PresentStats(int Frames, double CadenceP50, double CadenceP95, double CadenceP99, double CadenceMax,
    double RenderLatencyP50, double RenderLatencyP99, double RenderLatencyMax,
    double AcquireWaitP50, double AcquireWaitP99);

// Section 10.13: the combined frame drawn into a real swapchain on a visible window, with a fixed present cadence.
public sealed unsafe partial class NodeBenchmark
{
    private nint _window;
    private VkSurfaceKHR _surface;
    private const int _presentSlotsMax = 3;

    public const string PresentCsvHeader =
        "section,mode,frames_in_flight,frames,cadence_p50_ms,cadence_p95_ms,cadence_p99_ms,cadence_max_ms,render_latency_p50_ms,render_latency_p99_ms,render_latency_max_ms," +
        "acquire_wait_p50_ms,acquire_wait_p99_ms";

    public static string PresentCsvLine(string section, string mode, int framesInFlight, PresentStats s)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("F4", c);
        return string.Join(',', section, mode, framesInFlight.ToString(c), s.Frames.ToString(c), F(s.CadenceP50), F(s.CadenceP95), F(s.CadenceP99), F(s.CadenceMax),
            F(s.RenderLatencyP50), F(s.RenderLatencyP99), F(s.RenderLatencyMax), F(s.AcquireWaitP50), F(s.AcquireWaitP99));
    }

    /// <summary>Creates the visible window and the surface. Needs <see cref="EnablePresent"/> before <see cref="Initialize"/>.</summary>
    public void InitializePresentSurface()
    {
        _window = WindowsNative.CreateVisibleWindow(ScreenWidth, ScreenHeight);
        var info = new VkWin32SurfaceCreateInfoKHR
        {
            sType = VkStructureType.Win32SurfaceCreateInfoKHR,
            hinstance = WindowsNative.ModuleHandle(),
            hwnd = _window,
        };
        Check(_instanceApi.vkCreateWin32SurfaceKHR(&info, null, out _surface), "vkCreateWin32SurfaceKHR");
        Check(_instanceApi.vkGetPhysicalDeviceSurfaceSupportKHR(_physical, _queueFamily, _surface, out VkBool32 supported), "vkGetPhysicalDeviceSurfaceSupportKHR");
        if (!(bool)supported)
        {
            throw new InvalidOperationException("The graphics queue family cannot present to the window surface.");
        }
    }

    /// <summary>Present modes the surface offers.</summary>
    public VkPresentModeKHR[] PresentModes()
    {
        _instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(_physical, _surface, out uint count);
        var modes = new VkPresentModeKHR[count];
        _instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(_physical, _surface, modes);
        return modes;
    }

    /// <summary>
    /// Draws the points variant of the combined frame (cull, decimation, points, 100k edges) straight into swapchain images and presents each
    /// one, with <paramref name="framesInFlight"/> frames queued. The swapchain is created for <paramref name="mode"/> and destroyed afterwards.
    /// Needs <see cref="BuildTiles"/> for the same topology, dimension, N and point count.
    /// </summary>
    public PresentStats MeasurePresented(Topology topology, int dim, long n, Viewpoint view, int points, int edges, VkPresentModeKHR mode, int framesInFlight,
        int warmup, int measured, float lodMinPixels, float lodMaxPixels)
    {
        _instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(_physical, _surface, out VkSurfaceCapabilitiesKHR capabilities);
        uint imageCount = Math.Max(capabilities.minImageCount + 1, (uint)framesInFlight + 1);
        if (capabilities.maxImageCount != 0)
        {
            imageCount = Math.Min(imageCount, capabilities.maxImageCount);
        }

        var swapchainInfo = new VkSwapchainCreateInfoKHR
        {
            sType = VkStructureType.SwapchainCreateInfoKHR,
            surface = _surface,
            minImageCount = imageCount,
            imageFormat = VkFormat.R8G8B8A8Unorm,
            imageColorSpace = VkColorSpaceKHR.SrgbNonLinear,
            imageExtent = new VkExtent2D(_width, _height),
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = mode,
            clipped = true,
        };
        Trace("pre-swapchain");
        Check(_deviceApi.vkCreateSwapchainKHR(&swapchainInfo, null, out VkSwapchainKHR swapchain), "vkCreateSwapchainKHR");
        _deviceApi.vkGetSwapchainImagesKHR(swapchain, out uint count);
        var images = new VkImage[count];
        _deviceApi.vkGetSwapchainImagesKHR(swapchain, images);
        var views = new VkImageView[count];
        var renderDone = new VkSemaphore[count];
        var semaphoreInfo = new VkSemaphoreCreateInfo { sType = VkStructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new VkImageViewCreateInfo
            {
                sType = VkStructureType.ImageViewCreateInfo,
                image = images[i],
                viewType = VkImageViewType.Image2D,
                format = VkFormat.R8G8B8A8Unorm,
                subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
            };
            Check(_deviceApi.vkCreateImageView(&viewInfo, null, out views[i]), "vkCreateImageView(swapchain)");
            Check(_deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out renderDone[i]), "vkCreateSemaphore(render)");
        }

        var buffers = new VkCommandBuffer[framesInFlight];
        var fences = new VkFence[framesInFlight];
        var acquired = new VkSemaphore[framesInFlight];
        var allocate = new VkCommandBufferAllocateInfo { sType = VkStructureType.CommandBufferAllocateInfo, commandPool = _commandPool, level = VkCommandBufferLevel.Primary, commandBufferCount = (uint)framesInFlight };
        fixed (VkCommandBuffer* pointer = buffers)
        {
            Check(_deviceApi.vkAllocateCommandBuffers(&allocate, pointer), "vkAllocateCommandBuffers(present)");
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            var fenceInfo = new VkFenceCreateInfo { sType = VkStructureType.FenceCreateInfo };
            Check(_deviceApi.vkCreateFence(&fenceInfo, null, out fences[slot]), "vkCreateFence(present)");
            Check(_deviceApi.vkCreateSemaphore(&semaphoreInfo, null, out acquired[slot]), "vkCreateSemaphore(acquire)");
        }

        Trace("post-views");
        double ticksPerMs = Stopwatch.Frequency / 1000.0;

        int total = warmup + measured;
        var inputAt = new long[total];
        var acquireStart = new long[total];
        var presentReturn = new long[total];
        var gpuDone = new long[total];
        var done = new ManualResetEventSlim[total];
        for (int i = 0; i < total; i++)
        {
            done[i] = new ManualResetEventSlim(false);
        }

        // One waiter thread observes when each frame's GPU work completes, so completion time does not depend on when the main thread looks.
        var waitQueue = new System.Collections.Concurrent.BlockingCollection<(int Frame, VkFence Fence)>();
        var waiter = new Thread(() =>
        {
            foreach ((int frame, VkFence fence) in waitQueue.GetConsumingEnumerable())
            {
                VkFence local = fence;
                _deviceApi.vkWaitForFences(1, &local, true, _fenceTimeoutNs * 4);
                gpuDone[frame] = Stopwatch.GetTimestamp();
                done[frame].Set();
            }
        })
        { IsBackground = true };
        waiter.Start();

        Trace("pre-loop");
        TopoPush push = MakeTopoPush(topology, dim, n, view);
        push.Stride = (uint)Math.Max(1, n / points);
        push.B = (uint)points;
        push.A = 0;
        var slotFrame = new int[framesInFlight];
        Array.Fill(slotFrame, -1);
        VkImageView savedView = _imageView;
        for (int frame = 0; frame < total; frame++)
        {
            int slot = frame % framesInFlight;
            if (slotFrame[slot] >= 0)
            {
                done[slotFrame[slot]].Wait();
                VkFence reset = fences[slot];
                _deviceApi.vkResetFences(1, &reset);
            }

            WindowsNative.Pump();
            if (frame < 5 && Environment.GetEnvironmentVariable("BAZAAR_PRESENT_TRACE") == "1")
            {
                Console.WriteLine($"frame {frame} slot {slot}");
            }

            acquireStart[frame] = Stopwatch.GetTimestamp();
            VkResult acquire = _deviceApi.vkAcquireNextImageKHR(swapchain, ulong.MaxValue, acquired[slot], default, out uint imageIndex);
            if (acquire is not (VkResult.Success or VkResult.SuboptimalKHR))
            {
                throw new InvalidOperationException($"vkAcquireNextImageKHR failed with {acquire}.");
            }

            inputAt[frame] = Stopwatch.GetTimestamp();
            VkCommandBuffer cb = buffers[slot];
            Check(_deviceApi.vkResetCommandBuffer(cb, 0), "vkResetCommandBuffer(present)");
            var begin = new VkCommandBufferBeginInfo { sType = VkStructureType.CommandBufferBeginInfo, flags = VkCommandBufferUsageFlags.OneTimeSubmit };
            Check(_deviceApi.vkBeginCommandBuffer(cb, &begin), "vkBeginCommandBuffer(present)");
            ImageBarrier(cb, images[imageIndex], VkImageLayout.Undefined, VkImageLayout.ColorAttachmentOptimal, VkPipelineStageFlags.ColorAttachmentOutput,
                VkPipelineStageFlags.ColorAttachmentOutput, 0, VkAccessFlags.ColorAttachmentWrite);
            _imageView = views[imageIndex];
            RecordCombinedPart(cb, push, 0, 1, n, edges, lodMinPixels, lodMaxPixels);
            ImageBarrier(cb, images[imageIndex], VkImageLayout.ColorAttachmentOptimal, VkImageLayout.PresentSrcKHR, VkPipelineStageFlags.ColorAttachmentOutput,
                VkPipelineStageFlags.BottomOfPipe, VkAccessFlags.ColorAttachmentWrite, 0);
            Check(_deviceApi.vkEndCommandBuffer(cb), "vkEndCommandBuffer(present)");

            VkSemaphore waitSemaphore = acquired[slot];
            VkSemaphore signal = renderDone[imageIndex];
            var waitStage = VkPipelineStageFlags.ColorAttachmentOutput;
            var submit = new VkSubmitInfo
            {
                sType = VkStructureType.SubmitInfo,
                waitSemaphoreCount = 1,
                pWaitSemaphores = &waitSemaphore,
                pWaitDstStageMask = &waitStage,
                commandBufferCount = 1,
                pCommandBuffers = &cb,
                signalSemaphoreCount = 1,
                pSignalSemaphores = &signal,
            };
            Check(_deviceApi.vkQueueSubmit(_queue, 1, &submit, fences[slot]), "vkQueueSubmit(present)");
            slotFrame[slot] = frame;
            waitQueue.Add((frame, fences[slot]));

            VkSwapchainKHR chain = swapchain;
            var present = new VkPresentInfoKHR
            {
                sType = VkStructureType.PresentInfoKHR,
                waitSemaphoreCount = 1,
                pWaitSemaphores = &signal,
                swapchainCount = 1,
                pSwapchains = &chain,
                pImageIndices = &imageIndex,
            };
            VkResult presented = _deviceApi.vkQueuePresentKHR(_queue, &present);
            presentReturn[frame] = Stopwatch.GetTimestamp();
            if (presented is not (VkResult.Success or VkResult.SuboptimalKHR))
            {
                throw new InvalidOperationException($"vkQueuePresentKHR failed with {presented}.");
            }
        }

        _imageView = savedView;
        for (int i = 0; i < total; i++)
        {
            done[i].Wait();
        }

        waitQueue.CompleteAdding();
        _deviceApi.vkDeviceWaitIdle();
        var cadence = new double[measured];
        var render = new double[measured];
        var acquireWait = new double[measured];
        for (int i = 0; i < measured; i++)
        {
            int frame = warmup + i;
            cadence[i] = (presentReturn[frame] - presentReturn[frame - 1]) / ticksPerMs;
            render[i] = (gpuDone[frame] - inputAt[frame]) / ticksPerMs;
            acquireWait[i] = (inputAt[frame] - acquireStart[frame]) / ticksPerMs;
        }

        for (int slot = 0; slot < framesInFlight; slot++)
        {
            _deviceApi.vkDestroyFence(fences[slot], null);
            _deviceApi.vkDestroySemaphore(acquired[slot], null);
        }

        for (int i = 0; i < count; i++)
        {
            _deviceApi.vkDestroyImageView(views[i], null);
            _deviceApi.vkDestroySemaphore(renderDone[i], null);
        }

        fixed (VkCommandBuffer* pointer = buffers)
        {
            _deviceApi.vkFreeCommandBuffers(_commandPool, (uint)framesInFlight, pointer);
        }

        _deviceApi.vkDestroySwapchainKHR(swapchain, null);
        return new PresentStats(measured, Percentile(cadence, 0.5), Percentile(cadence, 0.95), Percentile(cadence, 0.99), cadence.Max(),
            Percentile(render, 0.5), Percentile(render, 0.99), render.Max(), Percentile(acquireWait, 0.5), Percentile(acquireWait, 0.99));
    }

    private static void Trace(string text)
    {
        if (Environment.GetEnvironmentVariable("BAZAAR_PRESENT_TRACE") == "1")
        {
            Console.WriteLine(text);
        }
    }

    private void ImageBarrier(VkCommandBuffer cb, VkImage image, VkImageLayout oldLayout, VkImageLayout newLayout, VkPipelineStageFlags source,
        VkPipelineStageFlags destination, VkAccessFlags sourceAccess, VkAccessFlags destinationAccess)
    {
        var barrier = new VkImageMemoryBarrier
        {
            sType = VkStructureType.ImageMemoryBarrier,
            srcAccessMask = sourceAccess,
            dstAccessMask = destinationAccess,
            oldLayout = oldLayout,
            newLayout = newLayout,
            srcQueueFamilyIndex = uint.MaxValue,
            dstQueueFamilyIndex = uint.MaxValue,
            image = image,
            subresourceRange = new VkImageSubresourceRange(VkImageAspectFlags.Color, 0, 1, 0, 1),
        };
        _deviceApi.vkCmdPipelineBarrier(cb, source, destination, default, 0, null, 0, null, 1, &barrier);
    }
}
