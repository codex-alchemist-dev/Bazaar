using System.Globalization;

namespace Bazaar.Guest.PoolSpawn;

/// <summary>Why the probe command line was refused. Never carries argument text.</summary>
public enum ProbeParseError
{
    None,
    WrongShape,
    BadTarget,
    BadTimeout,
}

/// <summary>
/// The parsed command line of <c>bazaar-pool-spawn --probe-connect IPV4:PORT --timeout-ms N</c> (docs/design/59-desktop-density-and-guest-display.md,
/// section 7). It is one fixed shape with no options to reorder or repeat: the pool host runs it as the slot's uid, inside the slot's
/// network namespace, to learn whether one TCP connection gets through. The target is an IPv4 literal of four plain decimal parts;
/// names are never resolved and shorthand forms are refused.
/// </summary>
internal sealed record ProbeArguments(byte[] Address, int Port, int TimeoutMs)
{
    /// <summary>The first argument that selects probe mode.</summary>
    public const string Option = "--probe-connect";

    /// <summary>The shortest connect timeout accepted, in milliseconds.</summary>
    public const int MinTimeoutMs = 50;

    /// <summary>The longest connect timeout accepted, in milliseconds.</summary>
    public const int MaxTimeoutMs = 60_000;

    private const string _timeoutOption = "--timeout-ms";

    /// <summary>True when the arguments ask for probe mode, whether or not they are valid.</summary>
    public static bool IsProbe(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Count > 0 && args[0] == Option;
    }

    /// <summary>Parses the arguments. On failure <paramref name="error"/> names the reason and the result is null.</summary>
    public static bool TryParse(IReadOnlyList<string> args, out ProbeArguments? result, out ProbeParseError error)
    {
        ArgumentNullException.ThrowIfNull(args);
        result = null;
        if (args.Count != 4 || args[0] != Option || args[2] != _timeoutOption)
        {
            error = ProbeParseError.WrongShape;
            return false;
        }

        if (!TryParseTarget(args[1], out var address, out var port))
        {
            error = ProbeParseError.BadTarget;
            return false;
        }

        if (!TryParseDigits(args[3], 6, out var timeout) || timeout is < MinTimeoutMs or > MaxTimeoutMs)
        {
            error = ProbeParseError.BadTimeout;
            return false;
        }

        result = new ProbeArguments(address!, port, timeout);
        error = ProbeParseError.None;
        return true;
    }

    /// <summary>A fixed message for a refusal. It never contains argument text.</summary>
    public static string Describe(ProbeParseError error) => error switch
    {
        ProbeParseError.WrongShape => "usage: --probe-connect IPV4:PORT --timeout-ms N",
        ProbeParseError.BadTarget => "the target must be a dotted-quad IPv4 address and a port from 1 to 65535",
        ProbeParseError.BadTimeout => "the timeout must be a whole number of milliseconds from 50 to 60000",
        _ => "invalid arguments",
    };

    private static bool TryParseTarget(string text, out byte[]? address, out int port)
    {
        address = null;
        port = 0;
        var colon = text.LastIndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        if (!TryParseDigits(text[(colon + 1)..], 5, out port) || port is < 1 or > 65535)
        {
            return false;
        }

        // Exactly four plain decimal parts, no leading zeros (they read as octal in some parsers), each 0 to 255.
        var parts = text[..colon].Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (parts[i].Length > 1 && parts[i][0] == '0')
            {
                return false;
            }

            if (!TryParseDigits(parts[i], 3, out var octet) || octet > 255)
            {
                return false;
            }

            bytes[i] = (byte)octet;
        }

        address = bytes;
        return true;
    }

    private static bool TryParseDigits(string text, int maxLength, out int value)
    {
        value = 0;
        return text.Length >= 1 && text.Length <= maxLength && text.All(char.IsAsciiDigit) && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
