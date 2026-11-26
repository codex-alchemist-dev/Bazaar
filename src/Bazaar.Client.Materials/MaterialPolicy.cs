namespace Bazaar.Client.Materials;

/// <summary>
/// The material policy (docs/design/93-client-visual-system-and-motion.md, Materials). Content surfaces are always opaque.
/// Chrome is Mica or acrylic on Windows, a tinted translucent surface on Linux and Android, and opaque whenever the
/// system asks for reduced transparency, the backdrop request failed, battery saver is on or the frame budget was missed.
/// The person's setting can force opaque, or force translucency past battery saver and a frame-budget miss; it cannot
/// override reduce-transparency (an accessibility setting) or a backdrop the platform refused.
/// </summary>
public sealed class MaterialPolicy(IMaterialEnvironmentSource source) : IMaterialPolicy
{
    /// <summary>True for surfaces that carry long-form text and so are never translucent.</summary>
    public static bool IsContentSurface(MaterialSurface surface) =>
        surface is MaterialSurface.MessageList or MaterialSurface.Editor or MaterialSurface.Terminal
            or MaterialSurface.Diff or MaterialSurface.LongText;

    /// <inheritdoc />
    public MaterialDecision Resolve(MaterialSurface surface) => Resolve(surface, source.Current);

    /// <summary>The decision for a surface in a given environment.</summary>
    public static MaterialDecision Resolve(MaterialSurface surface, MaterialEnvironment env)
    {
        if (IsContentSurface(surface))
        {
            return Opaque(MaterialReason.ContentSurface);
        }

        if (env.Level == MaterialLevel.Opaque)
        {
            return Opaque(MaterialReason.SettingOpaque);
        }

        if (env.ReduceTransparency)
        {
            return Opaque(MaterialReason.ReduceTransparency);
        }

        if (env.BackdropRequestFailed)
        {
            return Opaque(MaterialReason.BackdropFailed);
        }

        bool forced = env.Level == MaterialLevel.Translucent;
        if (!forced && env.BatterySaver)
        {
            return Opaque(MaterialReason.BatterySaver);
        }

        if (!forced && env.FrameBudgetMissed)
        {
            return Opaque(MaterialReason.FrameBudgetMissed);
        }

        MaterialKind kind = env.Platform switch
        {
            MaterialPlatform.Windows => WindowsKind(surface, env),
            MaterialPlatform.Linux => env.TransparencyAvailable ? MaterialKind.Tinted : MaterialKind.Opaque,
            MaterialPlatform.Android => env.BlurMeasuredWithinBudget ? MaterialKind.Acrylic : MaterialKind.Tinted,
            _ => MaterialKind.Opaque,
        };

        return kind == MaterialKind.Opaque
            ? Opaque(MaterialReason.PlatformUnsupported)
            : new MaterialDecision(kind, MaterialReason.Translucent);
    }

    // The window and rail take the backdrop (Mica, then acrylic); strips, sheets and overlays are tinted surfaces over it.
    private static MaterialKind WindowsKind(MaterialSurface surface, MaterialEnvironment env)
    {
        bool backdrop = env.MicaAvailable || env.AcrylicAvailable;
        if (!backdrop && !env.TransparencyAvailable)
        {
            return MaterialKind.Opaque;
        }

        if (surface == MaterialSurface.Chrome)
        {
            return env.MicaAvailable ? MaterialKind.Mica
                : env.AcrylicAvailable ? MaterialKind.Acrylic
                : MaterialKind.Tinted;
        }

        return MaterialKind.Tinted;
    }

    private static MaterialDecision Opaque(MaterialReason reason) => new(MaterialKind.Opaque, reason);
}
