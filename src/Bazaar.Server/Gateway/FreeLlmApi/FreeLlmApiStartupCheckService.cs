using Bazaar.Server.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.FreeLlmApi;

/// <summary>
/// Checks at start that the configured FreeLLMAPI key resolves, so a missing variable or file stops the start with a
/// clear error instead of failing every call later. The message names the configuration key and the reference, never the value.
/// Registered only by <see cref="FreeLlmApiComponent"/>, and only when FreeLLMAPI is enabled.
/// </summary>
public sealed class FreeLlmApiStartupCheckService(IOptions<FreeLlmApiOptions> options, ISecretResolver resolver) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        FreeLlmApiStartupCheck.VerifyKeyAsync(options.Value, resolver, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
