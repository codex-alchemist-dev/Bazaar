using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Bazaar.Diagnostics.Fingerprinting;

/// <summary>
/// Deterministic grouping key for a failure (doc 43 section 8.2, algorithm <c>fp1</c>). The same input gives
/// the same string on every machine. Any change to the algorithm changes the golden tests and needs a new
/// algorithm prefix, because existing groups are keyed by these strings.
/// </summary>
public static class Fingerprint
{
    /// <summary>Algorithm prefix. Written as the first line of the canonical input and before the colon.</summary>
    public const string Prefix = "fp1";

    private const int _maxFrames = 5;
    private const int _hashBytes = 16;

    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex _arity = new("`[0-9]+", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _asyncWrapper = new(@"<([^>]+)>d__[0-9]+", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _asyncMethod = new(@"^<([^>]+)>d__[0-9]+(?:\.MoveNext)?$", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _localFunction = new(@"^<[^>]+>g__([^|]+)\|[0-9]+_[0-9]+$", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _lambda = new(@"^<([^>]+)>b__[0-9]+_[0-9]+$", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _guid = new(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _quoted = new(@"""[^""]*""|'[^']*'", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _path = new(@"(?:[A-Za-z]:\\|/)[^\s""']+", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _hex = new(@"\b[0-9a-fA-F]{6,}\b", RegexOptions.CultureInvariant, _matchTimeout);
    private static readonly Regex _decimal = new(@"[0-9]+", RegexOptions.CultureInvariant, _matchTimeout);

    /// <summary>Returns the <c>fp1:</c> fingerprint: the prefix and 32 lowercase hex characters (the first 16 bytes of SHA-256).</summary>
    public static string Compute(FingerprintInput input)
    {
        var canonical = CanonicalInput(input).Normalize(NormalizationForm.FormC);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Prefix + ":" + Convert.ToHexStringLower(hash.AsSpan(0, _hashBytes));
    }

    /// <summary>
    /// The exact text that is hashed. Each line ends with a newline. Exposed so the golden tests can show what
    /// a change of the algorithm would alter.
    /// </summary>
    public static string CanonicalInput(FingerprintInput input)
    {
        var builder = new StringBuilder();
        builder.Append(Prefix).Append('\n');
        builder.Append(string.IsNullOrEmpty(input.ErrorCode) ? "-" : input.ErrorCode).Append('\n');
        builder.Append(NormaliseType(input.ErrorType)).Append('\n');

        foreach (var frame in SelectFrames(input.Frames))
        {
            builder.Append(frame.ModuleId).Append('|')
                .Append(NormaliseType(frame.Type)).Append('|')
                .Append(NormaliseMethod(frame.Method)).Append('\n');
        }

        if (string.IsNullOrEmpty(input.ErrorCode))
        {
            builder.Append(NormaliseMessage(FirstLine(input.Message))).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>App frames in order, at most five. Without app frames, the first five frames of any kind.</summary>
    private static IEnumerable<FingerprintFrame> SelectFrames(IReadOnlyList<FingerprintFrame> frames)
    {
        var app = frames.Where(frame => frame.App).Take(_maxFrames).ToArray();
        return app.Length > 0 ? app : frames.Take(_maxFrames);
    }

    /// <summary>Removes generic arity (<c>Name`1</c>) and state-machine wrappers (<c>&lt;Name&gt;d__12</c> becomes <c>Name</c>).</summary>
    private static string NormaliseType(string type)
    {
        var withoutArity = _arity.Replace(type, string.Empty);
        return _asyncWrapper.Replace(withoutArity, match => match.Groups[1].Value);
    }

    /// <summary>
    /// Strips the parameter list and maps compiler-generated names to their source names: async state machines
    /// to the method, local functions to their name, lambdas to <c>Owner.lambda</c>.
    /// </summary>
    private static string NormaliseMethod(string method)
    {
        var name = method.Trim();
        var parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0)
        {
            name = name[..parameters];
        }

        var asyncMatch = _asyncMethod.Match(name);
        if (asyncMatch.Success)
        {
            return asyncMatch.Groups[1].Value;
        }

        var localMatch = _localFunction.Match(name);
        if (localMatch.Success)
        {
            return localMatch.Groups[1].Value;
        }

        var lambdaMatch = _lambda.Match(name);
        if (lambdaMatch.Success)
        {
            return lambdaMatch.Groups[1].Value + ".lambda";
        }

        return name;
    }

    /// <summary>
    /// Replaces the parts of a message that vary between occurrences: GUIDs, quoted strings, paths, long hex
    /// runs and decimal numbers. The order matters, since each step leaves placeholders the next one skips.
    /// </summary>
    private static string NormaliseMessage(string message)
    {
        var text = _guid.Replace(message.Trim(), "<guid>");
        text = _quoted.Replace(text, "<str>");
        text = _path.Replace(text, "<path>");
        text = _hex.Replace(text, "<hex>");
        return _decimal.Replace(text, "<n>");
    }

    private static string FirstLine(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var line = message.Split('\n', 2)[0];
        return line.TrimEnd('\r');
    }
}
