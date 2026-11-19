namespace Bazaar.Server.Agents.LoopGuard;

/// <summary>The five loop guards in doc 06 ("Loop guards"). Any trip sends the agents involved to the Psych Ward (C34).</summary>
public enum GuardKind
{
    /// <summary>Two agents alternate messages with no human message, tool result or third agent in between.</summary>
    PingPong,

    /// <summary>A nested @mention chain is deeper than the limit.</summary>
    MentionDepth,

    /// <summary>An agent's wakes in the last hour, all reasons included, exceed the limit.</summary>
    WakesPerHour,

    /// <summary>Agent messages in a thread since the last human message exceed the limit.</summary>
    ThreadAgentMessages,

    /// <summary>An agent repeats the same tool call more times in a row than the limit.</summary>
    RepeatToolCall,
}
