using System.Runtime.InteropServices;

namespace Bazaar.Constellation.Vulkan.Tests;

/// <summary>
/// A hidden, borderless Win32 window for swapchain tests. It uses the built-in STATIC window class, so no window
/// procedure or class registration is needed. Windows only; callers must check <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
internal sealed partial class NativeTestWindow : IDisposable
{
    private const uint _wsPopup = 0x80000000;

    private NativeTestWindow(nint instance, nint handle)
    {
        Instance = instance;
        Handle = handle;
    }

    public nint Instance { get; }

    public nint Handle { get; }

    public static NativeTestWindow Create(int width, int height)
    {
        nint instance = GetModuleHandleW(0);
        nint handle = CreateWindowExW(0, "STATIC", "Bazaar Vulkan test", _wsPopup, 0, 0, width, height, 0, 0, instance, 0);
        if (handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW failed (error {Marshal.GetLastPInvokeError()}).");
        }

        return new NativeTestWindow(instance, handle);
    }

    public void Dispose()
    {
        if (Handle != 0)
        {
            _ = DestroyWindow(Handle);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);
}
