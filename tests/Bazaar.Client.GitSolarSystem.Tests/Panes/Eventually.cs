using Avalonia.Threading;

namespace Bazaar.Client.GitSolarSystem.Tests.Panes;

/// <summary>Polls a condition until it holds. The feed runs on its own thread, so tests wait for its effects.</summary>
internal static class Eventually
{
    public static void Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(because);
            }

            Thread.Sleep(5);
        }
    }

    /// <summary>Like <see cref="Until"/>, but runs the UI thread's queued work before each check, for headless view tests.</summary>
    public static void UntilUiIdle(Func<bool> condition, string because) =>
        Until(() =>
        {
            Dispatcher.UIThread.RunJobs();
            return condition();
        }, because);
}
