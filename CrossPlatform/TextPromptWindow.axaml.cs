using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace OfflineMusicLibrary;

public sealed partial class TextPromptWindow : Window
{
    public TextPromptWindow() : this("输入", "请输入内容")
    {
    }

    public TextPromptWindow(string title, string prompt, string hint = "")
    {
        InitializeComponent();
        Title = title;
        this.FindControl<TextBlock>("PromptText")!.Text = prompt;
        this.FindControl<TextBlock>("HintText")!.Text = hint;
        Opened += (_, _) => Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("ValueBox")!.Focus());
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        var value = this.FindControl<TextBox>("ValueBox")!.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(value))
            Close(value);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e) => Close(null);
}
