using Avalonia.Controls;
using Bazaar.Client.Materials;
using Bazaar.Client.Materials.Avalonia;
using Bazaar.Client.Theme;

namespace Bazaar.Client.Shell;

/// <summary>
/// Applies the material policy to the shell's chrome (doc 93, Materials): the window, the sidebar, the top strip, sheets and overlays.
/// Content surfaces (message list, editor, terminal, diffs, long text) are refused here: they are never wrapped, so nothing translucent
/// can end up behind text. Every surface it has touched is re-applied by <see cref="Refresh"/>, which a head calls when the OS or the
/// person's setting changes; theme tokens come from <see cref="ThemeDefinition"/> through <see cref="MaterialBrushes"/>.
/// </summary>
public sealed class ShellMaterials
{
    private readonly IMaterialPolicy _policy;
    private readonly IMaterialFeedback? _feedback;
    private readonly List<(Border Host, MaterialSurface Surface)> _surfaces = [];
    private ThemeDefinition _theme;
    private Window? _window;

    public ShellMaterials(IMaterialPolicy policy, ThemeDefinition theme, IMaterialFeedback? feedback = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(theme);
        _policy = policy;
        _theme = theme;
        _feedback = feedback;
    }

    /// <summary>The theme the surfaces are drawn with. Setting it re-applies every surface.</summary>
    public ThemeDefinition Theme
    {
        get => _theme;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _theme = value;
            Refresh();
        }
    }

    /// <summary>
    /// Applies the chrome decision to <paramref name="window"/> and keeps it current: the surfaces are refreshed when the window is
    /// activated (the cheap moment to notice a settings change) and, once the window is open, a backdrop the window system did not
    /// grant is reported so the chrome falls back to opaque.
    /// </summary>
    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        MaterialApplier.ApplyToWindow(window, _policy, _theme);
        window.Activated += (_, _) => Refresh();
        window.Opened += (_, _) => VerifyBackdrop();
    }

    /// <summary>
    /// Wraps <paramref name="content"/> in a surface drawn with the material for <paramref name="surface"/>. Throws for a content surface:
    /// those are drawn by the view that owns them, opaque.
    /// </summary>
    public Border Surface(MaterialSurface surface, Control? content)
    {
        if (MaterialPolicy.IsContentSurface(surface))
        {
            throw new ArgumentException($"{surface} is a content surface and is never drawn with a material.", nameof(surface));
        }

        var host = new Border { Child = content };
        _surfaces.Add((host, surface));
        Apply(host, surface);
        return host;
    }

    /// <summary>Re-reads the policy and redraws the window and every wrapped surface.</summary>
    public void Refresh()
    {
        if (_window is not null)
        {
            MaterialApplier.ApplyToWindow(_window, _policy, _theme);
        }

        foreach ((Border host, MaterialSurface surface) in _surfaces)
        {
            Apply(host, surface);
        }
    }

    private void Apply(Border host, MaterialSurface surface) =>
        host.Background = MaterialBrushes.For(_theme, surface, _policy.Resolve(surface));

    // The hint is a request: the window system picks the first level it supports. If translucency was asked for and the window
    // ended up with none, the backdrop failed and the policy must stop claiming it.
    private void VerifyBackdrop()
    {
        if (_window is null || _feedback is null)
        {
            return;
        }

        MaterialDecision chrome = _policy.Resolve(MaterialSurface.Chrome);
        if (!chrome.IsOpaque && _window.ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            _feedback.ReportBackdropFailed();
            Refresh();
        }
    }
}
