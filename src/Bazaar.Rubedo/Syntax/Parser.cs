using Bazaar.Rubedo.Lexing;

namespace Bazaar.Rubedo.Syntax;

/// <summary>
/// Recursive-descent parser for the grammar in doc 41a section 4. Comparisons and equality do not chain: two of them in a
/// row without parentheses are a syntax error. Throws <see cref="RubedoException"/> on the first problem.
/// </summary>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private readonly RubedoOptions _options;
    private int _pos;
    private int _depth;

    public Parser(List<Token> tokens, RubedoOptions options)
    {
        _tokens = tokens;
        _options = options;
    }

    public Node ParseExpression()
    {
        Node root = ParseOr();
        Token t = Peek();
        if (t.Kind != TokenKind.End)
        {
            throw new RubedoException("syntax", $"Unexpected '{t.Text}'.", t.Offset);
        }

        return root;
    }

    private Node ParseOr()
    {
        Node left = ParseAnd();
        while (IsOperator("||"))
        {
            Token op = Advance();
            left = new BinaryNode("||", left, ParseAnd(), op.Offset);
        }

        return left;
    }

    private Node ParseAnd()
    {
        Node left = ParseEquality();
        while (IsOperator("&&"))
        {
            Token op = Advance();
            left = new BinaryNode("&&", left, ParseEquality(), op.Offset);
        }

        return left;
    }

    private Node ParseEquality()
    {
        Node left = ParseRelation();
        if (IsOperator("==") || IsOperator("!="))
        {
            Token op = Advance();
            left = new BinaryNode(op.Text, left, ParseRelation(), op.Offset);
            RejectChain();
        }

        return left;
    }

    private Node ParseRelation()
    {
        Node left = ParseSum();
        if (IsOperator("<") || IsOperator("<=") || IsOperator(">") || IsOperator(">="))
        {
            Token op = Advance();
            left = new BinaryNode(op.Text, left, ParseSum(), op.Offset);
            RejectChain();
        }

        return left;
    }

    private Node ParseSum()
    {
        Node left = ParseProduct();
        while (IsOperator("+") || IsOperator("-"))
        {
            Token op = Advance();
            left = new BinaryNode(op.Text, left, ParseProduct(), op.Offset);
        }

        return left;
    }

    private Node ParseProduct()
    {
        Node left = ParseUnary();
        while (IsOperator("*") || IsOperator("/") || IsOperator("%"))
        {
            Token op = Advance();
            left = new BinaryNode(op.Text, left, ParseUnary(), op.Offset);
        }

        return left;
    }

    private Node ParseUnary()
    {
        if (IsOperator("!") || IsOperator("-"))
        {
            Token op = Advance();
            Enter(op.Offset);
            Node operand = ParseUnary();
            _depth--;
            return new UnaryNode(op.Text, operand, op.Offset);
        }

        return ParseCallOrPrimary();
    }

    private Node ParseCallOrPrimary()
    {
        Token t = Peek();
        if (t.Kind == TokenKind.Identifier && _tokens[_pos + 1].Kind == TokenKind.LeftParen)
        {
            Advance();
            Advance();
            Enter(t.Offset);
            var args = new List<Node>();
            if (Peek().Kind != TokenKind.RightParen)
            {
                args.Add(ParseOr());
                while (Peek().Kind == TokenKind.Comma)
                {
                    Advance();
                    args.Add(ParseOr());
                }
            }

            Expect(TokenKind.RightParen, "A call's argument list is not closed.");
            _depth--;
            return new CallNode(t.Text, args, t.Offset);
        }

        return ParsePrimary();
    }

    private Node ParsePrimary()
    {
        Token t = Peek();
        switch (t.Kind)
        {
            case TokenKind.Number:
                Advance();
                if (!Lexer.TryParseNumber(t.Text, out decimal number) || number > RubedoLanguage.MaxMagnitude)
                {
                    throw new RubedoException("number-out-of-range", $"'{t.Text}' is outside the range of a Rubedo number.", t.Offset);
                }

                return new LiteralNode(number, RubedoType.Number, t.Offset);
            case TokenKind.String:
                Advance();
                return new LiteralNode(t.Text, RubedoType.Text, t.Offset);
            case TokenKind.Keyword:
                Advance();
                return new LiteralNode(t.Text == "true", RubedoType.Bool, t.Offset);
            case TokenKind.Identifier:
                Advance();
                return new IdentifierNode(t.Text, t.Offset);
            case TokenKind.LeftParen:
                Advance();
                Enter(t.Offset);
                Node inner = ParseOr();
                Expect(TokenKind.RightParen, "A '(' is not closed.");
                _depth--;
                return inner;
            case TokenKind.End:
                throw new RubedoException("syntax", "The expression ends too early.", t.Offset);
            default:
                throw new RubedoException("syntax", $"Unexpected '{t.Text}'.", t.Offset);
        }
    }

    private void RejectChain()
    {
        Token t = Peek();
        bool chained = t.Kind == TokenKind.Operator && t.Text is "==" or "!=" or "<" or "<=" or ">" or ">=";
        if (chained)
        {
            throw new RubedoException("chained-comparison", "Comparisons do not chain. Use parentheses.", t.Offset);
        }
    }

    private void Enter(int offset)
    {
        if (++_depth > _options.MaxDepth)
        {
            throw new RubedoException("too-deep", $"Nesting may be at most {_options.MaxDepth} levels.", offset);
        }
    }

    private void Expect(TokenKind kind, string message)
    {
        Token t = Peek();
        if (t.Kind != kind)
        {
            throw new RubedoException("syntax", message, t.Offset);
        }

        Advance();
    }

    private Token Peek() => _tokens[_pos];

    private Token Advance() => _tokens[_pos++];

    private bool IsOperator(string op)
    {
        Token t = Peek();
        return t.Kind == TokenKind.Operator && t.Text == op;
    }
}
