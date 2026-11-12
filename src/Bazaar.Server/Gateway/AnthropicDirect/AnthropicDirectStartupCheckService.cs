using Bazaar.Server.Secrets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Bazaar.Server.Gateway.AnthropicDirect;

/// <summary>
/// Runs <see cref="AnthropicDirectStartupCheck"/> when the host starts. Registered only by
/// <see cref="AnthropicDirectComponent"/>, and only when the source is enabled.
/// </summary>
public sealed class AnthropicDirectStartupCheckService(IOptions<AnthropicDirectOptions> options, ISecretResolver resolver) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        AnthropicDirectStartupCheck.VerifyKeyAsync(options.Value, resolver, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
