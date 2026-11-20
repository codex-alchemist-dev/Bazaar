using System.Globalization;
using Bazaar.Server.VmDensity.Abstractions;

namespace Bazaar.Server.VmDensity.HostProbes;

/// <summary>Values read from <c>/proc/meminfo</c>, in bytes.</summary>
/// <param name="MemTotalBytes">MemTotal.</param>
/// <param name="MemAvailableBytes">MemAvailable (the kernel's estimate of memory available without swapping).</param>
/// <param name="SwapTotalBytes">SwapTotal.</param>
/// <param name="SwapFreeBytes">SwapFree.</param>
public sealed record MeminfoValues(long MemTotalBytes, long MemAvailableBytes, long SwapTotalBytes, long SwapFreeBytes);

/// <summary>Memory pressure (PSI) averages over the last 10 seconds, in percent of time stalled.</summary>
/// <param name="SomeAvg10">Share of time at least one task stalled on memory.</param>
/// <param name="FullAvg10">Share of time all non-idle tasks stalled on memory.</param>
public sealed record MemoryPressure(double SomeAvg10, double FullAvg10);

/// <summary>Pure parsers for the Linux memory files. Every malformed input throws <see cref="HostMemoryProbeException"/>.</summary>
public static class LinuxMemoryParser
{
    private const long _bytesPerKb = 1024;

    /// <summary>Parses the text of <c>/proc/meminfo</c>.</summary>
    /// <param name="text">The file content.</param>
    public static MeminfoValues ParseMeminfo(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Dictionary<string, long> found = new(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            if (key is not ("MemTotal" or "MemAvailable" or "SwapTotal" or "SwapFree"))
            {
                continue;
            }

            if (found.ContainsKey(key))
            {
                throw new HostMemoryProbeException($"meminfo field {key} appears more than once.");
            }

            found[key] = ParseKbToBytes(key, line[(colon + 1)..]);
        }

        long total = Require(found, "MemTotal");
        long available = Require(found, "MemAvailable");
        long swapTotal = Require(found, "SwapTotal");
        long swapFree = Require(found, "SwapFree");
        if (total <= 0)
        {
            throw new HostMemoryProbeException("meminfo MemTotal is zero.");
        }

        if (available > total)
        {
            throw new HostMemoryProbeException("meminfo MemAvailable exceeds MemTotal.");
        }

        if (swapFree > swapTotal)
        {
            throw new HostMemoryProbeException("meminfo SwapFree exceeds SwapTotal.");
        }

        return new MeminfoValues(total, available, swapTotal, swapFree);
    }

    /// <summary>Parses the text of <c>/proc/pressure/memory</c>.</summary>
    /// <param name="text">The file content.</param>
    public static MemoryPressure ParsePressure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        double? some = null;
        double? full = null;
        foreach (string line in text.Split('\n'))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts[0] is not ("some" or "full"))
            {
                continue;
            }

            double avg10 = ParseAvg10(parts);
            if (parts[0] == "some")
            {
                some = some is null ? avg10 : throw new HostMemoryProbeException("pressure line 'some' appears more than once.");
            }
            else
            {
                full = full is null ? avg10 : throw new HostMemoryProbeException("pressure line 'full' appears more than once.");
            }
        }

        if (some is null || full is null)
        {
            throw new HostMemoryProbeException("pressure data lacks a 'some' or 'full' line.");
        }

        return new MemoryPressure(some.Value, full.Value);
    }

    /// <summary>Maps meminfo to the governor's reading: total and available memory, floored to whole MiB.</summary>
    /// <param name="values">Parsed meminfo.</param>
    public static HostMemory ToHostMemory(MeminfoValues values)
    {
        ArgumentNullException.ThrowIfNull(values);
        const long mib = 1024 * 1024;
        return new HostMemory(values.MemTotalBytes / mib, values.MemAvailableBytes / mib);
    }

    private static long Require(Dictionary<string, long> found, string key) =>
        found.TryGetValue(key, out long value) ? value : throw new HostMemoryProbeException($"meminfo lacks {key}.");

    private static long ParseKbToBytes(string key, string rest)
    {
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !string.Equals(parts[1], "kB", StringComparison.Ordinal))
        {
            throw new HostMemoryProbeException($"meminfo field {key} is not '<number> kB'.");
        }

        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long kb))
        {
            throw new HostMemoryProbeException($"meminfo field {key} has an invalid number.");
        }

        try
        {
            return checked(kb * _bytesPerKb);
        }
        catch (OverflowException ex)
        {
            throw new HostMemoryProbeException($"meminfo field {key} overflows.", ex);
        }
    }

    private static double ParseAvg10(string[] parts)
    {
        const string prefix = "avg10=";
        string? token = parts.FirstOrDefault(p => p.StartsWith(prefix, StringComparison.Ordinal));
        if (token is null
            || !double.TryParse(token.AsSpan(prefix.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value)
            || value is < 0 or > 100)
        {
            throw new HostMemoryProbeException($"pressure line '{parts[0]}' has no valid avg10.");
        }

        return value;
    }
}
