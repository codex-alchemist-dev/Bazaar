namespace Bazaar.Rubedo.Syntax;

/// <summary>
/// A node of the syntax tree. Every node has an offset for diagnostics, and <see cref="Type"/> is set by the type checker.
/// Nodes are immutable once checked.
/// </summary>
internal abstract class Node(int offset)
{
    public int Offset { get; } = offset;

    public RubedoType Type { get; internal set; }
}

internal sealed class LiteralNode(object value, RubedoType type, int offset) : Node(offset)
{
    /// <summary>A <c>bool</c>, <c>decimal</c> or <c>string</c>, matching <see cref="Node.Type"/>.</summary>
    public object Value { get; } = value;

    public RubedoType LiteralType { get; } = type;
}

internal sealed class IdentifierNode(string name, int offset) : Node(offset)
{
    public string Name { get; } = name;
}

internal sealed class UnaryNode(string op, Node operand, int offset) : Node(offset)
{
    public string Op { get; } = op;

    public Node Operand { get; } = operand;
}

internal sealed class BinaryNode(string op, Node left, Node right, int offset) : Node(offset)
{
    public string Op { get; } = op;

    public Node Left { get; } = left;

    public Node Right { get; } = right;
}

internal sealed class CallNode(string name, IReadOnlyList<Node> arguments, int offset) : Node(offset)
{
    public string Name { get; } = name;

    public IReadOnlyList<Node> Arguments { get; } = arguments;
}
