using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Bazaar.Server.Gateway.Policy;

/// <summary>
/// A provider-qualified model, written <c>provider:model</c> (for example <c>anthropic-direct:claude-haiku-5-5</c>).
/// The model id may itself contain colons and slashes. Wildcards and the global <c>auto</c> family never parse.
/// </summary>
public readonly partial record struct ModelRef(string ProviderId, string ModelId)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$")]
    private static partial Regex ProviderIdPattern();

    public override string ToString() => $"{ProviderId}:{ModelId}";

    public static ModelRef Parse(string text) =>
        TryParse(text, out var parsed) ? parsed : throw new FormatException($"'{text}' is not a provider-qualified model id.");

    public static bool TryParse([NotNullWhen(true)] string? text, out ModelRef parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var provider = text[..colon];
        var model = text[(colon + 1)..];
        if (!ProviderIdPattern().IsMatch(provider) || !IsPlainName(model) || RouteNames.IsAuto(model))
        {
            return false;
        }

        parsed = new ModelRef(provider, model);
        return true;
    }

    private static bool IsPlainName(string name) =>
        name.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '*');
}

/// <summary>Rules for route names. A route name has no colon, so it never reads as a model id.</summary>
public static partial class RouteNames
{
    /// <summary>Routes that resolve to an upstream profile that picks the model itself (docs/design/05, delegated routes).</summary>
    public const string DelegatedPrefix = "custom/";

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]*$")]
    private static partial Regex NamePattern();

    /// <summary>True for <c>auto</c> and anything under <c>auto/</c>: no global auto routing exists, so these never resolve.</summary>
    public static bool IsAuto(string name) =>
        name.Equals("auto", StringComparison.OrdinalIgnoreCase) || name.StartsWith("auto/", StringComparison.OrdinalIgnoreCase);

    public static bool IsValid(string? name) =>
        name is { Length: > 0 and <= 128 } && NamePattern().IsMatch(name) && !IsAuto(name);

    public static bool IsDelegated(string name) => name.StartsWith(DelegatedPrefix, StringComparison.Ordinal);
}
