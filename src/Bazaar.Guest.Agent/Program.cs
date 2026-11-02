using Bazaar.Guest.Agent;

// stdin and stdout carry the guest protocol, so every diagnostic goes to stderr.
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

var transport = new StreamGuestTransport(Console.OpenStandardInput(), Console.OpenStandardOutput());
var agent = new GuestAgent(options, transport, log: Console.Error);
return await agent.RunAsync(CancellationToken.None);
