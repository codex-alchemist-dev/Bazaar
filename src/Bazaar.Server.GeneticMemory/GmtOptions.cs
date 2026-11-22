namespace Bazaar.Server.GeneticMemory;

/// <summary>Operator options (doc 25: <c>gmt.lesson_reviewer</c>; binds from <c>Bazaar:GeneticMemory</c>).</summary>
public sealed record GmtOptions
{
    /// <summary>Who reviews lesson candidates. Tool grants and charter text always go to the owner whatever this says.</summary>
    public Abstractions.GmtReviewer LessonReviewer { get; init; } = Abstractions.GmtReviewer.Architect;

    /// <summary>Agent id that owns the memory commits of the accept flow.</summary>
    public string WriterAgentId { get; init; } = "gmt";
}
