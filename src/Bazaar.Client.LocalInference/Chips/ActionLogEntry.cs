namespace Bazaar.Client.LocalInference.Chips;

/// <summary>What the agent or user did. Verbs are listed in the order chip labels use.</summary>
public enum ActionVerb { Read, Edited, Created, Deleted, Ran, Fetched }

/// <summary>What an action touched. Labels use the target's singular and plural nouns.</summary>
public enum ActionTarget { File, Command, Web }

/// <summary>
/// One action-log group for chip labels. <paramref name="Count"/> is how many times the action happened.
/// <paramref name="FileExtension"/> is the file type (for example ".cs"), used only for <see cref="ActionTarget.File"/>.
/// </summary>
public sealed record ActionLogEntry(ActionVerb Verb, ActionTarget Target, int Count, string? FileExtension = null);
