using Microsoft.Extensions.Options;

namespace Bazaar.Server.VmBench.Display;

/// <summary>
/// Pass criteria for the display scenarios (doc 59 section 10: fixed before the first run and recorded with the report). Config
/// section <c>Bazaar:VmBench:Display</c>. Doc 59 gives no numbers, so every default below is PROVISIONAL: it stands until the first
/// measured run on the owner's host (spike S-DSP) and the operator may set any of them. A run copies these into a
/// <see cref="DisplayBenchCriteriaSnapshot"/> at its start, so changing them later never affects a run in progress.
/// </summary>
public sealed class DisplayBenchCriteria
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmBench:Display";

    /// <summary>Seconds to wait after a state change before reading memory. Provisional. 0 to 600.</summary>
    public int SettleSeconds { get; set; } = 30;

    /// <summary>Largest guest memory in use with the kiosk up and no browser, in MiB. Provisional.</summary>
    public double DesktopIdleMaxMiB { get; set; } = 2048;

    /// <summary>Largest memory rise from one headless tab, in MiB. Provisional.</summary>
    public double BrowserHeadlessMaxDeltaMiB { get; set; } = 600;

    /// <summary>Largest memory rise from one headed tab, in MiB. Provisional.</summary>
    public double BrowserHeadedMaxDeltaMiB { get; set; } = 900;

    /// <summary>Captures per source. Provisional. 1 to 1000.</summary>
    public int ScreenshotSamples { get; set; } = 20;

    /// <summary>Largest 95th-percentile capture time for the hypervisor source, in ms. Provisional.</summary>
    public double ScreenshotHypervisorP95MaxMs { get; set; } = 500;

    /// <summary>Largest 95th-percentile capture time for the guest source, in ms. Provisional.</summary>
    public double ScreenshotGuestP95MaxMs { get; set; } = 3000;

    /// <summary>Largest 95th-percentile capture time for the browser source, in ms. Provisional.</summary>
    public double ScreenshotBrowserP95MaxMs { get; set; } = 1000;

    /// <summary>Largest single frame of any source, in bytes. Provisional.</summary>
    public long ScreenshotMaxFrameBytes { get; set; } = 8L * 1024 * 1024;

    /// <summary>Input actions per source. Provisional. 1 to 1000.</summary>
    public int InputSamples { get; set; } = 20;

    /// <summary>Largest 95th-percentile input acknowledgement time for the hypervisor source, in ms. Provisional.</summary>
    public double InputHypervisorP95MaxMs { get; set; } = 250;

    /// <summary>Largest 95th-percentile input acknowledgement time for the guest source, in ms. Provisional.</summary>
    public double InputGuestP95MaxMs { get; set; } = 1500;

    /// <summary>Largest 95th-percentile input acknowledgement time for the browser source, in ms. Provisional.</summary>
    public double InputBrowserP95MaxMs { get; set; } = 250;

    /// <summary>Fewest pairs the governor must admit before it queues. Provisional. At least 1.</summary>
    public int DensityMinPairs { get; set; } = 2;

    /// <summary>Pairs after which the ramp stops even if the governor never queued. Provisional. At least <see cref="DensityMinPairs"/>.</summary>
    public int DensityMaxPairs { get; set; } = 16;

    /// <summary>Memory the ramp asks for per Windows VM, in MiB. Provisional.</summary>
    public int DensityWindowsVmMemoryMb { get; set; } = 4096;

    /// <summary>Memory the ramp asks for per Linux VM, in MiB. Provisional.</summary>
    public int DensityLinuxVmMemoryMb { get; set; } = 2048;

    /// <summary>Seconds an admission may stay pending before the ramp treats the governor as queueing. Provisional. 1 to 600.</summary>
    public int DensityQueueDetectSeconds { get; set; } = 2;
}

/// <summary>The criteria as they were when a run started. Immutable; this is what scenarios, verdicts and the report use.</summary>
public sealed record DisplayBenchCriteriaSnapshot
{
    /// <summary>Copies the current values.</summary>
    public static DisplayBenchCriteriaSnapshot From(DisplayBenchCriteria c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new DisplayBenchCriteriaSnapshot
        {
            SettleSeconds = c.SettleSeconds,
            DesktopIdleMaxMiB = c.DesktopIdleMaxMiB,
            BrowserHeadlessMaxDeltaMiB = c.BrowserHeadlessMaxDeltaMiB,
            BrowserHeadedMaxDeltaMiB = c.BrowserHeadedMaxDeltaMiB,
            ScreenshotSamples = c.ScreenshotSamples,
            ScreenshotHypervisorP95MaxMs = c.ScreenshotHypervisorP95MaxMs,
            ScreenshotGuestP95MaxMs = c.ScreenshotGuestP95MaxMs,
            ScreenshotBrowserP95MaxMs = c.ScreenshotBrowserP95MaxMs,
            ScreenshotMaxFrameBytes = c.ScreenshotMaxFrameBytes,
            InputSamples = c.InputSamples,
            InputHypervisorP95MaxMs = c.InputHypervisorP95MaxMs,
            InputGuestP95MaxMs = c.InputGuestP95MaxMs,
            InputBrowserP95MaxMs = c.InputBrowserP95MaxMs,
            DensityMinPairs = c.DensityMinPairs,
            DensityMaxPairs = c.DensityMaxPairs,
            DensityWindowsVmMemoryMb = c.DensityWindowsVmMemoryMb,
            DensityLinuxVmMemoryMb = c.DensityLinuxVmMemoryMb,
            DensityQueueDetectSeconds = c.DensityQueueDetectSeconds,
        };
    }

    /// <summary>See <see cref="DisplayBenchCriteria.SettleSeconds"/>.</summary>
    public int SettleSeconds { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DesktopIdleMaxMiB"/>.</summary>
    public double DesktopIdleMaxMiB { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.BrowserHeadlessMaxDeltaMiB"/>.</summary>
    public double BrowserHeadlessMaxDeltaMiB { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.BrowserHeadedMaxDeltaMiB"/>.</summary>
    public double BrowserHeadedMaxDeltaMiB { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.ScreenshotSamples"/>.</summary>
    public int ScreenshotSamples { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.ScreenshotHypervisorP95MaxMs"/>.</summary>
    public double ScreenshotHypervisorP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.ScreenshotGuestP95MaxMs"/>.</summary>
    public double ScreenshotGuestP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.ScreenshotBrowserP95MaxMs"/>.</summary>
    public double ScreenshotBrowserP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.ScreenshotMaxFrameBytes"/>.</summary>
    public long ScreenshotMaxFrameBytes { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.InputSamples"/>.</summary>
    public int InputSamples { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.InputHypervisorP95MaxMs"/>.</summary>
    public double InputHypervisorP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.InputGuestP95MaxMs"/>.</summary>
    public double InputGuestP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.InputBrowserP95MaxMs"/>.</summary>
    public double InputBrowserP95MaxMs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DensityMinPairs"/>.</summary>
    public int DensityMinPairs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DensityMaxPairs"/>.</summary>
    public int DensityMaxPairs { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DensityWindowsVmMemoryMb"/>.</summary>
    public int DensityWindowsVmMemoryMb { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DensityLinuxVmMemoryMb"/>.</summary>
    public int DensityLinuxVmMemoryMb { get; init; }

    /// <summary>See <see cref="DisplayBenchCriteria.DensityQueueDetectSeconds"/>.</summary>
    public int DensityQueueDetectSeconds { get; init; }
}

/// <summary>Validates <see cref="DisplayBenchCriteria"/> at start-up and again at the start of every run.</summary>
public sealed class DisplayBenchCriteriaValidator : IValidateOptions<DisplayBenchCriteria>
{
    /// <summary>Returns every problem found; empty means valid.</summary>
    public static IReadOnlyList<string> Check(DisplayBenchCriteria c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var errors = new List<string>();
        Range(errors, nameof(c.SettleSeconds), c.SettleSeconds, 0, 600);
        Positive(errors, nameof(c.DesktopIdleMaxMiB), c.DesktopIdleMaxMiB);
        Positive(errors, nameof(c.BrowserHeadlessMaxDeltaMiB), c.BrowserHeadlessMaxDeltaMiB);
        Positive(errors, nameof(c.BrowserHeadedMaxDeltaMiB), c.BrowserHeadedMaxDeltaMiB);
        Range(errors, nameof(c.ScreenshotSamples), c.ScreenshotSamples, 1, 1000);
        Positive(errors, nameof(c.ScreenshotHypervisorP95MaxMs), c.ScreenshotHypervisorP95MaxMs);
        Positive(errors, nameof(c.ScreenshotGuestP95MaxMs), c.ScreenshotGuestP95MaxMs);
        Positive(errors, nameof(c.ScreenshotBrowserP95MaxMs), c.ScreenshotBrowserP95MaxMs);
        if (c.ScreenshotMaxFrameBytes < 1)
        {
            errors.Add($"{nameof(c.ScreenshotMaxFrameBytes)} must be at least 1.");
        }

        Range(errors, nameof(c.InputSamples), c.InputSamples, 1, 1000);
        Positive(errors, nameof(c.InputHypervisorP95MaxMs), c.InputHypervisorP95MaxMs);
        Positive(errors, nameof(c.InputGuestP95MaxMs), c.InputGuestP95MaxMs);
        Positive(errors, nameof(c.InputBrowserP95MaxMs), c.InputBrowserP95MaxMs);
        Range(errors, nameof(c.DensityMinPairs), c.DensityMinPairs, 1, 1000);
        if (c.DensityMaxPairs < c.DensityMinPairs || c.DensityMaxPairs > 1000)
        {
            errors.Add($"{nameof(c.DensityMaxPairs)} must be between {nameof(c.DensityMinPairs)} and 1000.");
        }

        Range(errors, nameof(c.DensityWindowsVmMemoryMb), c.DensityWindowsVmMemoryMb, 256, 1_048_576);
        Range(errors, nameof(c.DensityLinuxVmMemoryMb), c.DensityLinuxVmMemoryMb, 256, 1_048_576);
        Range(errors, nameof(c.DensityQueueDetectSeconds), c.DensityQueueDetectSeconds, 1, 600);
        return errors;
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DisplayBenchCriteria options)
    {
        var errors = Check(options);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Range(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name} must be between {min} and {max}.");
        }
    }

    private static void Positive(List<string> errors, string name, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            errors.Add($"{name} must be a positive number.");
        }
    }
}
