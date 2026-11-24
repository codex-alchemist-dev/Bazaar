using Bazaar.Client.Materials;
using Bazaar.Client.Motion;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Settings;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Shell;

/// <summary>The visual-system settings of doc 93 (Settings): their definitions with the shipped defaults, and the theme read.</summary>
public static class VisualSettings
{
    /// <summary>Every visual setting, to register with the head's <see cref="IClientSettings"/> store.</summary>
    public static IReadOnlyList<ClientSettingDefinition> Definitions { get; } =
    [
        new(ClientThemeSettings.Key, BundledThemes.Default, [.. BundledThemes.Names, ClientThemeSettings.Imported]),
        new(ClientDensitySettings.Key, ClientDensitySettings.Format(ClientDensitySettings.Default), ClientDensitySettings.Values),
        new(MaterialSettings.Key, MaterialSettings.Format(MaterialSettings.Default), ["auto", "translucent", "opaque"]),
        // No default: liquid on a phone and subtle on a desktop is the platform's call (MotionLevelResolver).
        new(MotionSettingKeys.MotionLevel, null, ["liquid", "subtle", "reduced"]),
    ];

    /// <summary>
    /// The theme named by <c>client.theme</c>. An imported theme file is loaded by the theme import feature, which this does not
    /// replace; until it is wired, <c>imported</c> and any unknown value give the default bundled theme.
    /// </summary>
    public static ThemeDefinition LoadTheme(IClientSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? name = settings.GetValue(ClientThemeSettings.Key)?.Trim().ToLowerInvariant();
        return BundledThemes.Load(name is not null && BundledThemes.Names.Contains(name) ? name : BundledThemes.Default);
    }
}

/// <summary>
/// Feeds the person's visual settings and the measured conditions into the live visual state (doc 93, Settings and Behaviour):
/// <c>client.materials</c> into the materials source, <c>client.motion_level</c> and the OS reduced-motion flag into
/// <see cref="MotionSettings"/>, <c>client.theme</c> into the chrome, <c>client.density</c> into <see cref="Density"/>, and the
/// frame-budget verdict into both the materials flag and the motion cap. It applies everything once at construction and again as
/// each input changes. Changes that arrive from other threads (the settings store, an OS probe) are marshalled with
/// <c>post</c>; the frame-budget monitor runs on the UI thread already.
/// </summary>
public sealed class VisualSettingsBinder : IDisposable
{
    private readonly IClientSettings _settings;
    private readonly PolledMaterialEnvironmentSource _materials;
    private readonly MotionSettings _motion;
    private readonly IOsReducedMotionSource? _os;
    private readonly FrameBudgetMonitor? _monitor;
    private readonly ShellMaterials? _chrome;
    private readonly Action<Action> _post;

    public VisualSettingsBinder(
        IClientSettings settings,
        PolledMaterialEnvironmentSource materials,
        MotionSettings motion,
        IOsReducedMotionSource? osReducedMotion = null,
        FrameBudgetMonitor? frameBudget = null,
        ShellMaterials? chrome = null,
        Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(motion);
        _settings = settings;
        _materials = materials;
        _motion = motion;
        _os = osReducedMotion;
        _monitor = frameBudget;
        _chrome = chrome;
        _post = post ?? (action => action());

        ApplyAll();
        _settings.Changed += OnSettingChanged;
        if (_os is not null)
        {
            _os.Changed += OnOsChanged;
        }

        if (_monitor is not null)
        {
            _monitor.Changed += OnBudgetChanged;
        }
    }

    /// <summary>The person's <c>client.density</c>.</summary>
    public ClientDensity Density { get; private set; }

    /// <summary>Raised after <see cref="Density"/> changes. Views that lay out by density re-read it.</summary>
    public event EventHandler? DensityChanged;

    /// <summary>
    /// What every head does at start: makes <paramref name="motion"/> the shared <see cref="MotionHost"/> state, starts measuring
    /// frames of running animations, and binds the settings, the OS flag and the frame-budget verdict to the live visual state.
    /// Dispose the result when the window closes.
    /// </summary>
    public static VisualSettingsBinder Start(
        IClientSettings settings,
        PolledMaterialEnvironmentSource materials,
        MotionSettings motion,
        IOsReducedMotionSource osReducedMotion,
        ShellMaterials? chrome,
        Action<Action> post)
    {
        MotionHost.Use(motion);
        var monitor = new FrameBudgetMonitor();
        MeasureFrames(monitor);
        return new VisualSettingsBinder(settings, materials, motion, osReducedMotion, monitor, chrome, post);
    }

    /// <summary>Connects a monitor to the running animations of the shared <see cref="MotionHost"/>, so frames measure themselves.</summary>
    public static void MeasureFrames(FrameBudgetMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        MotionHost.FrameTimes = new FrameBudgetSink(monitor);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingChanged;
        if (_os is not null)
        {
            _os.Changed -= OnOsChanged;
        }

        if (_monitor is not null)
        {
            _monitor.Changed -= OnBudgetChanged;
        }
    }

    private void ApplyAll()
    {
        ApplyMaterials();
        ApplyMotionLevel();
        ApplyOs();
        ApplyBudget();
        ApplyDensity();
        ApplyTheme();
    }

    private void OnSettingChanged(object? sender, ClientSettingChangedEventArgs e) => _post(() =>
    {
        switch (e.Key)
        {
            case MaterialSettings.Key:
                ApplyMaterials();
                break;
            case MotionSettingKeys.MotionLevel:
                ApplyMotionLevel();
                break;
            case ClientDensitySettings.Key:
                ApplyDensity();
                break;
            case ClientThemeSettings.Key:
                ApplyTheme();
                break;
        }
    });

    private void OnOsChanged(object? sender, EventArgs e) => _post(ApplyOs);

    private void OnBudgetChanged(object? sender, EventArgs e) => ApplyBudget();

    private void ApplyMaterials() => _materials.Level = MaterialSettings.Parse(_settings.GetValue(MaterialSettings.Key));

    private void ApplyMotionLevel() =>
        _motion.Setting = MotionLevelResolver.TryParse(_settings.GetValue(MotionSettingKeys.MotionLevel), out MotionLevel level) ? level : null;

    private void ApplyOs() => _motion.OsReducedMotion = _os?.Current ?? false;

    // Light: no blur, motion at most subtle. Heavy: motion reduced too. Both lift again once the monitor sees frames fit.
    private void ApplyBudget()
    {
        FrameBudgetDegradation degradation = _monitor?.Degradation ?? FrameBudgetDegradation.None;
        _materials.FrameBudgetMissed = degradation != FrameBudgetDegradation.None;
        _motion.BudgetCap = degradation switch
        {
            FrameBudgetDegradation.Light => MotionLevel.Subtle,
            FrameBudgetDegradation.Heavy => MotionLevel.Reduced,
            _ => null,
        };
    }

    private void ApplyDensity()
    {
        ClientDensity density = ClientDensitySettings.Parse(_settings.GetValue(ClientDensitySettings.Key));
        if (density == Density)
        {
            return;
        }

        Density = density;
        DensityChanged?.Invoke(this, EventArgs.Empty);
    }

    // Applied instantly; the cross-fade of doc 93 (Behaviour) is not built yet.
    private void ApplyTheme()
    {
        if (_chrome is not null)
        {
            _chrome.Theme = VisualSettings.LoadTheme(_settings);
        }
    }
}
