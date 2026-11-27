using System.Diagnostics.CodeAnalysis;

namespace Bazaar.Core;

/// <summary>
/// Who performed an action and, optionally, on whose behalf, for example a service acting for a signed-in person
/// or an agent acting for a user. Audit entries carry both.
/// </summary>
/// <remarks>
/// The text form is the serialised form: <c>actor</c> or <c>actor on behalf of principal</c>, each side in the
/// <see cref="Principal"/> <c>kind:id</c> form. Principal ids contain no whitespace, so the separator is unambiguous.
/// </remarks>
/// <param name="Actor">The principal that performed the action.</param>
/// <param name="OnBehalfOf">The principal the actor was acting for, or null when it acted for itself.</param>
public sealed record PrincipalAction(Principal Actor, Principal? OnBehalfOf = null)
{
    /// <summary>A short human-readable form: <c>actor</c>, or <c>actor on behalf of principal</c>.</summary>
    public override string ToString() => OnBehalfOf is null ? Actor.ToString() : $"{Actor}{_separator}{OnBehalfOf}";

    private const string _separator = " on behalf of ";

    /// <summary>Parses the text form, throwing <see cref="FormatException"/> when it is invalid.</summary>
    public static PrincipalAction Parse(string text) =>
        TryParse(text, out var action) ? action : throw new FormatException($"'{text}' is not a valid principal action.");

    /// <summary>Parses the text form produced by <see cref="ToString"/>. No trimming is done.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out PrincipalAction? action)
    {
        action = null;
        if (text is null)
        {
            return false;
        }

        var split = text.IndexOf(_separator, StringComparison.Ordinal);
        if (split < 0)
        {
            if (!Principal.TryParse(text, out var solo))
            {
                return false;
            }

            action = new PrincipalAction(solo);
            return true;
        }

        if (!Principal.TryParse(text[..split], out var actor) || !Principal.TryParse(text[(split + _separator.Length)..], out var onBehalfOf))
        {
            return false;
        }

        action = new PrincipalAction(actor, onBehalfOf);
        return true;
    }
}
