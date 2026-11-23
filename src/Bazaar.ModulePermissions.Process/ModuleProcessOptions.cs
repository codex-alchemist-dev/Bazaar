namespace Bazaar.ModulePermissions.Process;

/// <summary>Limits on the programs one module runs. The defaults are safe; a host may lower them or raise them with care.</summary>
/// <param name="MaxArguments">Most arguments in one run.</param>
/// <param name="MaxArgumentChars">Longest single argument, in characters.</param>
/// <param name="MaxCommandChars">Most characters in all arguments of one run together.</param>
/// <param name="MaxInputChars">Most characters of standard input in one run.</param>
/// <param name="MaxOutputChars">Most characters kept from each of standard output and standard error. The rest is read and dropped, and the result says so.</param>
/// <param name="MaxConcurrent">Most programs the module may have running at once. One more is refused, not queued.</param>
/// <param name="Timeout">Longest one run may take. At the limit the program and every process it started are killed. Null means <see cref="DefaultTimeout"/>.</param>
public sealed record ModuleProcessOptions(
    int MaxArguments = 64,
    int MaxArgumentChars = 4096,
    int MaxCommandChars = 16 * 1024,
    int MaxInputChars = 1024 * 1024,
    int MaxOutputChars = 1024 * 1024,
    int MaxConcurrent = 2,
    TimeSpan? Timeout = null)
{
    /// <summary>The run limit when <see cref="Timeout"/> is not set.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The longest run limit a host may set.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromHours(1);
}
