using Bazaar.Client.Terminal;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Terminal.Tests;

public sealed class MessageFormatterTests
{
    private static readonly Guid _gcId = Guid.Parse("6f1c2b0e-3d4a-4b8e-9c1f-2a3b4c5d6e7f");
    private static readonly Guid _authorId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly DateTimeOffset _ts = new(2026, 10, 29, 9, 5, 42, TimeSpan.Zero);

    private static WireGcMessage Message(string text, Guid? author = null, bool edited = false, long n = 7) =>
        new(_gcId, n, edited ? 2 : 1, Guid.NewGuid(), author ?? _authorId, text, _ts, edited, [], null);

    [Fact]
    public void FormatsTimeSpeakerAndText()
    {
        string line = MessageFormatter.Format(Message("hello there"), new Speaker("Dana", false), TimeSpan.Zero);

        Assert.Equal("[09:05] Dana: hello there", line);
    }

    [Fact]
    public void UsesTheRequestedOffsetForTime()
    {
        string line = MessageFormatter.Format(Message("hi"), new Speaker("Dana", false), TimeSpan.FromHours(2));

        Assert.Equal("[11:05] Dana: hi", line);
    }

    [Fact]
    public void MarksAgentsInTheSpeakerLabel()
    {
        string line = MessageFormatter.Format(Message("ship it"), new Speaker("Charles", IsAgent: true), TimeSpan.Zero);

        Assert.Equal("[09:05] Charles [agent]: ship it", line);
    }

    [Fact]
    public void MarksEditedMessages()
    {
        string line = MessageFormatter.Format(Message("fixed", edited: true), new Speaker("Dana", false), TimeSpan.Zero);

        Assert.Equal("[09:05] Dana (edited): fixed", line);
    }

    [Fact]
    public void IndentsContinuationLines()
    {
        string line = MessageFormatter.Format(Message("one\r\ntwo\nthree"), new Speaker("Dana", false), TimeSpan.Zero);

        Assert.Equal("[09:05] Dana: one\n        two\n        three", line);
    }

    [Fact]
    public void ResolvesOwnMessagesFirst()
    {
        var members = new Dictionary<Guid, Speaker> { [_authorId] = new Speaker("Dana", false) };

        var speaker = MessageFormatter.Resolve(Message("x"), isOwn: true, ownName: "Me", members);

        Assert.Equal(new Speaker("Me", false), speaker);
    }

    [Fact]
    public void ResolvesKnownMembersAndFallsBackToShortId()
    {
        var members = new Dictionary<Guid, Speaker> { [_authorId] = new Speaker("Charles", IsAgent: true) };

        Assert.Equal(new Speaker("Charles", IsAgent: true), MessageFormatter.Resolve(Message("x"), false, "Me", members));

        var stranger = Guid.Parse("bbbbbbbb-1234-0000-0000-000000000000");
        var unknown = MessageFormatter.Resolve(Message("x", author: stranger), false, "Me", members);
        Assert.Equal(new Speaker("bbbbbbbb", IsAgent: false), unknown);
    }

    [Fact]
    public void StatusLinesAreBracketed()
    {
        Assert.Equal("-- connected --", MessageFormatter.Status("connected"));
    }
}
