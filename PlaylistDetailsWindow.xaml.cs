using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OfflineMusicLibrary;

public partial class PlaylistDetailsWindow : Window
{
    public PlaylistDetailsWindow(PlaylistModel playlist, BitmapSource? currentCover)
    {
        InitializeComponent();
        NameTextBox.Text = playlist.Name;
        DescriptionTextBox.Text = playlist.Description;
        TagsTextBox.Text = string.Join("、", playlist.Tags);
        MetadataText.Text = $"{playlist.SourceText} · {playlist.TrackIds.Count:N0} 首歌曲";
        CoverPreviewImage.Source = currentCover;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            NameTextBox.SelectAll();
        };
    }

    public string PlaylistName => NameTextBox.Text.Trim();
    public string Description => DescriptionTextBox.Text.Trim();
    public IReadOnlyList<string> Tags => TagsTextBox.Text
        .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToList();
    public string? SelectedCoverFile { get; private set; }
    public bool RemoveCustomCover { get; private set; }

    private void ChooseCoverButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择歌单封面",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.webp;*.bmp|所有文件|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var preview = CoverService.LoadImageFile(dialog.FileName, 520);
        if (preview is null)
        {
            ValidationText.Text = "无法读取这张图片，请换一张。";
            return;
        }
        SelectedCoverFile = dialog.FileName;
        RemoveCustomCover = false;
        CoverPreviewImage.Source = preview;
        ValidationText.Text = "";
    }

    private void UseAutomaticCoverButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedCoverFile = null;
        RemoveCustomCover = true;
        CoverPreviewImage.Source = null;
        ValidationText.Text = "保存后将从歌单歌曲中自动选择封面。";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistName.Length == 0)
        {
            ValidationText.Text = "歌单名称不能为空。";
            NameTextBox.Focus();
            return;
        }
        DialogResult = true;
    }
}
