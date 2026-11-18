namespace Bazaar.Identity.Kek;

/// <summary>
/// A pluggable value of <c>identity.kek_provider</c> (doc 14). The built-in kinds (<c>dpapi</c>, <c>file</c>) live in the KEK module;
/// every other kind is its own module that registers one of these, so the KEK module never references provider modules.
/// </summary>
public interface IKekProviderKind
{
    /// <summary>The setting value this kind answers to, lower case (for example <c>openbao</c>).</summary>
    string Kind { get; }

    /// <summary>
    /// Opens the provider. <paramref name="setting"/> reads a configuration value by its full key (for example
    /// <c>Bazaar:Identity:OpenBao:Address</c>) and returns null when it is unset. A kind that is misconfigured throws here, never
    /// falls back to another provider.
    /// </summary>
    IKekProvider Create(Func<string, string?> setting, string dataRoot);
}
