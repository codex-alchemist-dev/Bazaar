using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bazaar.Constellation.Vulkan.Bench;

/// <summary>Windows-only helpers of the contention and presentation benchmarks: GPU scheduling class (D3DKMT), a visible window.</summary>
internal static partial class WindowsNative
{
    private const uint _wsPopup = 0x80000000;
    private const uint _wsVisible = 0x10000000;

    /// <summary>D3DKMT_SCHEDULINGPRIORITYCLASS: 0 idle, 1 below normal, 2 normal, 3 above normal, 4 high, 5 realtime.</summary>
    public static readonly string[] ClassNames = ["idle", "belownormal", "normal", "abovenormal", "high", "realtime"];

    /// <summary>
    /// Reads BAZAAR_BENCH_D3DKMT_CLASS (idle, belownormal, normal, abovenormal, high, realtime) and sets the GPU scheduling priority class of
    /// this process. Returns a one-line result with the NTSTATUS, or an empty string when the variable is not set.
    /// </summary>
    public static string ApplySchedulingClassFromEnvironment()
    {
        string requested = (Environment.GetEnvironmentVariable("BAZAAR_BENCH_D3DKMT_CLASS") ?? string.Empty).ToLowerInvariant();
        if (requested.Length == 0 || !OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        int index = Array.IndexOf(ClassNames, requested);
        if (index < 0)
        {
            return $"D3DKMT class: unknown value {requested}";
        }

        int status = D3DKMTSetProcessSchedulingPriorityClass(Process.GetCurrentProcess().Handle, index);
        return string.Create(CultureInfo.InvariantCulture, $"D3DKMT class {requested}: NTSTATUS 0x{status:X8} ({(status == 0 ? "granted" : "refused")})");
    }

    public static nint CreateVisibleWindow(int width, int height)
    {
        nint instance = GetModuleHandleW(0);
        nint handle = CreateWindowExW(0, "STATIC", "Bazaar present benchmark", _wsPopup | _wsVisible, 100, 100, width, height, 0, 0, instance, 0);
        if (handle == 0)
        {
            throw new InvalidOperationException($"CreateWindowExW failed (error {Marshal.GetLastPInvokeError()}).");
        }

        return handle;
    }

    public static nint ModuleHandle() => GetModuleHandleW(0);

    public static void DestroyWindowHandle(nint handle) => _ = DestroyWindow(handle);

    /// <summary>Drains the window message queue so Windows does not mark the window as not responding.</summary>
    public static void Pump()
    {
        Span<byte> message = stackalloc byte[64];
        for (int i = 0; i < 64 && PeekMessageW(ref MemoryMarshal.GetReference(message), 0, 0, 0, 1); i++)
        {
            _ = DispatchMessageW(ref MemoryMarshal.GetReference(message));
        }
    }

    [LibraryImport("gdi32.dll")]
    private static partial int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priorityClass);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessageW(ref byte message);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(ref byte message, nint window, uint filterMin, uint filterMax, uint remove);
}
