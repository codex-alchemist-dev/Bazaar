using Bazaar.Guest.Agent;

// With the stdio transport, stdin and stdout carry the guest protocol, so every diagnostic goes to stderr.
GuestAgentOptions options;
try
{
    options = GuestAgentOptions.Parse(args);
}
catch (ArgumentException ex)
{
    await Console.Error.WriteLineAsync($"bazaar-guest-agent: {ex.Message}");
    return 2;
}

IGuestTransport transport;
try
{
    transport = options.Transport switch
    {
        GuestTransportKind.HvSocket => await HvSocketGuestTransport.ConnectAsync(
            options.HvSocketServiceId, options.ConnectTimeout, Console.Error, CancellationToken.None),
        GuestTransportKind.Vsock => await VsockGuestTransport.ConnectAsync(
            options.VsockHostCid, options.VsockPort, options.ConnectTimeout, Console.Error, CancellationToken.None),
        _ => new StreamGuestTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()),
    };
}
catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
{
    await Console.Error.WriteLineAsync($"bazaar-guest-agent: {ex.Message}");
    return 3;
}

try
{
    var agent = new GuestAgent(options, transport, log: Console.Error);
    return await agent.RunAsync(CancellationToken.None);
}
finally
{
    (transport as IDisposable)?.Dispose();
}
