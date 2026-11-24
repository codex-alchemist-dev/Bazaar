using Bazaar.Client.Athanor.Bridge;

namespace Bazaar.Client.Athanor.Gtk;

/// <summary>
/// Settings of the GTK helper proxy (doc 57 section 2.1). Every limit has a safe default and every default is
/// operator-changeable through the head that builds this record.
/// </summary>
public sealed record GtkEditorHostOptions
{
    /// <summary>How long the helper has to send <c>helper.hello</c> and the page's <c>ready</c> after it starts.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the proxy pings the helper. <see cref="TimeSpan.Zero"/> turns pings off.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a helper may stay silent (no pong) before it is treated as hung and killed.</summary>
    public TimeSpan PingTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one write to the helper may block before the helper is treated as hung and killed.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a dying helper may take to exit before it is killed.</summary>
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a clean shutdown waits for the helper to leave after <c>helper.shutdown</c>.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Restarts allowed in a row before the host stays down (<see cref="EditorHelperState.Faulted"/>).</summary>
    public int MaxRestarts { get; init; } = 5;

    /// <summary>The wait before the first restart. It doubles for each further failure.</summary>
    public TimeSpan BackoffInitial { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait between restarts.</summary>
    public TimeSpan BackoffMax { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A helper that ran this long counts as healthy and resets the failure count.</summary>
    public TimeSpan StableAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Allow a helper that runs without the WebKit sandbox (root containers, CI). False in production: the proxy
    /// refuses a helper whose <c>helper.hello</c> says <c>sandbox: false</c>.
    /// </summary>
    public bool AllowNoSandbox { get; init; }

    /// <summary>The bridge payload limit in UTF-8 bytes (doc 57 section 4).</summary>
    public long MaxPayloadBytes { get; init; } = BridgeCodec.DefaultMaxPayloadBytes;

    /// <summary>Throws <see cref="ArgumentException"/> when a value is out of range.</summary>
    public void Validate()
    {
        Positive(HandshakeTimeout, nameof(HandshakeTimeout));
        Positive(PingTimeout, nameof(PingTimeout));
        Positive(WriteTimeout, nameof(WriteTimeout));
        Positive(ExitGrace, nameof(ExitGrace));
        Positive(ShutdownGrace, nameof(ShutdownGrace));
        Positive(BackoffInitial, nameof(BackoffInitial));
        Positive(BackoffMax, nameof(BackoffMax));
        if (PingInterval < TimeSpan.Zero)
        {
            throw new ArgumentException("PingInterval cannot be negative.", nameof(PingInterval));
        }

        if (StableAfter < TimeSpan.Zero)
        {
            throw new ArgumentException("StableAfter cannot be negative.", nameof(StableAfter));
        }

        if (MaxRestarts < 0)
        {
            throw new ArgumentException("MaxRestarts cannot be negative.", nameof(MaxRestarts));
        }

        if (MaxPayloadBytes < 1)
        {
            throw new ArgumentException("MaxPayloadBytes must be positive.", nameof(MaxPayloadBytes));
        }
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} must be positive.", name);
        }
    }
}
