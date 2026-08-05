using System.Windows;
using System.Windows.Markup;

namespace OfflineMusicLibrary;

public partial class TextPromptWindow : Window, IComponentConnector
{
	public string Value => ValueTextBox.Text.Trim();

	public TextPromptWindow(string title, string prompt, string initialValue = "")
	{
		InitializeComponent();
		base.Title = title;
		PromptText.Text = prompt;
		ValueTextBox.Text = initialValue;
		ValueTextBox.SelectAll();
		base.Loaded += delegate
		{
			ValueTextBox.Focus();
		};
	}

	private void ConfirmButton_Click(object sender, RoutedEventArgs e)
	{
		if (!string.IsNullOrWhiteSpace(ValueTextBox.Text))
		{
			base.DialogResult = true;
		}
	}
}
