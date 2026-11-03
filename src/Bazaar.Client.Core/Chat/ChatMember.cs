namespace Bazaar.Client.Core.Chat;

/// <summary>A display name for a principal the client knows about.</summary>
/// <param name="Name">Display name.</param>
/// <param name="IsAgent">True for an agent; its messages carry an <c>[agent]</c> marker.</param>
public sealed record ChatMember(string Name, bool IsAgent)
{
    /// <summary>The name with the agent marker when it applies.</summary>
    public string Label => IsAgent ? $"{Name} [agent]" : Name;
}
