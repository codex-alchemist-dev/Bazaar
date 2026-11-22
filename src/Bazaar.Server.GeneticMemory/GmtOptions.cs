namespace Bazaar.Server.GeneticMemory;

/// <summary>Operator options (doc 25: <c>gmt.lesson_reviewer</c>; binds from <c>Bazaar:GeneticMemory</c>).</summary>
public sealed record GmtOptions
{
    /// <summary>Who reviews lesson candidates. Tool grants and charter text always go to the owner whatever this says.</summary>
    public Abstractions.GmtReviewer LessonReviewer { get; init; } = Abstractions.GmtReviewer.Architect;

    /// <summary>Agent id that owns the memory commits of the accept flow.</summary>
    public string WriterAgentId { get; init; } = "gmt";

    /// <summary>
    /// [PROPOSED] Whether the <c>gmt.propose_lesson</c> tool is registered. Off by default: with it off a child has no way to record a
    /// lesson and nothing is staged. Binds from <c>Bazaar:GeneticMemory:ProposalTool:Enabled</c>.
    /// </summary>
    public bool ProposalToolEnabled { get; init; }

    private readonly int _maxProposalsPerChild = 5;

    /// <summary>Most lessons one child may record, 1 to 50. Binds from <c>Bazaar:GeneticMemory:ProposalTool:MaxPerChild</c>.</summary>
    public int MaxProposalsPerChild
    {
        get => _maxProposalsPerChild;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 50);
            _maxProposalsPerChild = value;
        }
    }
}
