using Bazaar.Rubedo.Syntax;

namespace Bazaar.Rubedo.Semantics;

/// <summary>
/// Infers and checks the type of every node (doc 41a sections 2 and 5). Sets <see cref="Node.Type"/> on each node and throws
/// <see cref="RubedoException"/> on the first mismatch. No implicit conversions exist.
/// </summary>
internal static class TypeChecker
{
    public static RubedoType Check(Node node, RubedoSchema schema)
    {
        RubedoType type = Infer(node, schema);
        node.Type = type;
        return type;
    }

    private static RubedoType Infer(Node node, RubedoSchema schema)
    {
        switch (node)
        {
            case LiteralNode literal:
                return literal.LiteralType;

            case IdentifierNode identifier:
                if (!schema.TryGetType(identifier.Name, out RubedoType declared))
                {
                    throw new RubedoException("unknown-identifier", $"'{identifier.Name}' is not in the context.", identifier.Offset);
                }

                return declared;

            case UnaryNode unary:
                {
                    RubedoType operand = Check(unary.Operand, schema);
                    if (unary.Op == "!")
                    {
                        Require(operand, RubedoType.Bool, unary.Offset, "'!' needs true or false.");
                        return RubedoType.Bool;
                    }

                    Require(operand, RubedoType.Number, unary.Offset, "A unary '-' needs a number.");
                    return RubedoType.Number;
                }

            case BinaryNode binary:
                return InferBinary(binary, schema);

            case CallNode call:
                return InferCall(call, schema);

            default:
                throw new RubedoException("internal", "Unknown syntax node.", node.Offset);
        }
    }

    private static RubedoType InferBinary(BinaryNode binary, RubedoSchema schema)
    {
        RubedoType left = Check(binary.Left, schema);
        RubedoType right = Check(binary.Right, schema);
        switch (binary.Op)
        {
            case "&&":
            case "||":
                Require(left, RubedoType.Bool, binary.Offset, $"'{binary.Op}' needs true or false.");
                Require(right, RubedoType.Bool, binary.Offset, $"'{binary.Op}' needs true or false.");
                return RubedoType.Bool;

            case "==":
            case "!=":
                if (left != right)
                {
                    throw new RubedoException("type", $"'{binary.Op}' needs both sides to have the same type.", binary.Offset);
                }

                return RubedoType.Bool;

            case "<":
            case "<=":
            case ">":
            case ">=":
                Require(left, RubedoType.Number, binary.Offset, $"'{binary.Op}' compares numbers.");
                Require(right, RubedoType.Number, binary.Offset, $"'{binary.Op}' compares numbers.");
                return RubedoType.Bool;

            default:
                // + - * / %
                Require(left, RubedoType.Number, binary.Offset, $"'{binary.Op}' needs numbers.");
                Require(right, RubedoType.Number, binary.Offset, $"'{binary.Op}' needs numbers.");
                return RubedoType.Number;
        }
    }

    private static RubedoType InferCall(CallNode call, RubedoSchema schema)
    {
        if (call.Name == Functions.InSet)
        {
            return InferInSet(call, schema);
        }

        if (!Functions.All.TryGetValue(call.Name, out FunctionSpec? spec))
        {
            throw new RubedoException("unknown-function", $"'{call.Name}' is not a Rubedo function.", call.Offset);
        }

        if (call.Arguments.Count != spec.Parameters.Length)
        {
            throw new RubedoException("arity", $"'{call.Name}' takes {spec.Parameters.Length} argument(s).", call.Offset);
        }

        for (int i = 0; i < spec.Parameters.Length; i++)
        {
            RubedoType actual = Check(call.Arguments[i], schema);
            if (actual != spec.Parameters[i])
            {
                throw new RubedoException("type", $"Argument {i + 1} of '{call.Name}' has the wrong type.", call.Arguments[i].Offset);
            }
        }

        return spec.Result;
    }

    private static RubedoType InferInSet(CallNode call, RubedoSchema schema)
    {
        if (call.Arguments.Count < 2 || call.Arguments.Count > RubedoLanguage.MaxInSetCandidates + 1)
        {
            throw new RubedoException("arity",
                $"'inSet' takes a value and 1 to {RubedoLanguage.MaxInSetCandidates} candidates.", call.Offset);
        }

        RubedoType subject = Check(call.Arguments[0], schema);
        if (subject == RubedoType.Bool)
        {
            throw new RubedoException("type", "'inSet' compares numbers or strings.", call.Arguments[0].Offset);
        }

        for (int i = 1; i < call.Arguments.Count; i++)
        {
            RubedoType candidate = Check(call.Arguments[i], schema);
            if (candidate != subject)
            {
                throw new RubedoException("type", "Every candidate of 'inSet' must have the same type as the value.", call.Arguments[i].Offset);
            }
        }

        return RubedoType.Bool;
    }

    private static void Require(RubedoType actual, RubedoType expected, int offset, string message)
    {
        if (actual != expected)
        {
            throw new RubedoException("type", message, offset);
        }
    }
}
