namespace Bazaar.Server.Skimmer.Scoring;

/// <summary>The closed set of categories a scorer may assign (doc 13, <c>wake_decisions.category</c>).</summary>
public enum Category
{
    /// <summary>A question addressed to the agent or its work.</summary>
    Question,

    /// <summary>A review request or review activity.</summary>
    Review,

    /// <summary>Something blocked that the agent may unblock.</summary>
    Blocked,

    /// <summary>A decision that was made or is being asked for.</summary>
    Decision,

    /// <summary>General conversation.</summary>
    Chat,

    /// <summary>Anything that fits none of the above.</summary>
    Other,
}

/// <summary>Wire mapping for <see cref="Category"/>. The lowercase strings match the SQL CHECK in migration 0016.</summary>
public static class CategoryWire
{
    /// <summary>Returns the lowercase wire string for a category.</summary>
    public static string ToWire(this Category category) => category switch
    {
        Category.Question => "question",
        Category.Review => "review",
        Category.Blocked => "blocked",
        Category.Decision => "decision",
        Category.Chat => "chat",
        Category.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown category."),
    };

    /// <summary>Parses a wire string. Matching is case-sensitive and closed: anything else returns false.</summary>
    public static bool TryParseWire(string? value, out Category category)
    {
        switch (value)
        {
            case "question":
                category = Category.Question;
                return true;
            case "review":
                category = Category.Review;
                return true;
            case "blocked":
                category = Category.Blocked;
                return true;
            case "decision":
                category = Category.Decision;
                return true;
            case "chat":
                category = Category.Chat;
                return true;
            case "other":
                category = Category.Other;
                return true;
            default:
                category = default;
                return false;
        }
    }
}
