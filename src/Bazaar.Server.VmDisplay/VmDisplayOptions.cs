using System.Globalization;
using Bazaar.Server.VmDisplay.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Bazaar.Server.VmDisplay;

/// <summary>
/// Display settings (docs/design/59, section 9: <c>vm.display.*</c>), bound from the <c>Bazaar:VmDisplay</c> section. The settings
/// registry (doc 03) is not built yet, so the host binds these through configuration. Size limits can tighten but never loosen
/// the hard ceiling in <see cref="DisplayLimits"/>.
/// </summary>
public sealed class VmDisplayOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmDisplay";

    /// <summary>Whether capture and input are available at all (<c>vm.display.enabled</c>).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Widest frame returned; larger frames are downscaled (<c>vm.display.max_width</c>).</summary>
    public int MaxWidth { get; set; } = 1920;

    /// <summary>Tallest frame returned; larger frames are downscaled (<c>vm.display.max_height</c>).</summary>
    public int MaxHeight { get; set; } = 1200;

    /// <summary>Captures per agent per minute (<c>vm.display.captures_per_minute</c>).</summary>
    public int CapturesPerMinute { get; set; } = 60;

    /// <summary>Input batches per agent per minute (<c>vm.display.input_batches_per_minute</c>).</summary>
    public int InputBatchesPerMinute { get; set; } = 120;

    /// <summary>Timeout for one guest exec in seconds (<c>vm.display.guest_capture_timeout_s</c>).</summary>
    public int GuestCaptureTimeoutSeconds { get; set; } = 10;

    /// <summary>Largest encoded frame accepted from any source, in bytes.</summary>
    public int MaxEncodedBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>Most actions in one input batch.</summary>
    public int MaxActionsPerBatch { get; set; } = 200;

    /// <summary>Longest text one <see cref="TypeText"/> may carry, in UTF-16 code units.</summary>
    public int MaxTextLength { get; set; } = 2000;

    /// <summary><see cref="MaxWidth"/> limited to the hard ceiling.</summary>
    public int EffectiveMaxWidth => Math.Min(MaxWidth, DisplayLimits.CeilingWidth);

    /// <summary><see cref="MaxHeight"/> limited to the hard ceiling.</summary>
    public int EffectiveMaxHeight => Math.Min(MaxHeight, DisplayLimits.CeilingHeight);

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> when a value is not usable.</summary>
    public void Validate()
    {
        Positive(MaxWidth, nameof(MaxWidth));
        Positive(MaxHeight, nameof(MaxHeight));
        Positive(CapturesPerMinute, nameof(CapturesPerMinute));
        Positive(InputBatchesPerMinute, nameof(InputBatchesPerMinute));
        Positive(GuestCaptureTimeoutSeconds, nameof(GuestCaptureTimeoutSeconds));
        Positive(MaxEncodedBytes, nameof(MaxEncodedBytes));
        Positive(MaxActionsPerBatch, nameof(MaxActionsPerBatch));
        Positive(MaxTextLength, nameof(MaxTextLength));
    }

    /// <summary>Reads the options from <paramref name="configuration"/> (the <see cref="SectionName"/> section) and validates them.</summary>
    public static VmDisplayOptions Bind(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IConfigurationSection s = configuration.GetSection(SectionName);
        var o = new VmDisplayOptions();
        if (bool.TryParse(s[nameof(Enabled)], out bool enabled))
            o.Enabled = enabled;
        o.MaxWidth = ReadInt(s, nameof(MaxWidth), o.MaxWidth);
        o.MaxHeight = ReadInt(s, nameof(MaxHeight), o.MaxHeight);
        o.CapturesPerMinute = ReadInt(s, nameof(CapturesPerMinute), o.CapturesPerMinute);
        o.InputBatchesPerMinute = ReadInt(s, nameof(InputBatchesPerMinute), o.InputBatchesPerMinute);
        o.GuestCaptureTimeoutSeconds = ReadInt(s, nameof(GuestCaptureTimeoutSeconds), o.GuestCaptureTimeoutSeconds);
        o.MaxEncodedBytes = ReadInt(s, nameof(MaxEncodedBytes), o.MaxEncodedBytes);
        o.MaxActionsPerBatch = ReadInt(s, nameof(MaxActionsPerBatch), o.MaxActionsPerBatch);
        o.MaxTextLength = ReadInt(s, nameof(MaxTextLength), o.MaxTextLength);
        o.Validate();
        return o;
    }

    private static int ReadInt(IConfigurationSection section, string key, int fallback) =>
        int.TryParse(section[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    private static void Positive(int value, string name)
    {
        if (value < 1)
            throw new ArgumentOutOfRangeException(name, value, "The value must be at least 1.");
    }
}
