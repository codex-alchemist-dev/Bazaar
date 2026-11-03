namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// What the guard checks a built prompt against.
/// </summary>
/// <param name="Prompt">The prompt text.</param>
/// <param name="Phase">The phase the prompt was built for.</param>
/// <param name="Context">Working or Thinking.</param>
/// <param name="Revealed">Whether the reveal has happened for this vote.</param>
/// <param name="PeerVoterIds">Ids of the other eligible voters. None may appear in the prompt.</param>
/// <param name="ReasoningTexts">Every reasoning text the guard knows about (revealed reasons and any own ballot reasoning).</param>
public sealed record PrivacyCheckRequest(
    string Prompt,
    BallotPhase Phase,
    PromptContext Context,
    bool Revealed,
    IReadOnlyCollection<string> PeerVoterIds,
    IReadOnlyCollection<string> ReasoningTexts);
