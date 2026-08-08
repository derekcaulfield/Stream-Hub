using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamHub;

public sealed class ServiceDialog : Window
{
    private readonly TextBox _name = new();
    private readonly TextBox _url = new() { Text = "https://" };
    private readonly TextBox _category = new() { Text = "Streaming" };
    private readonly TextBox _color = new() { Text = "#36C5A3" };
    private readonly CheckBox _external = new() { Content = "Always open in system browser", Foreground = Brushes.White };
    public ServiceItem Service { get; private set; } = new();

    public ServiceDialog()
    {
        Title = "Add streaming service"; Width = 470; Height = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = new SolidColorBrush(Color.FromRgb(25, 28, 34)); ResizeMode = ResizeMode.NoResize;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Add service", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        AddField(panel, "Name", _name); AddField(panel, "Website URL", _url); AddField(panel, "Category", _category); AddField(panel, "Tile color (hex)", _color);
        panel.Children.Add(_external);
        var save = new Button { Content = "Add service", Margin = new Thickness(0, 22, 0, 0), Padding = new Thickness(12), Background = new SolidColorBrush(Color.FromRgb(54, 197, 163)) };
        save.Click += (_, _) => Save(); panel.Children.Add(save); Content = panel;
    }

    private static void AddField(Panel panel, string label, TextBox input)
    {
        panel.Children.Add(new TextBlock { Text = label, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 5) });
        input.Padding = new Thickness(8); input.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(input);
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || !Uri.TryCreate(_url.Text, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) { MessageBox.Show("Enter a name and a valid http/https URL."); return; }
        Service = new ServiceItem { Name = _name.Text.Trim(), Url = uri.ToString(), Category = _category.Text.Trim(), Color = _color.Text.Trim(), OpenExternally = _external.IsChecked == true };
        DialogResult = true;
    }
}
