using Bazaar.Rubedo.Semantics;
using Bazaar.Rubedo.Syntax;

namespace Bazaar.Rubedo.Runtime;

/// <summary>
/// Evaluates a checked tree (doc 41a sections 7 and 8). Pure and deterministic: decimal arithmetic, no clock, no randomness.
/// Every node visited costs one step from the budget. Values are <c>bool</c>, <c>decimal</c> or <c>string</c>.
/// </summary>
internal sealed class Evaluator
{
    private readonly IReadOnlyDictionary<string, object> _context;
    private int _stepsLeft;

    public Evaluator(IReadOnlyDictionary<string, object> context, int maxSteps)
    {
        _context = context;
        _stepsLeft = maxSteps;
    }

    public object Evaluate(Node node)
    {
        Spend(node.Offset);
        return node switch
        {
            LiteralNode literal => literal.Value,
            IdentifierNode identifier => ReadIdentifier(identifier),
            UnaryNode unary => unary.Op == "!" ? !(bool)Evaluate(unary.Operand) : EvaluateNegate(unary),
            BinaryNode binary => EvaluateBinary(binary),
            CallNode call => EvaluateCall(call),
            _ => throw new RubedoException("internal", "Unknown syntax node.", node.Offset),
        };
    }

    private void Spend(int offset)
    {
        if (--_stepsLeft < 0)
        {
            throw new RubedoException("budget", "The expression ran out of its step budget.", offset);
        }
    }

    private object ReadIdentifier(IdentifierNode identifier)
    {
        if (!_context.TryGetValue(identifier.Name, out object? value) || value is null)
        {
            throw new RubedoException("missing-value", $"No value was supplied for '{identifier.Name}'.", identifier.Offset);
        }

        return value;
    }

    private decimal EvaluateNegate(UnaryNode unary) => CheckRange(-(decimal)Evaluate(unary.Operand), unary.Offset);

    private object EvaluateBinary(BinaryNode binary)
    {
        switch (binary.Op)
        {
            case "&&":
                return (bool)Evaluate(binary.Left) && (bool)Evaluate(binary.Right);
            case "||":
                return (bool)Evaluate(binary.Left) || (bool)Evaluate(binary.Right);
        }

        object left = Evaluate(binary.Left);
        object right = Evaluate(binary.Right);
        switch (binary.Op)
        {
            case "==":
                return Equals(left, right);
            case "!=":
                return !Equals(left, right);
            case "<":
                return (decimal)left < (decimal)right;
            case "<=":
                return (decimal)left <= (decimal)right;
            case ">":
                return (decimal)left > (decimal)right;
            case ">=":
                return (decimal)left >= (decimal)right;
        }

        decimal a = (decimal)left;
        decimal b = (decimal)right;
        return binary.Op switch
        {
            "+" => CheckRange(CheckedAdd(a, b, binary.Offset), binary.Offset),
            "-" => CheckRange(CheckedSub(a, b, binary.Offset), binary.Offset),
            "*" => CheckRange(CheckedMul(a, b, binary.Offset), binary.Offset),
            "/" => CheckRange(Divide(a, b, binary.Offset), binary.Offset),
            "%" => CheckRange(Modulo(a, b, binary.Offset), binary.Offset),
            _ => throw new RubedoException("internal", "Unknown operator.", binary.Offset),
        };
    }

    private object EvaluateCall(CallNode call)
    {
        var args = new object[call.Arguments.Count];
        for (int i = 0; i < args.Length; i++)
        {
            args[i] = Evaluate(call.Arguments[i]);
        }

        switch (call.Name)
        {
            case "min":
                return Math.Min((decimal)args[0], (decimal)args[1]);
            case "max":
                return Math.Max((decimal)args[0], (decimal)args[1]);
            case "abs":
                return Math.Abs((decimal)args[0]);
            case "clamp":
                {
                    decimal x = (decimal)args[0];
                    decimal lo = (decimal)args[1];
                    decimal hi = (decimal)args[2];
                    if (lo > hi)
                    {
                        throw new RubedoException("bad-range", "clamp needs lo less than or equal to hi.", call.Offset);
                    }

                    return Math.Clamp(x, lo, hi);
                }
            case "contains":
                return ((string)args[0]).Contains((string)args[1], StringComparison.Ordinal);
            case "startsWith":
                return ((string)args[0]).StartsWith((string)args[1], StringComparison.Ordinal);
            case Functions.InSet:
                for (int i = 1; i < args.Length; i++)
                {
                    if (Equals(args[0], args[i]))
                    {
                        return true;
                    }
                }

                return false;
            default:
                throw new RubedoException("unknown-function", $"'{call.Name}' is not a Rubedo function.", call.Offset);
        }
    }

    private static decimal CheckedAdd(decimal a, decimal b, int offset) => Wrap(() => a + b, offset);

    private static decimal CheckedSub(decimal a, decimal b, int offset) => Wrap(() => a - b, offset);

    private static decimal CheckedMul(decimal a, decimal b, int offset) => Wrap(() => a * b, offset);

    private static decimal Divide(decimal a, decimal b, int offset)
    {
        if (b == 0)
        {
            throw new RubedoException("division-by-zero", "Division by zero.", offset);
        }

        return Wrap(() => a / b, offset);
    }

    private static decimal Modulo(decimal a, decimal b, int offset)
    {
        if (b == 0)
        {
            throw new RubedoException("division-by-zero", "Modulo by zero.", offset);
        }

        return Wrap(() => a % b, offset);
    }

    private static decimal Wrap(Func<decimal> op, int offset)
    {
        try
        {
            return op();
        }
        catch (OverflowException)
        {
            throw new RubedoException("number-out-of-range", "The result is outside the range of a Rubedo number.", offset);
        }
    }

    private static decimal CheckRange(decimal value, int offset)
    {
        if (Math.Abs(value) > RubedoLanguage.MaxMagnitude)
        {
            throw new RubedoException("number-out-of-range", "The result is outside the range of a Rubedo number.", offset);
        }

        return value;
    }
}
