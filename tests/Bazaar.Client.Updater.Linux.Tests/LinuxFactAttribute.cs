using System.Runtime.CompilerServices;

namespace Bazaar.Client.Updater.Linux.Tests;

internal static class Platform
{
    public static bool Linux => OperatingSystem.IsLinux();
}

/// <summary>A fact that runs only on Linux (symbolic links, real gpg and tar semantics); the solution also builds and tests on Windows.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = "Needs Linux.";
        SkipUnless = nameof(Platform.Linux);
        SkipType = typeof(Platform);
    }
}
