using Bazaar.Server.BrowserPool.Abstractions;

namespace Bazaar.Server.BrowserPool.Display;

/// <summary>Settings for <see cref="PoolBrowserPageDriver"/>. The shipped defaults are safe; every limit may be tightened.</summary>
public sealed class BrowserPageDriverOptions
{
    /// <summary>Room, in bytes, for the JSON around a screenshot's base64 text.</summary>
    internal const int EnvelopeSlack = 64 * 1024;

    /// <summary>How long one DevTools command may take before it fails. Default 10 seconds.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many commands may wait for a reply on one session at once. Default 32.</summary>
    public int MaxPendingRequests { get; set; } = 32;

    /// <summary>The largest screenshot, in decoded bytes, that is accepted. Checked on the base64 text before decoding. Default 32 MiB.</summary>
    public int MaxScreenshotBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>The most actions one input batch may hold. Default 200 (the display module's own limit).</summary>
    public int MaxActionsPerBatch { get; set; } = 200;

    /// <summary>The most characters one <c>TypeText</c> may hold. Default 2000 (the display module's own limit).</summary>
    public int MaxTextLength { get; set; } = 2000;

    /// <summary>The largest CSS-pixel coordinate accepted for a pointer action. Default 16384.</summary>
    public int MaxCoordinate { get; set; } = 16384;

    /// <summary>The largest wheel delta accepted, in CSS pixels. Default 10000 (the display module's own limit).</summary>
    public int MaxScrollDelta { get; set; } = 10_000;

    /// <summary>The largest allowed <see cref="MaxScreenshotBytes"/>: what base64 text still fits the hard framing limit.</summary>
    internal static int MaxScreenshotLimit => (DevToolsFraming.HardMaxMessageBytes - EnvelopeSlack) / 4 * 3;

    /// <summary>The longest reply the reader accepts: the base64 of the largest screenshot plus the envelope.</summary>
    internal int MaxMessageBytes => (int)Math.Min(DevToolsFraming.HardMaxMessageBytes, Base64Length(MaxScreenshotBytes) + EnvelopeSlack);

    /// <summary>Throws when a value is out of range.</summary>
    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), "The command timeout must be above zero and at most 5 minutes.");
        }

        Range(MaxPendingRequests, 1, 1024, nameof(MaxPendingRequests));
        Range(MaxScreenshotBytes, 1, MaxScreenshotLimit, nameof(MaxScreenshotBytes));
        Range(MaxActionsPerBatch, 1, 10_000, nameof(MaxActionsPerBatch));
        Range(MaxTextLength, 1, 1_000_000, nameof(MaxTextLength));
        Range(MaxCoordinate, 1, 1_000_000, nameof(MaxCoordinate));
        Range(MaxScrollDelta, 1, 1_000_000, nameof(MaxScrollDelta));
    }

    /// <summary>The length of the base64 text of <paramref name="decoded"/> bytes.</summary>
    internal static long Base64Length(long decoded) => (decoded + 2) / 3 * 4;

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max)
        {
            throw new ArgumentOutOfRangeException(name, $"{name} must be {min} to {max}.");
        }
    }
}
