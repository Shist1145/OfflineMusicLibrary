using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OfflineMusicLibrary;

public sealed partial class MessageWindow : Window
{
    public MessageWindow() : this("提示", "")
    {
    }

    public MessageWindow(string title, string message)
    {
        AvaloniaXamlLoader.Load(this);
        Title = title;
        this.FindControl<TextBlock>("MessageText")!.Text = message;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e) => Close();
}
