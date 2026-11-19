using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Bazaar.Rubedo.Views;

using Path = Avalonia.Controls.Shapes.Path;

namespace Bazaar.Client.Views;

/// <summary>Content widgets: text, icon, button, input and badge. Every control that the user acts on carries a label.</summary>
public sealed partial class ViewMapper
{
    /// <summary>The icon set is closed. An icon name outside it fails the node.</summary>
    private static string IconPath(string name) => name switch
    {
        "check" => "M3,8 L6.5,11.5 L13,4.5",
        "close" => "M4,4 L12,12 M12,4 L4,12",
        "warning" => "M8,2.5 L14,13.5 L2,13.5 Z M8,6.5 L8,9.5 M8,11.5 L8,11.6",
        "search" => "M7,2 A5,5 0 1 0 7,12 A5,5 0 1 0 7,2 M11,11 L14,14",
        _ => throw new ViewDeclarationException($"icon '{name}' is not in the icon set."),
    };

    private TextBlock MapText(ViewNode node, Scope scope)
    {
        string style = OptionalLiteral(node, "style", "body");
        bool small = style switch
        {
            "body" => false,
            "small" => true,
            _ => throw new ViewDeclarationException($"text style '{style}' is not a style token."),
        };

        return new TextBlock
        {
            Text = Text(node, "text", scope),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = _theme.Family("font.family.ui"),
            FontSize = _theme.Length(small ? "font.size.small" : "font.size.body"),
            Foreground = _theme.Brush(small ? "color.text.secondary" : "color.text.primary"),
        };
    }

    private Path MapIcon(ViewNode node, Scope scope)
    {
        string label = Label(node, "label", scope);
        var icon = new Path
        {
            Data = Geometry.Parse(IconPath(LiteralString(node, "name"))),
            Stroke = _theme.Brush("color.text.primary"),
            StrokeThickness = 1.5,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
        };
        AutomationProperties.SetName(icon, label);
        return icon;
    }

    private Button MapButton(ViewNode node, Scope scope)
    {
        string label = Label(node, "label", scope);
        ViewAction action = ActionOf(node, "action", scope);
        if (action.Kind == ViewActionKind.SetSetting)
        {
            throw new ViewDeclarationException("setSetting needs a value, so it belongs on an input, not a button.");
        }

        var button = new Button { Content = label };
        button.Click += (_, _) => RunAction(action, value: null);
        return button;
    }

    /// <summary>
    /// A text input. Its change goes through a setSetting action, so the value is written only by the head's action
    /// handler. Attached after the initial text is set, so building the view does not write anything.
    /// </summary>
    private TextBox MapInput(ViewNode node, Scope scope)
    {
        string label = Label(node, "label", scope);
        ViewAction setting = ActionOf(node, "onChange", scope);
        if (setting.Kind != ViewActionKind.SetSetting)
        {
            throw new ViewDeclarationException("onChange must name a setSetting action.");
        }

        var input = new TextBox { Text = Text(node, "value", scope) };
        AutomationProperties.SetName(input, label);
        input.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty)
            {
                RunAction(setting, input.Text ?? string.Empty);
            }
        };
        return input;
    }

    private Border MapBadge(ViewNode node, Scope scope)
    {
        string variant = OptionalLiteral(node, "variant", "neutral");
        (string background, string foreground) = variant switch
        {
            "neutral" => ("color.background.surface", "color.text.primary"),
            "owner" => ("color.badge.owner.background", "color.badge.owner.text"),
            "agent" => ("color.badge.agent.background", "color.badge.agent.text"),
            "system" => ("color.badge.system.background", "color.badge.system.text"),
            _ => throw new ViewDeclarationException($"badge variant '{variant}' is not a variant token."),
        };

        return new Border
        {
            Background = _theme.Brush(background),
            CornerRadius = new CornerRadius(_theme.Length("radius.sm")),
            Padding = new Thickness(_theme.Length("space.xs"), 0),
            Child = new TextBlock
            {
                Text = Text(node, "text", scope),
                FontFamily = _theme.Family("font.family.ui"),
                FontSize = _theme.Length("font.size.small"),
                Foreground = _theme.Brush(foreground),
            },
        };
    }

    /// <summary>
    /// Runs an action for a click or an input change. A failure goes to <see cref="ViewContext.OnActionError"/>, because
    /// the control is already built and cannot show a placeholder.
    /// </summary>
    private async void RunAction(ViewAction action, string? value)
    {
        try
        {
            await ViewActionDispatch.RunAsync(_context.Actions, action, value);
        }
        catch (Exception ex)
        {
            _context.OnActionError(ex);
        }
    }
}
