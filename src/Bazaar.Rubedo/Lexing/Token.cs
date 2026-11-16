namespace Bazaar.Rubedo.Lexing;

internal enum TokenKind
{
    Number,
    String,
    Identifier,
    Keyword,
    Operator,
    LeftParen,
    RightParen,
    Comma,
    End,
}

/// <summary>A lexical token. <see cref="Text"/> is the source text, or the decoded content for a string.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, int Offset);
