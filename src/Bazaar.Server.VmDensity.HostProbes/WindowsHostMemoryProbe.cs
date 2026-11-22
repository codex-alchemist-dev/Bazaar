using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.HostProbes;

/// <summary>Physical memory in bytes as Windows reports it.</summary>
/// <param name="TotalPhysBytes">ullTotalPhys.</param>
/// <param name="AvailPhysBytes">ullAvailPhys.</param>
internal readonly record struct WindowsMemoryStatus(ulong TotalPhysBytes, ulong AvailPhysBytes);

/// <summary>The native call behind a seam so the mapping is testable without Windows.</summary>
internal interface IWindowsMemoryStatusSource
{
    /// <summary>Reads physical memory, or throws <see cref="HostMemoryProbeException"/>.</summary>
    WindowsMemoryStatus Read();
}

/// <summary>Reads host memory with <c>GlobalMemoryStatusEx</c>. Free memory is available physical memory. A failed call throws <see cref="HostMemoryProbeException"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsHostMemoryProbe : IHostMemoryProbe
{
    private readonly IWindowsMemoryStatusSource _source;

    /// <summary>Creates a probe over the real Windows call.</summary>
    public WindowsHostMemoryProbe()
        : this(new NativeSource())
    {
    }

    internal WindowsHostMemoryProbe(IWindowsMemoryStatusSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    /// <inheritdoc />
    public Task<HostMemory> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Map(_source.Read()));
    }

    /// <summary>Maps the native values to MiB, flooring. Zero total or available above total is malformed.</summary>
    internal static HostMemory Map(WindowsMemoryStatus status)
    {
        const ulong mib = 1024UL * 1024UL;
        if (status.TotalPhysBytes == 0 || status.AvailPhysBytes > status.TotalPhysBytes)
        {
            throw new HostMemoryProbeException("GlobalMemoryStatusEx returned inconsistent physical memory.");
        }

        return new HostMemory(checked((long)(status.TotalPhysBytes / mib)), checked((long)(status.AvailPhysBytes / mib)));
    }

    private sealed class NativeSource : IWindowsMemoryStatusSource
    {
        public WindowsMemoryStatus Read()
        {
            MemoryStatusEx status = new() { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                throw new HostMemoryProbeException("GlobalMemoryStatusEx failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }

            return new WindowsMemoryStatus(status.ullTotalPhys, status.ullAvailPhys);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryStatusEx
{
    public uint dwLength;
    public uint dwMemoryLoad;
    public ulong ullTotalPhys;
    public ulong ullAvailPhys;
    public ulong ullTotalPageFile;
    public ulong ullAvailPageFile;
    public ulong ullTotalVirtual;
    public ulong ullAvailVirtual;
    public ulong ullAvailExtendedVirtual;
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
