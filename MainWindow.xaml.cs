using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Net.NetworkInformation;

namespace StreamHub;

public partial class MainWindow : Window
{
    private readonly AppState _state;
    private Process? _vpnProcess;
    private VpnProfile? _connectedVpn;
    private DashboardServer? _dashboardServer;
    private readonly List<IptvChannel> _channels = [];
    private LibVLC? _libVlc;
    private VlcMediaPlayer? _iptvPlayer;
    private bool _isFullscreen;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly DispatcherTimer _telemetryTimer;
    private long _lastStreamBytes;
    private DateTimeOffset _lastStreamSample = DateTimeOffset.Now;
    private int _telemetryTicks;
    private int _bufferingEvents;
    private float _lastBufferPercent;
    private long _lastVpnReceived;
    private long _lastVpnSent;
    private DateTimeOffset _lastVpnSample = DateTimeOffset.Now;
    private int _vpnTelemetryTicks;
    private long? _vpnLatencyMs;
    private bool _vpnPingRunning;

    public MainWindow()
    {
        InitializeComponent();
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telemetryTimer.Tick += (_, _) => { UpdateStreamTelemetry(); UpdateVpnTelemetry(); };
        _telemetryTimer.Start();
        _state = AppState.Load();
        DashboardNameBox.Text = _state.DashboardName;
        PortBox.Text = _state.Port.ToString();
        RemoteAccessBox.IsChecked = _state.RemoteAccessEnabled;
        DebugModeBox.IsChecked = _state.DebugMode;
        AdminUsernameBox.Text = _state.AdminUsername;
        Title = _state.DashboardName;
        RefreshServices();
        VpnList.ItemsSource = _state.VpnProfiles;
        SetDebugVisibility();
        LogDebug("StreamHub initialized.");
        LogEnvironmentSnapshot();
        Browser.NavigationStarting += (_, args) => LogDebug($"WebView navigation started: {SafeHost(args.Uri)}");
        Browser.NavigationCompleted += (_, args) => LogDebug($"WebView navigation completed: success={args.IsSuccess}, status={args.HttpStatusCode}, error={args.WebErrorStatus}");
        Application.Current.DispatcherUnhandledException += (_, args) => { LogDebug($"UI exception: {args.Exception.GetType().Name}: {args.Exception.Message}"); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogDebug($"Fatal exception: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) => LogDebug($"Background task exception: {args.Exception.GetBaseException().Message}");
        Loaded += async (_, _) => { InitializeIptvPlayer(); await ApplyServerSettingsAsync(); await LoadPlaylistsAsync(); };
        Closed += async (_, _) => { _telemetryTimer.Stop(); _iptvPlayer?.Stop(); _iptvPlayer?.Dispose(); _libVlc?.Dispose(); if (_dashboardServer is not null) await _dashboardServer.DisposeAsync(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape && _isFullscreen) ExitFullscreen(); };
    }

    private void ShowOnly(UIElement page)
    {
        foreach (var item in new[] { HomePage, ServicesPage, BrowserPage, VpnPage, LiveTvPage, SettingsPage }) item.Visibility = Visibility.Collapsed;
        page.Visibility = Visibility.Visible;
    }

    private void ShowHome_Click(object sender, RoutedEventArgs e) { ShowOnly(HomePage); RefreshServices(); }
    private void ShowServices_Click(object sender, RoutedEventArgs e) { ShowOnly(ServicesPage); RefreshServices(); }
    private void ShowLiveTv_Click(object sender, RoutedEventArgs e) => ShowOnly(LiveTvPage);
    private void ShowVpn_Click(object sender, RoutedEventArgs e) => ShowOnly(VpnPage);
    private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowOnly(SettingsPage);

    private void RefreshServices(string filter = "")
    {
        var services = _state.Services.Where(s => string.IsNullOrWhiteSpace(filter) || s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        ServicesList.ItemsSource = null;
        ServicesList.ItemsSource = _state.Services;
        ServiceTiles.Children.Clear();
        foreach (var service in services)
        {
            var button = new Button { Width = 252, Height = 154, Margin = new Thickness(0, 0, 18, 18), Padding = new Thickness(1), Tag = service, Background = new SolidColorBrush(Color.FromRgb(42, 48, 59)), BorderBrush = new SolidColorBrush(Color.FromRgb(55, 63, 76)), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var card = new Grid { Margin = new Thickness(0) };
            card.Background = new LinearGradientBrush(((SolidColorBrush)BrushFrom(service.Color)).Color, Color.FromRgb(19, 23, 31), new Point(0, 0), new Point(1, 1));
            var content = new Grid { Margin = new Thickness(18) };
            var badge = new Border { Width = 43, Height = 43, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(55, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            badge.Child = new TextBlock { Text = service.Name[..1].ToUpperInvariant(), FontSize = 21, FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(badge);
            content.Children.Add(new TextBlock { Text = "↗", FontSize = 18, Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top });
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
            labels.Children.Add(new TextBlock { Text = service.Name, FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
            labels.Children.Add(new TextBlock { Text = service.Category.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), Margin = new Thickness(0, 5, 0, 0) });
            content.Children.Add(labels); card.Children.Add(content); button.Content = card;
            button.Click += OpenService_Click;
            ServiceTiles.Children.Add(button);
        }
    }

    private static Brush BrushFrom(string color)
    {
        try { return (Brush)new BrushConverter().ConvertFromString(color)!; }
        catch { return new SolidColorBrush(Color.FromRgb(54, 197, 163)); }
    }

    private async void OpenService_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServiceItem service) return;
        if (service.OpenExternally) { LaunchExternal(service.Url); return; }
        ShowOnly(BrowserPage);
        AddressBox.Text = service.Url;
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; Browser.CoreWebView2.Navigate(args.Uri); };
            Browser.CoreWebView2.Navigate(service.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WebView2 could not start: {ex.Message}\n\nThe service will open in your browser.", "StreamHub");
            LaunchExternal(service.Url);
        }
    }

    private void AddService_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ServiceDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _state.Services.Add(dialog.Service);
        _state.Save();
        RefreshServices();
    }

    private void DeleteService_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ServiceItem service) return;
        if (MessageBox.Show($"Remove {service.Name}?", "StreamHub", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _state.Services.RemoveAll(x => x.Id == service.Id);
        _state.Save();
        RefreshServices();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshServices(SearchBox.Text);
    private void BrowserBack_Click(object sender, RoutedEventArgs e) { if (Browser.CanGoBack) Browser.GoBack(); }
    private void BrowserForward_Click(object sender, RoutedEventArgs e) { if (Browser.CanGoForward) Browser.GoForward(); }
    private void BrowserRefresh_Click(object sender, RoutedEventArgs e) => Browser.Reload();
    private void BrowserGo_Click(object sender, RoutedEventArgs e) { if (Uri.TryCreate(AddressBox.Text, UriKind.Absolute, out var uri)) Browser.Source = uri; }
    private void OpenExternal_Click(object sender, RoutedEventArgs e) => LaunchExternal(AddressBox.Text);
    private static void LaunchExternal(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1024 or > 65535) { MessageBox.Show("Enter a port between 1024 and 65535."); return; }
        _state.DashboardName = string.IsNullOrWhiteSpace(DashboardNameBox.Text) ? "StreamHub" : DashboardNameBox.Text.Trim();
        _state.Port = port;
        _state.AdminUsername = string.IsNullOrWhiteSpace(AdminUsernameBox.Text) ? "admin" : AdminUsernameBox.Text.Trim();
        _state.DebugMode = DebugModeBox.IsChecked == true;
        if (!string.IsNullOrEmpty(AdminPasswordBox.Password))
        {
            if (AdminPasswordBox.Password.Length < 12) { MessageBox.Show("Use a password with at least 12 characters."); return; }
            _state.SetPassword(AdminPasswordBox.Password);
            AdminPasswordBox.Clear();
        }
        _state.RemoteAccessEnabled = RemoteAccessBox.IsChecked == true;
        if (_state.RemoteAccessEnabled && string.IsNullOrWhiteSpace(_state.PasswordHash)) { MessageBox.Show("Set an administrator password before enabling remote access."); return; }
        _state.Save();
        SetDebugVisibility();
        LogDebug("Settings saved.");
        Title = _state.DashboardName;
        await ApplyServerSettingsAsync();
        MessageBox.Show("Settings saved and applied.", "StreamHub");
    }

    private async Task ApplyServerSettingsAsync()
    {
        try
        {
            if (_dashboardServer is not null) { await _dashboardServer.DisposeAsync(); _dashboardServer = null; }
            if (!_state.RemoteAccessEnabled) { ServerStatusText.Text = "Server stopped"; ServerStatusText.Foreground = Brushes.Orange; return; }
            _dashboardServer = new DashboardServer(_state);
            await _dashboardServer.StartAsync();
            ServerStatusText.Text = $"● Running at http://127.0.0.1:{_state.Port} — ready for Tailscale";
            ServerStatusText.Foreground = new SolidColorBrush(Color.FromRgb(92, 225, 192));
            LogDebug($"Web dashboard started on 127.0.0.1:{_state.Port}.");
        }
        catch (Exception ex)
        {
            ServerStatusText.Text = "Server failed to start"; ServerStatusText.Foreground = Brushes.OrangeRed;
            LogDebug($"Web dashboard failed: {ex.GetType().Name}: {ex.Message}");
            MessageBox.Show(ex.Message, "StreamHub server");
        }
    }

    private void ImportVpn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "VPN profiles (*.ovpn;*.conf)|*.ovpn;*.conf|OpenVPN (*.ovpn)|*.ovpn|WireGuard (*.conf)|*.conf", Multiselect = false };
        if (picker.ShowDialog() != true) return;
        var vpnFolder = Path.Combine(AppState.DataDirectory, "vpn");
        Directory.CreateDirectory(vpnFolder);
        var destination = Path.Combine(vpnFolder, Path.GetFileName(picker.FileName));
        File.Copy(picker.FileName, destination, true);
        var protocol = Path.GetExtension(destination).Equals(".conf", StringComparison.OrdinalIgnoreCase) ? "WireGuard" : "OpenVPN";
        _state.VpnProfiles.Add(new VpnProfile { Name = Path.GetFileNameWithoutExtension(destination), FilePath = destination, Protocol = protocol });
        _state.Save();
        LogDebug($"Imported {protocol} profile '{Path.GetFileNameWithoutExtension(destination)}'. Sensitive configuration omitted.");
        VpnList.ItemsSource = null; VpnList.ItemsSource = _state.VpnProfiles;
    }

    private void ConnectVpn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not VpnProfile profile) return;
        try
        {
            DisconnectCurrentVpn();
            if (string.Equals(profile.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(profile.FilePath).Equals(".conf", StringComparison.OrdinalIgnoreCase))
            {
                var wireGuard = @"C:\Program Files\WireGuard\wireguard.exe";
                if (!File.Exists(wireGuard)) { MessageBox.Show("WireGuard for Windows was not found. Install it from wireguard.com, then try again.", "StreamHub"); return; }
                Process.Start(new ProcessStartInfo(wireGuard, $"/installtunnelservice \"{profile.FilePath}\"") { UseShellExecute = true, Verb = "runas" });
            }
            else
            {
                var openVpn = @"C:\Program Files\OpenVPN\bin\openvpn.exe";
                if (!File.Exists(openVpn)) { MessageBox.Show("OpenVPN Community was not found. Install it, then try again.", "StreamHub"); return; }
                _vpnProcess = Process.Start(new ProcessStartInfo(openVpn, $"--config \"{profile.FilePath}\"") { UseShellExecute = true, Verb = "runas" });
            }
            _connectedVpn = profile;
            VpnDot.Fill = Brushes.LimeGreen; VpnStatusText.Text = $"Connecting: {profile.Name}";
            LogDebug($"VPN connect requested: profile='{profile.Name}', protocol={profile.Protocol}.");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "VPN connection failed"); }
    }

    private void DisconnectVpn_Click(object sender, RoutedEventArgs e)
    {
        DisconnectCurrentVpn(); VpnDot.Fill = new SolidColorBrush(Color.FromRgb(112, 119, 130)); VpnStatusText.Text = "Disconnected";
    }

    private void DisconnectCurrentVpn()
    {
        try
        {
            if (_connectedVpn is not null && (string.Equals(_connectedVpn.Protocol, "WireGuard", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(_connectedVpn.FilePath).Equals(".conf", StringComparison.OrdinalIgnoreCase)))
            {
                var wireGuard = @"C:\Program Files\WireGuard\wireguard.exe";
                if (File.Exists(wireGuard)) Process.Start(new ProcessStartInfo(wireGuard, $"/uninstalltunnelservice \"{Path.GetFileNameWithoutExtension(_connectedVpn.FilePath)}\"") { UseShellExecute = true, Verb = "runas" });
            }
            if (_vpnProcess is { HasExited: false }) _vpnProcess.Kill(true);
            if (_connectedVpn is not null) LogDebug($"VPN disconnect requested: profile='{_connectedVpn.Name}'.");
        }
        catch (Exception ex) { LogDebug($"VPN disconnect error: {ex.Message}"); }
        _vpnProcess = null; _connectedVpn = null; _lastVpnReceived = 0; _lastVpnSent = 0; VpnStatsText.Text = "VPN telemetry: no active tunnel";
    }

    private void InitializeIptvPlayer()
    {
        try
        {
            Core.Initialize();
            _libVlc = new LibVLC("--network-caching=1800", "--no-video-title-show");
            _iptvPlayer = new VlcMediaPlayer(_libVlc);
            IptvVideoView.MediaPlayer = _iptvPlayer;
            _iptvPlayer.Playing += (_, _) => Dispatcher.Invoke(() => LogDebug("VLC state: playing"));
            _iptvPlayer.Paused += (_, _) => Dispatcher.Invoke(() => LogDebug("VLC state: paused"));
            _iptvPlayer.Stopped += (_, _) => Dispatcher.Invoke(() => LogDebug("VLC state: stopped"));
            _iptvPlayer.EncounteredError += (_, _) => Dispatcher.Invoke(() => LogDebug("VLC playback error encountered."));
            _iptvPlayer.Buffering += (_, args) => Dispatcher.Invoke(() => { _lastBufferPercent = args.Cache; if (args.Cache < 100) _bufferingEvents++; if (args.Cache is 0 or >= 100) LogDebug($"VLC buffering: {args.Cache:0}%"); });
            LogDebug("LibVLC IPTV engine ready.");
        }
        catch (Exception ex) { LogDebug($"LibVLC initialization failed: {ex.GetType().Name}: {ex.Message}"); MessageBox.Show($"The IPTV playback engine could not start: {ex.Message}", "StreamHub IPTV"); }
    }

    private async Task LoadPlaylistsAsync()
    {
        _channels.Clear();
        foreach (var playlist in _state.IptvPlaylists)
        {
            try
            {
                var parsed = M3uParser.Parse(await M3uParser.ReadSourceAsync(playlist.Source), playlist.Id);
                _channels.AddRange(parsed); LogDebug($"Playlist '{playlist.Name}' loaded {parsed.Count} channels.");
            }
            catch (Exception ex) { LogDebug($"Playlist '{playlist.Name}' failed: {ex.GetType().Name}: {ex.Message}"); }
        }
        RefreshChannels();
        LogDebug($"Loaded {_channels.Count} IPTV channels from {_state.IptvPlaylists.Count} playlist(s).");
    }

    private void RefreshChannels()
    {
        var selectedGroup = ChannelGroupBox.SelectedItem?.ToString() ?? "All channels";
        var groups = new[] { "All channels" }.Concat(_channels.Select(x => x.Group).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order()).ToList();
        if (ChannelGroupBox.Items.Count != groups.Count || !groups.SequenceEqual(ChannelGroupBox.Items.Cast<string>()))
        {
            ChannelGroupBox.ItemsSource = groups;
            ChannelGroupBox.SelectedItem = groups.Contains(selectedGroup) ? selectedGroup : "All channels";
            selectedGroup = ChannelGroupBox.SelectedItem?.ToString() ?? "All channels";
        }
        var query = ChannelSearchBox.Text?.Trim() ?? "";
        ChannelList.ItemsSource = _channels.Where(x => (selectedGroup == "All channels" || x.Group == selectedGroup) && (query.Length == 0 || x.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        ChannelCountText.Text = _channels.Count == 0 ? "Add an M3U playlist to begin." : $"{_channels.Count:N0} channels from {_state.IptvPlaylists.Count} playlist(s)";
    }

    private async void AddPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new IptvPlaylistDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var content = await M3uParser.ReadSourceAsync(dialog.Playlist.Source);
            var channels = M3uParser.Parse(content, dialog.Playlist.Id);
            if (channels.Count == 0) { MessageBox.Show("No playable channels were found in that playlist.", "StreamHub IPTV"); return; }
            _state.IptvPlaylists.Add(dialog.Playlist); _channels.AddRange(channels); _state.Save(); RefreshChannels();
            LogDebug($"Imported playlist '{dialog.Playlist.Name}' with {channels.Count} channels.");
            MessageBox.Show($"Imported {channels.Count:N0} channels.", "StreamHub IPTV");
        }
        catch (Exception ex) { MessageBox.Show($"The playlist could not be imported: {ex.Message}", "StreamHub IPTV"); }
    }

    private async void ManagePlaylists_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IptvPlaylists.Count == 0) { MessageBox.Show("No IPTV playlists have been added."); return; }
        var names = string.Join("\n", _state.IptvPlaylists.Select((x, i) => $"{i + 1}. {x.Name}"));
        var result = MessageBox.Show($"Configured playlists:\n\n{names}\n\nReload all playlists now?", "IPTV playlists", MessageBoxButton.YesNo);
        if (result == MessageBoxResult.Yes) await LoadPlaylistsAsync();
    }

    private void ChannelSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshChannels();
    private void ChannelGroup_Changed(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RefreshChannels(); }
    private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelList.SelectedItem is not IptvChannel channel) return;
        NowPlayingText.Text = channel.Name; NowPlayingGroupText.Text = channel.Group;
    }
    private void ChannelList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => PlaySelectedChannel();
    private void PlayChannel_Click(object sender, RoutedEventArgs e) => PlaySelectedChannel();

    private void PlaySelectedChannel()
    {
        if (ChannelList.SelectedItem is not IptvChannel channel || _libVlc is null || _iptvPlayer is null) return;
        using var media = new VlcMedia(_libVlc, channel.Url, FromType.FromLocation);
        if (!string.IsNullOrWhiteSpace(channel.UserAgent)) media.AddOption($":http-user-agent={channel.UserAgent}");
        _lastStreamBytes = 0; _lastStreamSample = DateTimeOffset.Now; _telemetryTicks = 0; _bufferingEvents = 0; _lastBufferPercent = 100;
        _iptvPlayer.Play(media); PlayerPlaceholder.Visibility = Visibility.Collapsed;
        NowPlayingText.Text = channel.Name; NowPlayingGroupText.Text = $"LIVE · {channel.Group}";
        LogDebug($"Playing IPTV channel: {channel.Name}");
    }

    private void StopChannel_Click(object sender, RoutedEventArgs e) { _iptvPlayer?.Stop(); StreamStatsText.Text = "Stream telemetry: idle"; PlayerPlaceholder.Visibility = Visibility.Visible; NowPlayingText.Text = "Nothing playing"; NowPlayingGroupText.Text = "Live TV"; }
    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        _isFullscreen = !_isFullscreen;
        WindowStyle = _isFullscreen ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = _isFullscreen ? WindowState.Maximized : WindowState.Normal;
    }

    private void ExitFullscreen()
    {
        _isFullscreen = false; WindowStyle = WindowStyle.SingleBorderWindow; WindowState = WindowState.Normal;
    }

    private void CloseApp_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleDebug_Click(object sender, RoutedEventArgs e)
    {
        _state.DebugMode = !_state.DebugMode;
        DebugModeBox.IsChecked = _state.DebugMode;
        _state.Save(); SetDebugVisibility();
        LogDebug(_state.DebugMode ? "Debug mode enabled." : "Debug mode disabled.");
    }

    private void SetDebugVisibility()
    {
        DebugPanel.Visibility = _state.DebugMode ? Visibility.Visible : Visibility.Collapsed;
        DebugToggleButton.Content = _state.DebugMode ? "{ } Debug on" : "{ } Debug off";
        DebugToggleButton.Opacity = _state.DebugMode ? 1 : 0.7;
#if DEBUG
        BuildStateText.Text = "Debug build · Runtime ready";
#else
        BuildStateText.Text = "Release build · Runtime ready";
#endif
    }

    private void LogDebug(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DebugLogBox.AppendText((DebugLogBox.Text.Length == 0 ? "" : Environment.NewLine) + entry);
        if (DebugLogBox.LineCount > 750)
        {
            var lines = DebugLogBox.Text.Split(Environment.NewLine).TakeLast(600);
            DebugLogBox.Text = string.Join(Environment.NewLine, lines);
        }
        DebugLogBox.ScrollToEnd();
    }

    private void ClearDebug_Click(object sender, RoutedEventArgs e) => DebugLogBox.Clear();

    private void DebugSnapshot_Click(object sender, RoutedEventArgs e) => LogEnvironmentSnapshot();

    private void LogEnvironmentSnapshot()
    {
        using var process = Process.GetCurrentProcess();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        LogDebug($"Snapshot: app={version}, runtime={RuntimeInformation.FrameworkDescription}, OS={RuntimeInformation.OSDescription}, arch={RuntimeInformation.ProcessArchitecture}");
        LogDebug($"Process: pid={Environment.ProcessId}, memory={process.WorkingSet64 / 1024d / 1024d:0.0} MB, CPUs={Environment.ProcessorCount}, uptime={(DateTimeOffset.Now - _startedAt):hh\\:mm\\:ss}");
        LogDebug($"State: services={_state.Services.Count}, playlists={_state.IptvPlaylists.Count}, channels={_channels.Count}, VPN profiles={_state.VpnProfiles.Count}, web={(_state.RemoteAccessEnabled ? $"127.0.0.1:{_state.Port}" : "off")}");
        LogDebug($"Playback: VLC={_iptvPlayer?.State.ToString() ?? "unavailable"}, WebView2={(Browser.CoreWebView2 is null ? "not initialized" : Browser.CoreWebView2.Environment.BrowserVersionString)}");
    }

    private void ExportDebug_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = $"streamhub-debug-{DateTime.Now:yyyyMMdd-HHmmss}.txt" };
        if (picker.ShowDialog() != true) return;
        File.WriteAllText(picker.FileName, DebugLogBox.Text);
        LogDebug("Debug log exported.");
    }

    private static string SafeHost(string? value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)) return uri.Host;
        return "unknown destination";
    }

    private void UpdateStreamTelemetry()
    {
        if (!_state.DebugMode || _iptvPlayer is null || _iptvPlayer.State is not (VLCState.Playing or VLCState.Buffering)) return;
        try
        {
            var media = _iptvPlayer.Media;
            if (media is null) return;
            var stats = media.Statistics;
            var now = DateTimeOffset.Now;
            var seconds = Math.Max((now - _lastStreamSample).TotalSeconds, 0.1);
            var bytesDelta = Math.Max(0, stats.ReadBytes - _lastStreamBytes);
            var measuredMbps = bytesDelta * 8d / seconds / 1_000_000d;
            var reportedMbps = stats.InputBitrate * 8d / 1_000_000d;
            var receivedMb = stats.ReadBytes / 1024d / 1024d;
            var dropRate = stats.DisplayedPictures + stats.LostPictures == 0 ? 0 : stats.LostPictures * 100d / (stats.DisplayedPictures + stats.LostPictures);
            var health = stats.DemuxCorrupted > 0 || stats.DemuxDiscontinuity > 2 ? "source unstable" : dropRate >= 3 ? "decoder/render constrained" : _iptvPlayer.State == VLCState.Buffering || _lastBufferPercent < 95 ? "buffering" : "healthy";
            StreamStatsText.Text = $"{health.ToUpperInvariant()} · ↓ {measuredMbps:0.00} Mbps · stream {reportedMbps:0.00} Mbps · {receivedMb:0.0} MB · dropped {dropRate:0.00}%";
            _lastStreamBytes = stats.ReadBytes; _lastStreamSample = now;
            if (++_telemetryTicks % 5 == 0)
                LogDebug($"Stream health={health}: transfer={measuredMbps:0.00} Mbps, media={reportedMbps:0.00} Mbps, received={receivedMb:0.0} MB, displayed={stats.DisplayedPictures}, dropped={stats.LostPictures} ({dropRate:0.00}%), corrupted={stats.DemuxCorrupted}, discontinuities={stats.DemuxDiscontinuity}, buffering-events={_bufferingEvents}, time={TimeSpan.FromMilliseconds(Math.Max(0, _iptvPlayer.Time)):hh\\:mm\\:ss}");
        }
        catch (Exception ex) { LogDebug($"Stream telemetry unavailable: {ex.Message}"); }
    }

    private void UpdateVpnTelemetry()
    {
        if (!_state.DebugMode) return;
        try
        {
            var adapters = NetworkInterface.GetAllNetworkInterfaces();
            var adapter = adapters.FirstOrDefault(x => x.OperationalStatus == OperationalStatus.Up &&
                ((_connectedVpn is not null && x.Name.Contains(_connectedVpn.Name, StringComparison.OrdinalIgnoreCase)) ||
                 x.Name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) || x.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)));
            if (adapter is null) { VpnStatsText.Text = "VPN telemetry: no active tunnel adapter"; return; }
            var counters = adapter.GetIPv4Statistics();
            var now = DateTimeOffset.Now;
            var seconds = Math.Max((now - _lastVpnSample).TotalSeconds, 0.1);
            var downMbps = _lastVpnReceived == 0 ? 0 : Math.Max(0, counters.BytesReceived - _lastVpnReceived) * 8d / seconds / 1_000_000d;
            var upMbps = _lastVpnSent == 0 ? 0 : Math.Max(0, counters.BytesSent - _lastVpnSent) * 8d / seconds / 1_000_000d;
            var receivedMb = counters.BytesReceived / 1024d / 1024d;
            var sentMb = counters.BytesSent / 1024d / 1024d;
            var latency = _vpnLatencyMs.HasValue ? $"{_vpnLatencyMs} ms" : "checking";
            var health = _vpnLatencyMs is > 180 ? "HIGH LATENCY" : _vpnLatencyMs is > 90 ? "MODERATE" : "ACTIVE";
            VpnStatsText.Text = $"{health} · ↓ {downMbps:0.00} Mbps · ↑ {upMbps:0.00} Mbps · ping {latency} · total {receivedMb:0.0}/{sentMb:0.0} MB";
            _lastVpnReceived = counters.BytesReceived; _lastVpnSent = counters.BytesSent; _lastVpnSample = now;
            if (++_vpnTelemetryTicks % 5 == 0)
            {
                LogDebug($"VPN telemetry: adapter='{adapter.Name}', down={downMbps:0.00} Mbps, up={upMbps:0.00} Mbps, latency={latency}, received={receivedMb:0.0} MB, sent={sentMb:0.0} MB, status={health.ToLowerInvariant()}");
                _ = MeasureVpnLatencyAsync();
            }
        }
        catch (Exception ex) { LogDebug($"VPN telemetry unavailable: {ex.Message}"); }
    }

    private async Task MeasureVpnLatencyAsync()
    {
        if (_vpnPingRunning) return;
        _vpnPingRunning = true;
        try
        {
            using var ping = new Ping();
            var response = await ping.SendPingAsync("1.1.1.1", 2500);
            _vpnLatencyMs = response.Status == IPStatus.Success ? response.RoundtripTime : null;
            if (response.Status != IPStatus.Success) LogDebug($"VPN latency probe: {response.Status}");
        }
        catch (Exception ex) { _vpnLatencyMs = null; LogDebug($"VPN latency probe failed: {ex.Message}"); }
        finally { _vpnPingRunning = false; }
    }
}
