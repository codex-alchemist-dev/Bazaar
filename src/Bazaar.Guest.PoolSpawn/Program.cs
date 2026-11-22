using System.Collections;
using System.ComponentModel;
using System.Globalization;
using Bazaar.Guest.PoolSpawn;

// bazaar-pool-spawn --fd3 PATH --fd4 PATH -- PROGRAM ARGS...
// Opens the two FIFOs the pool host made (first read-only, second write-only), puts them on descriptors 3 and 4, and execs the
// program, so Chromium's --remote-debugging-pipe finds its channel. No shell is involved and no argument value is ever printed.
// Exit codes: 64 bad command line, 74 a pipe could not be opened or placed, 126 the program could not be run, 127 it was not found,
// 1 not Linux.
//
// bazaar-pool-spawn --probe-connect IPV4:PORT --timeout-ms N
// Probe mode (docs/design/59-desktop-density-and-guest-display.md, section 7): the pool host runs this as a slot's uid, in the slot's
// network namespace, to see whether one TCP connection to an address the slot must not reach gets through. It connects, closes, and
// exits: 0 connected, 20 timed out, 21 refused, 22 unreachable, 23 denied, 70 other socket error, 64 bad command line, 1 not Linux.
// The host reads 20 to 23 as blocked and everything else as a failed probe. Nothing is sent and no address is printed.
const string name = "bazaar-pool-spawn";
const int ioError = 74;
const int notExecutable = 126;
const int notFound = 127;

if (!OperatingSystem.IsLinux())
{
    await Console.Error.WriteLineAsync(name + ": runs on Linux only.");
    return 1;
}

if (ProbeArguments.IsProbe(args))
{
    if (!ProbeArguments.TryParse(args, out var probe, out var probeError) || probe is null)
    {
        await Console.Error.WriteLineAsync(name + ": " + ProbeArguments.Describe(probeError));
        return SpawnArguments.UsageExitCode;
    }

    return (int)ProbeConnect.Run(probe);
}

if (!SpawnArguments.TryParse(args, out var parsed, out var error) || parsed is null)
{
    await Console.Error.WriteLineAsync(name + ": " + SpawnArguments.Describe(error));
    return SpawnArguments.UsageExitCode;
}

var posix = new Posix();
try
{
    // The host holds both ends open before this starts, so neither open blocks.
    var read = Posix.OpenForRead(parsed.Fd3Path);
    var write = Posix.OpenForWrite(parsed.Fd4Path);
    FdShuffle.Run(posix, read, write);
}
catch (Win32Exception ex)
{
    // The call name and errno only: never the path.
    await Console.Error.WriteLineAsync(name + ": " + ex.Message + " (errno " + ex.NativeErrorCode.ToString(CultureInfo.InvariantCulture) + ").");
    return ioError;
}

var environment = new List<string>();
foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
{
    environment.Add(entry.Key + "=" + entry.Value);
}

var errno = Posix.Exec(parsed.Program, parsed.Argv, environment);
await Console.Error.WriteLineAsync(name + ": could not run the program (errno " + errno.ToString(CultureInfo.InvariantCulture) + ").");
return Posix.IsNotFound(errno) ? notFound : notExecutable;
