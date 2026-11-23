using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.Mail.Abstractions;

namespace Bazaar.Client.Mail.Panes;

/// <summary>
/// The pane bodies of the mail panes. Each pane module registers its body with the pane registry. The views build plain
/// <see cref="TextBlock"/>s and never a link, a button, an image or a web view: a mail pane is display-only, and no model
/// string is parsed as markup.
/// </summary>
internal static class MailPaneViews
{
    // Theme tokens (one theme file drives every head). Views bind to these, never to a fixed colour.
    private const string _borderToken = "Bazaar.brush.color.border.default";
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    internal static StackPanel Quarantine(MailQuarantinePaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 10 };
        AutomationProperties.SetName(body, "Mail quarantine");
        body.Children.Add(Note("Metadata only. Message bodies are not shown."));

        var digest = pane.Digest;
        body.Children.Add(new TextBlock
        {
            Text = $"Digest for {digest.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
            FontWeight = FontWeight.SemiBold,
        });
        body.Children.Add(new TextBlock
        {
            Text = $"Held {digest.Held} · Released {digest.Released} · Purged {digest.Purged} · Reported {digest.Reported}",
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });

        if (digest.TopSenderDomains.Count > 0)
        {
            body.Children.Add(new TextBlock { Text = "Top sender domains", FontWeight = FontWeight.SemiBold });
            foreach (var domain in digest.TopSenderDomains)
            {
                body.Children.Add(Indented($"{domain.Domain}: {domain.Count}"));
            }
        }

        if (pane.Rows.Count == 0)
        {
            body.Children.Add(Note("Nothing is held."));
        }

        foreach (var row in pane.Rows)
        {
            var state = MailFormat.QuarantineState(row.State);
            var content = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = $"{row.SenderDomain} · {state}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Reasons: {MailFormat.Reasons(row.ReasonCodes)}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Auth: {row.AuthResult} · Received {MailFormat.Received(row.ReceivedAt)}", TextWrapping = TextWrapping.Wrap },
                },
            };
            body.Children.Add(Card(content, $"Quarantined mail from {row.SenderDomain}, {state}"));
        }

        return body;
    }

    internal static StackPanel Mailbox(MailMailboxPaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 10 };
        AutomationProperties.SetName(body, "Mail mailbox");
        body.Children.Add(Note("Plain text only. HTML and remote images are not loaded. Links are not opened."));

        if (pane.Messages.Count == 0)
        {
            body.Children.Add(Note("No messages."));
        }

        foreach (var message in pane.Messages)
        {
            var content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"From: {message.From}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Subject: {message.Subject}", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Received {MailFormat.Received(message.ReceivedAt)}", TextWrapping = TextWrapping.Wrap },
                    // Plain text: the server's string is set as text, so markup in it shows as typed.
                    new TextBlock { Text = message.Text, TextWrapping = TextWrapping.Wrap },
                },
            };

            // A link is shown by its real host and the URL as text. Nothing opens it.
            foreach (var url in message.Links)
            {
                content.Children.Add(Indented($"Link to {MailFormat.LinkHost(url)} (not opened)"));
                content.Children.Add(SecondaryIndented(url));
            }

            body.Children.Add(Card(content, $"Mail from {message.From}"));
        }

        return body;
    }

    internal static StackPanel Approval(MailApprovalPaneViewModel pane)
    {
        var card = pane.Card;
        var body = new StackPanel { Spacing = 8 };
        AutomationProperties.SetName(body, "Mail approval card");
        body.Children.Add(new TextBlock { Text = "Approval needed", FontWeight = FontWeight.SemiBold, FontSize = 16 });
        body.Children.Add(new TextBlock { Text = $"Approval ID: {card.ApprovalId}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"From (set by server): {card.From}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"To: {string.Join(", ", card.Recipients)}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"Subject: {card.Subject}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "Body", FontWeight = FontWeight.SemiBold });
        body.Children.Add(Card(new TextBlock { Text = card.Body, TextWrapping = TextWrapping.Wrap }, "Message body"));
        body.Children.Add(new TextBlock { Text = $"Footer: {card.FooterPreview}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"Why approval is needed: {card.Reason}", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Note("Display only. Decide this in Approvals; this card cannot send or release mail."));
        return body;
    }

    internal static StackPanel Dns(MailDnsPaneViewModel pane)
    {
        var body = new StackPanel { Spacing = 10 };
        AutomationProperties.SetName(body, "Mail DNS checks");
        if (!string.IsNullOrWhiteSpace(pane.Domain))
        {
            body.Children.Add(new TextBlock { Text = $"Domain: {pane.Domain}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        }

        if (pane.Rows.Count == 0)
        {
            body.Children.Add(Note("No DNS checks yet."));
        }

        foreach (var row in pane.Rows)
        {
            body.Children.Add(DnsRow(row));
        }

        return body;
    }

    /// <summary>One DNS row: the check, a status chip carrying the status as text, the detail and the fix hint.</summary>
    private static Border DnsRow(MailDnsRow row)
    {
        var kind = MailFormat.DnsKind(row.Kind);
        var status = MailFormat.DnsStatus(row.Status);

        var chip = new Border
        {
            Padding = new Thickness(6, 2),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = status, FontWeight = FontWeight.SemiBold, FontSize = 12 },
        };
        SetThemeBrush(chip, Border.BorderBrushProperty, _borderToken);
        AutomationProperties.SetName(chip, $"{kind} status {status}");

        var content = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = kind, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                        chip,
                    },
                },
                new TextBlock { Text = row.Detail, TextWrapping = TextWrapping.Wrap },
            },
        };

        if (!string.IsNullOrWhiteSpace(row.Fix))
        {
            content.Children.Add(new TextBlock { Text = $"Fix: {row.Fix}", TextWrapping = TextWrapping.Wrap });
        }

        return Card(content, $"{kind} check: {status}");
    }

    /// <summary>A bordered block that groups one row or message. The name is what a screen reader announces.</summary>
    private static Border Card(Control content, string name)
    {
        var border = new Border
        {
            Padding = new Thickness(8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = content,
        };
        SetThemeBrush(border, Border.BorderBrushProperty, _borderToken);
        AutomationProperties.SetName(border, name);
        return border;
    }

    private static TextBlock Note(string text)
    {
        var note = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        SetThemeBrush(note, TextBlock.ForegroundProperty, _secondaryTextToken);
        return note;
    }

    private static TextBlock Indented(string text) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) };

    private static TextBlock SecondaryIndented(string text)
    {
        var line = Indented(text);
        SetThemeBrush(line, TextBlock.ForegroundProperty, _secondaryTextToken);
        return line;
    }

    /// <summary>
    /// Sets a brush property from a theme token. A token the active theme does not define falls back to transparent, so the
    /// view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
