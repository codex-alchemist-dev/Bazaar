namespace Bazaar.Server.Council.Prompts;

/// <summary>
/// A built ballot prompt that has passed the privacy guard.
/// </summary>
/// <param name="Text">The prompt text sent to the voter.</param>
/// <param name="Phase">The phase the prompt was built for.</param>
/// <param name="Context">The GC context the prompt runs in.</param>
/// <param name="TaintedAtWrite">Pass-through from the request. The caller records it on the ballot (doc 10, Taint).</param>
public sealed record BallotPrompt(string Text, BallotPhase Phase, PromptContext Context, bool TaintedAtWrite);
