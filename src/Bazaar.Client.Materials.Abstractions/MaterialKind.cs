namespace Bazaar.Client.Materials;

/// <summary>The material a surface is drawn with once the policy has decided (docs/design/93-client-visual-system-and-motion.md, Materials).</summary>
public enum MaterialKind
{
    /// <summary>The theme's opaque fallback colour. Always safe.</summary>
    Opaque,

    /// <summary>A tinted translucent surface over whatever is behind the window; no blur.</summary>
    Tinted,

    /// <summary>Real blur behind the surface (Windows acrylic, or Android blur measured within the frame budget).</summary>
    Acrylic,

    /// <summary>The Windows 11 Mica window backdrop.</summary>
    Mica,
}
