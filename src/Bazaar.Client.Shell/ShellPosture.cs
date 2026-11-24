using Avalonia.Controls;
using Avalonia.Layout;
using Bazaar.Client.Motion.Avalonia;
using Bazaar.Client.Posture;
using Bazaar.Client.Posture.Avalonia;

namespace Bazaar.Client.Shell;

/// <summary>
/// The shell's posture layout (doc 93, Foldable unfold transition and postures): the chat in the first slot and the current pane in the
/// second, arranged by a <see cref="PosturePanel"/> and animated by a <see cref="PostureTransitionDriver"/>. Flat and at least 600 dp
/// wide (<see cref="PostureThresholds.DualPaneMinWidth"/>) is dual-pane; narrower, or with no pane variant for the posture, is the
/// single chat pane. A head with no fold sensor passes <see cref="FlatPostureSource"/>, so the window width alone decides.
/// </summary>
public sealed class ShellPosture : IDisposable
{
    private readonly PostureTransitionDriver _driver;

    private ShellPosture(PosturePanel panel, ContentControl chatSlot, ContentControl paneSlot, PostureTransitionDriver driver)
    {
        Panel = panel;
        ChatSlot = chatSlot;
        PaneSlot = paneSlot;
        _driver = driver;
    }

    /// <summary>The panel to place in the window. Its first child is the chat slot and its second the pane slot.</summary>
    public PosturePanel Panel { get; }

    /// <summary>The first slot, for the group chat.</summary>
    public ContentControl ChatSlot { get; }

    /// <summary>The second slot, for the current pane. Empty until the head has one to show.</summary>
    public ContentControl PaneSlot { get; }

    /// <summary>The layout the transition has reached.</summary>
    public PostureFrame LastFrame => _driver.LastFrame;

    /// <summary>Raised after each transition frame is applied.</summary>
    public event EventHandler<PostureFrame>? FrameApplied
    {
        add => _driver.FrameApplied += value;
        remove => _driver.FrameApplied -= value;
    }

    /// <summary>Builds the panel and starts following <paramref name="source"/>.</summary>
    /// <param name="chat">The group chat, or null for an empty chat slot.</param>
    /// <param name="pane">The current pane, or null for an empty pane slot.</param>
    /// <param name="frames">Frame source for the transition; defaults to the panel's top level.</param>
    /// <param name="variants">The layout variants the company has; defaults to all of them.</param>
    public static ShellPosture Mount(
        Control? chat,
        Control? pane,
        IPostureSource source,
        MotionSettings motion,
        IFrameDriver? frames = null,
        Func<LayoutVariants>? variants = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(motion);

        var chatSlot = new ContentControl { Content = chat, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var paneSlot = new ContentControl { Content = pane, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var panel = new PosturePanel();
        panel.Children.Add(chatSlot);
        panel.Children.Add(paneSlot);
        var driver = new PostureTransitionDriver(panel, source, motion, frames ?? new TopLevelFrameDriver(panel), variants);
        return new ShellPosture(panel, chatSlot, paneSlot, driver);
    }

    public void Dispose() => _driver.Dispose();
}
