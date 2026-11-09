namespace Bazaar.Server.Transports.Tests.Cloudflared;

/// <summary>Waiting helpers for the background supervisor. The clock is fake; these poll on real time.</summary>
internal static class CloudflaredTestSupport
{
    /// <summary>Polls <paramref name="condition"/> until it holds or the wait times out.</summary>
    public static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Gives background work a moment to act, so a negative assertion means something.</summary>
    public static Task Settle() => Task.Delay(30);
}
