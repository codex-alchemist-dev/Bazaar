using Bazaar.Contracts.Gc;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Agents.Responder;

/// <summary>
/// Builds the chat the model sees for one reply: the agent's system prompt, then the GC history. The agent's own messages are
/// assistant turns; every other message is a user turn prefixed with the speaker, so the model can tell speakers apart.
/// </summary>
public static class ResponderPrompt
{
    /// <summary>The chat for <paramref name="persona"/> replying to <paramref name="history"/>, oldest first.</summary>
    /// <param name="persona">The agent that replies.</param>
    /// <param name="history">The GC history, oldest first, ending with the trigger.</param>
    /// <param name="names">Display names by principal id. Principals not listed are labelled by kind and id.</param>
    public static IReadOnlyList<ChatMessage> Build(
        AgentResponderPersona persona, IReadOnlyList<ResponderMessage> history, IReadOnlyDictionary<Guid, string> names)
    {
        ArgumentNullException.ThrowIfNull(persona);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(names);

        var messages = new List<ChatMessage> { new(ChatRole.System, persona.SystemPrompt) };
        var started = false;
        foreach (var message in history)
        {
            var mine = message.AuthorKind == AuthorKind.Agent && message.AuthorId == persona.AgentId;

            // A model call needs a user turn first, so older replies that no longer have a question before them are dropped.
            if (!started && mine)
                continue;
            started = true;

            messages.Add(mine
                ? new ChatMessage(ChatRole.Assistant, message.Text)
                : new ChatMessage(ChatRole.User, $"{SpeakerName(message, names)}: {message.Text}"));
        }

        return messages;
    }

    /// <summary>The label a message's author gets in the chat: the display name if known, else the kind and a short id.</summary>
    public static string SpeakerName(ResponderMessage message, IReadOnlyDictionary<Guid, string> names) =>
        names.TryGetValue(message.AuthorId, out var name)
            ? name
            : $"{message.AuthorKind.ToString().ToLowerInvariant()} {message.AuthorId.ToString("N")[..8]}";
}
