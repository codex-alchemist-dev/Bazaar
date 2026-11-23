using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Bazaar.Kodo.Tests.Fuzz;

/// <summary>
/// Seeded input generators and the fuzz loop shared by the Kodo fuzz, property and mutation tests (doc 46 section 17.4). Every
/// input comes from a <see cref="Random"/> built from a fixed seed, and every failure message names the target, the seed, the
/// iteration and the input, so a failure replays by running the same seed. Nothing here sleeps or reads a wall clock for a
/// decision; the stopwatch only measures a time bound.
/// </summary>
internal static class FuzzKit
{
    /// <summary>The most one input may take. Far above any real cost, so it only trips on a hang or a blow-up.</summary>
    public static readonly TimeSpan PerInputBudget = TimeSpan.FromSeconds(2);

    /// <summary>Names that appear in the Kodo wire shapes, so generated JSON reaches the deserializer's member handling.</summary>
    public static readonly string[] WireNames =
    [
        "schemaVersion", "revision", "status", "journalHead", "components", "id", "state", "cause", "since", "type", "at", "stateChange",
        "snapshot", "previous", "underlyingState", "score", "asOf", "controls", "control", "enabled", "disabledReason", "servers",
        "serverId", "displayName", "version", "openAlerts", "incidentOpen",
    ];

    /// <summary>A generator seeded with a fixed value. Not security material.</summary>
    public static Random Rng(int seed) => new(seed);

    /// <summary>Random bytes of a random length up to <paramref name="maxLength"/>.</summary>
    public static byte[] RandomBytes(Random random, int maxLength)
    {
        byte[] bytes = new byte[random.Next(0, maxLength + 1)];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A mutated copy of <paramref name="valid"/>: a bit flip, a byte change, a cut, an insert, a delete or a repeated slice.</summary>
    public static byte[] Mutate(Random random, byte[] valid)
    {
        List<byte> bytes = [.. valid];
        int edits = random.Next(1, 4);
        for (int edit = 0; edit < edits; edit++)
        {
            if (bytes.Count == 0)
            {
                bytes.Add((byte)random.Next(256));
                continue;
            }

            int at = random.Next(bytes.Count);
            switch (random.Next(6))
            {
                case 0:
                    bytes[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    bytes[at] = (byte)random.Next(256);
                    break;
                case 2:
                    bytes.RemoveRange(at, bytes.Count - at);
                    break;
                case 3:
                    bytes.InsertRange(at, RandomBytes(random, 8));
                    break;
                case 4:
                    bytes.RemoveRange(at, Math.Min(random.Next(1, 9), bytes.Count - at));
                    break;
                default:
                    int length = Math.Min(random.Next(1, 33), bytes.Count - at);
                    bytes.InsertRange(at, bytes.GetRange(at, length));
                    break;
            }
        }

        return [.. bytes];
    }

    /// <summary>A random JSON document. Member names come from <paramref name="names"/> and may repeat; values may be any JSON type.</summary>
    public static string RandomJson(Random random, IReadOnlyList<string> names, int depth = 0)
    {
        int kind = random.Next(depth >= 4 ? 6 : 8);
        switch (kind)
        {
            case 0:
                return "null";
            case 1:
                return random.Next(2) == 0 ? "true" : "false";
            case 2:
                return random.Next(-5, 100000).ToString(CultureInfo.InvariantCulture);
            case 3:
                return new[] { "1e999", "-0", "1.5", "9223372036854775808", "0.000001", "12345678901234567890123" }[random.Next(6)];
            case 4:
                return "\"" + JsonText(random, names) + "\"";
            case 5:
                return "\"" + new[] { "2026-11-18T12:00:00Z", "0001-01-01T00:00:00Z", "9999-12-31T23:59:59.9999999+00:00", "not a date", "healthy" }[random.Next(5)] + "\"";
            case 6:
                StringBuilder array = new("[");
                int count = random.Next(0, 4);
                for (int i = 0; i < count; i++)
                {
                    array.Append(i > 0 ? "," : string.Empty).Append(RandomJson(random, names, depth + 1));
                }

                return array.Append(']').ToString();
            default:
                StringBuilder obj = new("{");
                int members = random.Next(0, 6);
                for (int i = 0; i < members; i++)
                {
                    obj.Append(i > 0 ? "," : string.Empty)
                        .Append('"').Append(names[random.Next(names.Count)]).Append("\":")
                        .Append(RandomJson(random, names, depth + 1));
                }

                return obj.Append('}').ToString();
        }
    }

    /// <summary>The input as short hex, for a failure message.</summary>
    public static string Hex(ReadOnlySpan<byte> input) =>
        Convert.ToHexString(input[..Math.Min(input.Length, 96)]) + (input.Length > 96 ? $"...({input.Length} bytes)" : string.Empty);

    /// <summary>
    /// Runs <paramref name="target"/> over <paramref name="iterations"/> generated inputs. The target may throw only the
    /// <paramref name="allowed"/> types (or none); any other exception, or an input that takes longer than
    /// <see cref="PerInputBudget"/>, fails with the seed, the iteration and the input.
    /// </summary>
    public static void Run(
        string name,
        int seed,
        int iterations,
        Func<Random, int, byte[]> next,
        Action<byte[]> target,
        params Type[] allowed)
    {
        Random random = Rng(seed);
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            byte[] input = next(random, iteration);
            Stopwatch watch = Stopwatch.StartNew();
            try
            {
                target(input);
            }
            catch (Exception error) when (allowed.Any(type => type.IsInstanceOfType(error)))
            {
                // A documented rejection.
            }
            catch (Exception error)
            {
                Assert.Fail($"{name}: unexpected {error.GetType().Name} ({error.Message}) at seed={seed} iteration={iteration} input={Hex(input)}");
            }

            Assert.True(
                watch.Elapsed < PerInputBudget,
                $"{name}: input took {watch.Elapsed.TotalMilliseconds:F0} ms at seed={seed} iteration={iteration} input={Hex(input)}");
        }
    }

    /// <summary>Picks a generator by iteration: pure random bytes, a mutation of <paramref name="valid"/>, or generated JSON.</summary>
    public static byte[] Mixed(Random random, int iteration, byte[] valid, IReadOnlyList<string> names) =>
        (iteration % 4) switch
        {
            0 => RandomBytes(random, 96),
            1 or 2 => Mutate(random, valid),
            _ => Encoding.UTF8.GetBytes(RandomJson(random, names)),
        };

    private static string JsonText(Random random, IReadOnlyList<string> names) =>
        random.Next(6) switch
        {
            0 => names[random.Next(names.Count)],
            1 => string.Empty,
            2 => "\\u0000\\n\\\"\\\\",
            3 => "\\ud800",
            4 => "caf\\u00e9 \\ud83d\\ude00",
            _ => new string((char)random.Next('a', 'z' + 1), random.Next(1, 40)),
        };
}
