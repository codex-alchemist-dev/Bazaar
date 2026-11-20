using Bazaar.Guest.Pointer;

// The only inputs are the arguments and the two Wayland environment variables. Exit codes: 0 done, 1 the compositor failed or
// is missing, 2 a rejected command line. Diagnostics are one line on stderr and never include more than the tool's own arguments.
if (!PointerCommandParser.TryParse(args, out PointerCommand? command, out string error))
{
    Console.Error.WriteLine($"bazaar-pointer: {error}");
    Console.Error.WriteLine(PointerCommandParser.Usage);
    return 2;
}

try
{
    string path = WaylandSession.SocketPath(
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    WaylandSession.Run(path, command!);
    return 0;
}
catch (PointerToolException e)
{
    Console.Error.WriteLine($"bazaar-pointer: {e.Message}");
    return 1;
}
