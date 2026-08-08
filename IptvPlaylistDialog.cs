using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamHub;

public sealed class IptvPlaylistDialog : Window
{
    private readonly TextBox _name = new() { Text = "My IPTV" };
    private readonly TextBox _source = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 82, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox _epg = new();
    public IptvPlaylist Playlist { get; private set; } = new();

    public IptvPlaylistDialog()
    {
        Title = "Add IPTV playlist"; Width = 560; Height = 510; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(20, 24, 32)); ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = "Add IPTV playlist", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        AddField(panel, "Playlist name", _name); AddField(panel, "M3U/M3U8 URL, file path, or raw #EXTM3U text", _source);
        var browse = new Button { Content = "Browse for playlist", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, -5, 0, 14) };
        browse.Click += (_, _) => { var p = new OpenFileDialog { Filter = "IPTV playlists (*.m3u;*.m3u8)|*.m3u;*.m3u8|All files|*.*" }; if (p.ShowDialog() == true) _source.Text = p.FileName; }; panel.Children.Add(browse);
        AddField(panel, "XMLTV guide URL (optional)", _epg);
        var save = new Button { Content = "Import playlist", Padding = new Thickness(13), Background = new SolidColorBrush(Color.FromRgb(92, 225, 192)), Foreground = new SolidColorBrush(Color.FromRgb(7, 18, 15)) };
        save.Click += (_, _) => Save(); panel.Children.Add(save); Content = panel;
    }

    private static void AddField(Panel panel, string label, TextBox input) { panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 6) }); input.Padding = new Thickness(9); input.Margin = new Thickness(0, 0, 0, 13); panel.Children.Add(input); }
    private void Save() { if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_source.Text)) { MessageBox.Show("Enter a playlist name and source."); return; } Playlist = new IptvPlaylist { Name = _name.Text.Trim(), Source = _source.Text.Trim(), EpgSource = _epg.Text.Trim() }; DialogResult = true; }
}
