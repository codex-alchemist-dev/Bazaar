namespace Bazaar.Server.VmDensity;

/// <summary>Density governor settings (doc 59 section 9). Bound from <c>Bazaar:VmDensity</c>.</summary>
public sealed class VmDensityOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Bazaar:VmDensity";

    /// <summary>When false the governor admits everything.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Free host memory (MiB) the governor keeps back (<c>vm.density.host_reserve_mb</c>).</summary>
    public long HostReserveMb { get; set; } = 4096;

    /// <summary>Hard cap on running VMs besides the memory rule; 0 means no fixed cap (<c>vm.density.max_running</c>).</summary>
    public int MaxRunning { get; set; }

    /// <summary>How long a request may wait for room before it fails with <c>QueueTimeout</c>. Zero or less means do not wait.</summary>
    public TimeSpan QueueTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The most requests that may wait; the next one fails with <c>QueueFull</c>.</summary>
    public int MaxQueueLength { get; set; } = 64;

    /// <summary>How often waiting requests are re-evaluated even without a memory-changed notice.</summary>
    public TimeSpan RecheckInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>A VM must have been idle at least this long before it may be saved to make room.</summary>
    public TimeSpan MinIdleBeforeSave { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The most VMs one admission may save.</summary>
    public int MaxSavesPerAdmission { get; set; } = 4;

    /// <summary>
    /// When true, a later, smaller request may start while an older queued request still does not fit. Default false: strict FIFO.
    /// </summary>
    public bool AllowSmallerToPass { get; set; }

    /// <summary>After saves finish, how many times the governor re-reads host memory waiting for the memory to come back.</summary>
    public int SaveSettleChecks { get; set; } = 6;

    /// <summary>The pause between those re-reads.</summary>
    public TimeSpan SaveSettleDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// How long a VM that was just saved (the directory may still list it) or whose save failed is left alone as a victim.
    /// </summary>
    public TimeSpan VictimCooldown { get; set; } = TimeSpan.FromSeconds(30);
}
