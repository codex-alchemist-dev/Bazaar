using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Bazaar.Client.Core.Constellation;

/// <summary>
/// The Windows host for the Constellation view: a native child window that Avalonia places in the layout, so a GPU surface
/// can render into it. Only the Windows head creates it; the class compiles on every platform but is never instantiated elsewhere.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class Win32ConstellationHost : NativeControlHost
{
    private const uint _wsChild = 0x40000000;
    private const uint _wsVisible = 0x10000000;

    private nint _childHandle;

    /// <summary>The child window's HWND, or 0 while no child window exists.</summary>
    public nint ChildHandle => _childHandle;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        ArgumentNullException.ThrowIfNull(parent);

        var hwnd = NativeMethods.CreateWindowExW(
            0,
            "STATIC",
            null,
            _wsChild | _wsVisible,
            0,
            0,
            PixelOf(Bounds.Width),
            PixelOf(Bounds.Height),
            parent.Handle,
            0,
            0,
            0);
        if (hwnd == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());

        _childHandle = hwnd;
        return new PlatformHandle(hwnd, "HWND");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ArgumentNullException.ThrowIfNull(control);

        if (control.Handle != 0) NativeMethods.DestroyWindow(control.Handle);
        _childHandle = 0;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (_childHandle != 0)
            NativeMethods.MoveWindow(_childHandle, 0, 0, PixelOf(finalSize.Width), PixelOf(finalSize.Height), true);
        return result;
    }

    private int PixelOf(double dip)
    {
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return Math.Max(1, (int)(dip * scale));
    }

    internal static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        internal static partial nint CreateWindowExW(
            uint dwExStyle,
            string? lpClassName,
            string? lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool DestroyWindow(nint hWnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MoveWindow(
            nint hWnd,
            int x,
            int y,
            int nWidth,
            int nHeight,
            [MarshalAs(UnmanagedType.Bool)] bool bRepaint);
    }
}
