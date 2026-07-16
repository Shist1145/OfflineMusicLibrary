using System.Windows;

namespace OfflineMusicLibrary;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueTextBox.Text = initialValue;
        ValueTextBox.SelectAll();
        Loaded += (_, _) => ValueTextBox.Focus();
    }

    public string Value => ValueTextBox.Text.Trim();

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueTextBox.Text))
            return;
        DialogResult = true;
    }
}
