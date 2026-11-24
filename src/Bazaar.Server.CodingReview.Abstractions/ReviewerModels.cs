namespace Bazaar.Server.CodingReview;

/// <summary>One model the author's whitelist shows to the reviewer pool.</summary>
public sealed record ReviewModelOption(string ModelId, string Family);

/// <summary>The reviewer pool for one author: the author's model and family, and the candidates the author's whitelist allows.</summary>
public sealed record ReviewerPool(string AuthorModelId, string AuthorFamily, IReadOnlyList<ReviewModelOption> Candidates);

/// <summary>The reviewer the chooser picked. <see cref="SameFamily"/> is true when no other family was available.</summary>
public sealed record ReviewerModelChoice(string ModelId, string Family, bool SameFamily);
