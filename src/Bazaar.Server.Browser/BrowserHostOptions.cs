namespace Bazaar.Server.Browser;

/// <summary>Settings for <see cref="GuestPipeBrowserHost"/>.</summary>
public sealed record BrowserHostOptions(TimeSpan CdpTimeout)
{
    public BrowserHostOptions()
        : this(TimeSpan.FromSeconds(10))
    {
    }
}
