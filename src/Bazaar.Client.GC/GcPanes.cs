using Bazaar.Client.Core.Panes;
using Bazaar.Client.GC.Abstractions;

namespace Bazaar.Client.GC;

/// <summary>The GC client module: the pane module a head composes to ship the group chat (doc 40, step 7; doc 41b, section 2a).</summary>
public static class GcPanes
{
    /// <summary>The group chat pane. Heads list it after the Client.Core panes, which keeps the phone pager order (doc 29).</summary>
    public static IPaneModule Module { get; } = new GcPaneModule();

    /// <summary>
    /// The registry modules add presence chip adornments to. A head puts it in the pane context with
    /// <see cref="WithPresenceAdornments"/> so feature modules reach it as <see cref="IPresenceAdornmentRegistry"/>.
    /// </summary>
    public static PresenceAdornmentRegistry CreatePresenceAdornments() => new();

    /// <summary>Returns a copy of <paramref name="context"/> that carries <paramref name="registry"/> as <see cref="IPresenceAdornmentRegistry"/>.</summary>
    public static PaneModuleContext WithPresenceAdornments(this PaneModuleContext context, IPresenceAdornmentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(registry);

        return context.With(registry);
    }

    /// <summary>
    /// Returns a copy of <paramref name="context"/> that carries <paramref name="chat"/> for the GC pane. A null chat
    /// returns the context unchanged, so the pane shows its empty state.
    /// </summary>
    public static PaneModuleContext WithChat(this PaneModuleContext context, ChatViewModel? chat)
    {
        ArgumentNullException.ThrowIfNull(context);

        return chat is null ? context : context.With(chat);
    }
}
