namespace Bazaar.Conduit;

/// <summary>
/// HTTP implementation of <see cref="IOutsideProbe"/>: a GET with a 5 second timeout. Transport failures and timeouts
/// are reported as false. Cancellation by the caller is not swallowed.
/// </summary>
public sealed class HttpOutsideProbe(HttpClient httpClient) : IOutsideProbe
{
    /// <summary>Time allowed for one probe request.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<bool> ProbeAsync(Uri probe, string expectedKeyHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrEmpty(expectedKeyHash);

        using var timeout = new CancellationTokenSource(ProbeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probe);
            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            return body.Contains(expectedKeyHash, StringComparison.Ordinal);
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
