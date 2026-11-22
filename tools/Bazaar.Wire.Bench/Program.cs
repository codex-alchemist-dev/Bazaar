using Bazaar.Wire.Bench;

// Spike S-WTX codec harness (doc 90 section 10). Two modes:
//   Bazaar.Wire.Bench --codec <name> --run <n>     measure one codec in this process, print RESULT lines
//   Bazaar.Wire.Bench --all [--runs 3] [--out raw.tsv] [--md tables.md]
//                                                  start one process per codec and run, then print tables
if (args.Length >= 2 && args[0] == "--codec")
{
    var run = args.Length >= 4 && args[2] == "--run" ? int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 1;
    return CodecRunner.Run(args[1], run);
}

if (args.Length >= 1 && args[0] == "--all")
{
    var runs = 3;
    string? raw = null;
    string? md = null;
    for (var i = 1; i < args.Length - 1; i++)
    {
        if (args[i] == "--runs") runs = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture);
        else if (args[i] == "--out") raw = args[i + 1];
        else if (args[i] == "--md") md = args[i + 1];
    }

    return Orchestrator.Run(runs, raw, md);
}

Console.Error.WriteLine("usage: Bazaar.Wire.Bench --codec <json|cbor|msgpack|protobuf|memorypack> --run <n> | --all [--runs 3] [--out raw.tsv] [--md tables.md]");
return 2;
