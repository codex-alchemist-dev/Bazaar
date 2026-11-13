namespace Bazaar.RyuseiFuda.Orrery;

/// <summary>Why a predicate was refused at creation.</summary>
public enum RyuseiValidationCode
{
    /// <summary>The kind is not in the closed v1 set.</summary>
    UnknownPredicateKind,

    /// <summary>A filter was set that this predicate kind does not accept.</summary>
    FilterNotAllowed,

    /// <summary>The path glob is empty, absolute, escapes upward, or too long.</summary>
    InvalidPathGlob,
}

/// <summary>A structured refusal: a code a caller can branch on, and a message for the owner or agent.</summary>
public sealed record RyuseiValidationError(RyuseiValidationCode Code, string Message);

/// <summary>The result of validating a predicate. <see cref="Error"/> is null when the predicate is valid.</summary>
public sealed record RyuseiValidationResult(RyuseiValidationError? Error)
{
    public bool IsValid => Error is null;

    public static RyuseiValidationResult Valid { get; } = new((RyuseiValidationError?)null);
}

/// <summary>Accepts only the closed predicate set (doc 38). Anything else is refused with a structured error.</summary>
public static class RyuseiPredicateValidator
{
    /// <summary>The longest path glob accepted for <c>vault.changed</c>.</summary>
    public const int MaxPathGlobLength = 256;

    public static RyuseiValidationResult Validate(RyuseiPredicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!RyuseiPredicates.TryParse(predicate.Kind, out var kind))
        {
            return Refuse(RyuseiValidationCode.UnknownPredicateKind, $"Predicate kind '{predicate.Kind}' is not in the RyuseiFuda predicate set.");
        }

        if (predicate.GcId is not null && kind != RyuseiPredicateKind.GcMessage)
        {
            return Refuse(RyuseiValidationCode.FilterNotAllowed, $"Predicate '{predicate.Kind}' does not take a GC filter.");
        }

        if (predicate.AuthorAgentId is not null && kind != RyuseiPredicateKind.GcMessage)
        {
            return Refuse(RyuseiValidationCode.FilterNotAllowed, $"Predicate '{predicate.Kind}' does not take an author filter.");
        }

        if (predicate.PathGlob is not null)
        {
            if (kind != RyuseiPredicateKind.VaultChanged)
            {
                return Refuse(RyuseiValidationCode.FilterNotAllowed, $"Predicate '{predicate.Kind}' does not take a path glob.");
            }

            var glob = predicate.PathGlob;
            if (glob.Length == 0 || glob.Length > MaxPathGlobLength)
            {
                return Refuse(RyuseiValidationCode.InvalidPathGlob, $"Path glob must be 1 to {MaxPathGlobLength} characters.");
            }

            if (glob.StartsWith('/') || glob.Contains('\\'))
            {
                return Refuse(RyuseiValidationCode.InvalidPathGlob, "Path glob must be relative and use '/' separators.");
            }

            if (glob.Split('/').Contains(".."))
            {
                return Refuse(RyuseiValidationCode.InvalidPathGlob, "Path glob must not contain '..' segments.");
            }
        }

        return RyuseiValidationResult.Valid;
    }

    private static RyuseiValidationResult Refuse(RyuseiValidationCode code, string message) =>
        new(new RyuseiValidationError(code, message));
}
