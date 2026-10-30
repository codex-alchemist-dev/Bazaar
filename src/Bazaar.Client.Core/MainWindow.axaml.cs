using Avalonia.Controls;
using Bazaar.Client.Core.Chat;

namespace Bazaar.Client.Core;

public sealed partial class MainWindow : Window
{
    public MainWindow() : this(null)
    {
    }

    /// <param name="chat">The group chat to show. Null shows the plain start window.</param>
    public MainWindow(ChatViewModel? chat)
    {
        InitializeComponent();
        if (chat is not null)
        {
            Content = new ChatView { DataContext = chat };
        }
    }
}
