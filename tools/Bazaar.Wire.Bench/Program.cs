using System.Globalization;
using Bazaar.Wire.Bench;

// Spike S-WTX harness (doc 90 section 10). Modes:
//   Bazaar.Wire.Bench --codec <name> --run <n>     measure one codec in this process, print RESULT lines
//   Bazaar.Wire.Bench --all [--runs 3] [--out raw.tsv] [--md tables.md]
//                                                  start one process per codec and run, then print tables
//   Bazaar.Wire.Bench --transports [--runs 3] [--condition clean] [--only ws-h1,quic] [--duration 10] [--rate 1000] [--out raw.tsv] [--md tables.md]
//                                                  start one process per transport and run (loopback), then print tables
//   Bazaar.Wire.Bench --transport <name> --run <n> [--condition clean] [--duration 10] [--rate 1000]
//                                                  measure one transport in this process
if (args.Length >= 2 && args[0] == "--codec")
{
    var run = args.Length >= 4 && args[2] == "--run" ? int.Parse(args[3], CultureInfo.InvariantCulture) : 1;
    return CodecRunner.Run(args[1], run);
}

string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

if (args.Length >= 1 && args[0] == "--all")
{
    var runs = int.Parse(Option("--runs") ?? "3", CultureInfo.InvariantCulture);
    return Orchestrator.Run(runs, Option("--out"), Option("--md"));
}

if (args.Length >= 1 && args[0] is "--transports" or "--transport")
{
    var defaults = TransportRunOptions.Default;
    var options = defaults with
    {
        Condition = Option("--condition") ?? defaults.Condition,
        Duration = TimeSpan.FromSeconds(double.Parse(Option("--duration") ?? "10", CultureInfo.InvariantCulture)),
        Rate = int.Parse(Option("--rate") ?? "1000", CultureInfo.InvariantCulture),
    };
    if (args[0] == "--transport")
    {
        return await TransportRunner.RunAsync(args[1], int.Parse(Option("--run") ?? "1", CultureInfo.InvariantCulture), options);
    }

    var only = Option("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [.. TransportScenario.Names];
    return TransportOrchestrator.Run(int.Parse(Option("--runs") ?? "3", CultureInfo.InvariantCulture), options, only, Option("--out"), Option("--md"));
}

Console.Error.WriteLine("usage: Bazaar.Wire.Bench --codec <json|cbor|msgpack|protobuf|memorypack> --run <n> | --all [--runs 3] [--out raw.tsv] [--md tables.md] | --transports [--runs 3] [--condition clean] [--only ws-h1,quic] [--duration 10] [--rate 1000] [--out raw.tsv] [--md tables.md] | --transport <name> --run <n>");
return 2;
