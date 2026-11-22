using System.Text;
using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.HostProbes;

/// <summary>One Linux reading: the governor's view plus swap and pressure for the status panel.</summary>
/// <param name="Memory">Total and available memory.</param>
/// <param name="SwapTotalBytes">Swap size.</param>
/// <param name="SwapFreeBytes">Free swap.</param>
/// <param name="Pressure">PSI memory pressure, or null when the kernel does not expose it.</param>
public sealed record LinuxMemorySample(HostMemory Memory, long SwapTotalBytes, long SwapFreeBytes, MemoryPressure? Pressure);

/// <summary>
/// Reads host memory from <c>/proc/meminfo</c>. Free memory is MemAvailable. A missing or malformed
/// meminfo throws <see cref="HostMemoryProbeException"/>, which the governor records as probe-failed. A missing
/// PSI file is normal (older kernels, PSI disabled) and gives no pressure value; a malformed one throws.
/// </summary>
public sealed class LinuxHostMemoryProbe : IHostMemoryProbe
{
    private const int _maxFileBytes = 64 * 1024;
    private readonly string _meminfoPath;
    private readonly string _pressurePath;

    /// <summary>Creates a probe over a procfs root.</summary>
    /// <param name="procRoot">The procfs directory; <c>/proc</c> in production, a temp directory in tests.</param>
    public LinuxHostMemoryProbe(string procRoot = "/proc")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(procRoot);
        _meminfoPath = Path.Combine(procRoot, "meminfo");
        _pressurePath = Path.Combine(procRoot, "pressure", "memory");
    }

    /// <inheritdoc />
    public async Task<HostMemory> ReadAsync(CancellationToken cancellationToken) =>
        (await ReadDetailedAsync(cancellationToken)).Memory;

    /// <summary>Reads memory, swap and pressure.</summary>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<LinuxMemorySample> ReadDetailedAsync(CancellationToken cancellationToken)
    {
        string meminfo = await ReadBoundedAsync(_meminfoPath, cancellationToken)
            ?? throw new HostMemoryProbeException("meminfo is not available.");
        MeminfoValues values = LinuxMemoryParser.ParseMeminfo(meminfo);

        string? pressureText = await ReadBoundedAsync(_pressurePath, cancellationToken);
        MemoryPressure? pressure = pressureText is null ? null : LinuxMemoryParser.ParsePressure(pressureText);
        return new LinuxMemorySample(LinuxMemoryParser.ToHostMemory(values), values.SwapTotalBytes, values.SwapFreeBytes, pressure);
    }

    // Returns null when the file does not exist. Reads at most _maxFileBytes; a larger file is malformed.
    private static async Task<string?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, useAsync: true);
            byte[] buffer = new byte[_maxFileBytes + 1];
            int total = 0;
            while (total < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > _maxFileBytes)
            {
                throw new HostMemoryProbeException($"{Path.GetFileName(path)} is larger than {_maxFileBytes} bytes.");
            }

            return Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HostMemoryProbeException($"{Path.GetFileName(path)} could not be read.", ex);
        }
    }
}
