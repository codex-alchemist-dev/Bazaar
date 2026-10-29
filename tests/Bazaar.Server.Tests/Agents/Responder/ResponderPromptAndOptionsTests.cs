using Bazaar.Contracts.Gc;
using Bazaar.Server.Agents.Responder;
using Microsoft.Extensions.AI;

namespace Bazaar.Server.Tests.Agents.Responder;

public class ResponderPromptAndOptionsTests
{
    private static readonly IReadOnlyDictionary<Guid, string> _names = new Dictionary<Guid, string>
    {
        [ResponderTestIds.Juniper] = "Juniper",
    };

    [Fact]
    public void The_prompt_opens_with_the_system_prompt_and_maps_speakers_to_roles()
    {
        var history = new[]
        {
            new ResponderMessage(1, Guid.NewGuid(), AuthorKind.Owner, ResponderTestIds.Owner, "hello", []),
            new ResponderMessage(2, Guid.NewGuid(), AuthorKind.Agent, ResponderTestIds.Charles, "hi there", []),
            new ResponderMessage(3, Guid.NewGuid(), AuthorKind.Agent, ResponderTestIds.Juniper, "I agree", []),
        };

        var chat = ResponderPrompt.Build(ResponderTestIds.CharlesPersona, history, _names);

        Assert.Equal(4, chat.Count);
        Assert.Equal(ChatRole.System, chat[0].Role);
        Assert.Equal(ChatRole.User, chat[1].Role);
        Assert.Equal($"owner {ResponderTestIds.Owner.ToString("N")[..8]}: hello", chat[1].Text);
        Assert.Equal(ChatRole.Assistant, chat[2].Role);
        Assert.Equal("hi there", chat[2].Text);
        Assert.Equal(ChatRole.User, chat[3].Role);
        Assert.Equal("Juniper: I agree", chat[3].Text);
    }

    [Fact]
    public void Leading_replies_with_no_question_before_them_are_dropped()
    {
        var history = new[]
        {
            new ResponderMessage(1, Guid.NewGuid(), AuthorKind.Agent, ResponderTestIds.Charles, "an old reply", []),
            new ResponderMessage(2, Guid.NewGuid(), AuthorKind.Owner, ResponderTestIds.Owner, "a question", []),
        };

        var chat = ResponderPrompt.Build(ResponderTestIds.CharlesPersona, history, _names);

        Assert.Equal([ChatRole.System, ChatRole.User], chat.Select(m => m.Role));
    }

    [Fact]
    public void Options_validation_accepts_the_defaults_and_a_complete_persona()
    {
        var options = new AgentResponderOptions { Agents = [ResponderTestIds.CharlesPersona] };

        options.Validate();

        Assert.Equal(30, options.HistoryLimit);
        Assert.Equal("agent-default", options.DefaultRoute);
    }

    [Fact]
    public void Options_validation_rejects_a_limit_below_one()
    {
        var options = new AgentResponderOptions { HistoryLimit = 0 };

        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Options_validation_rejects_an_agent_listed_twice()
    {
        var options = new AgentResponderOptions { Agents = [ResponderTestIds.CharlesPersona, ResponderTestIds.CharlesPersona] };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Options_validation_rejects_a_persona_without_a_prompt()
    {
        var options = new AgentResponderOptions
        {
            Agents = [new AgentResponderPersona { AgentId = ResponderTestIds.Charles, Name = "Charles", SystemPrompt = " " }],
        };

        Assert.Throws<ArgumentException>(() => options.Validate());
    }
}
