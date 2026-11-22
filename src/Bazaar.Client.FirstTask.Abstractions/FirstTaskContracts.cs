namespace Bazaar.Client.FirstTask;

/// <summary>Where the first task goes: the first agent and the group chat seeded with it (doc 45 section 2.10).</summary>
/// <param name="AgentId">The first agent's member id, mentioned in the message.</param>
/// <param name="ChatId">The group chat the message is posted to.</param>
public sealed record FirstTaskTarget(Guid AgentId, Guid ChatId);

/// <summary>How a send ended. The text is for the person and never repeats what they typed.</summary>
public enum FirstTaskOutcome
{
    /// <summary>The message was posted.</summary>
    Sent,

    /// <summary>The server or the connection refused or failed. The text stays in the box.</summary>
    Failed,
}

/// <summary>Posts the person's first task to the first agent's chat.</summary>
public interface IFirstTaskSender
{
    /// <summary>Posts <paramref name="text"/> to the target chat, mentioning the agent.</summary>
    Task<FirstTaskOutcome> SendAsync(FirstTaskTarget target, string text, CancellationToken cancellationToken = default);
}

/// <summary>Says where the first task goes, or null when the company's first agent is not ready. The walkthrough's facts supply it.</summary>
public interface IFirstTaskTargetSource
{
    /// <summary>The target, or null when unknown.</summary>
    Task<FirstTaskTarget?> ReadAsync(CancellationToken cancellationToken = default);
}
