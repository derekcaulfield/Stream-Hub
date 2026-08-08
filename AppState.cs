using System.Text.Json;
using System.IO;

namespace StreamHub;

public sealed class ServiceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New service";
    public string Url { get; set; } = "https://example.com";
    public string Category { get; set; } = "Streaming";
    public string Color { get; set; } = "#36C5A3";
    public bool OpenExternally { get; set; }
}

public sealed class VpnProfile
{
    public string Name { get; set; } = "VPN";
    public string FilePath { get; set; } = "";
    public string Protocol { get; set; } = "OpenVPN";
}

public sealed class IptvPlaylist
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "IPTV";
    public string Source { get; set; } = "";
    public string EpgSource { get; set; } = "";
}

public sealed class IptvChannel
{
    public string Name { get; set; } = "Channel";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "Other";
    public string Logo { get; set; } = "";
    public string TvgId { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public Guid PlaylistId { get; set; }
}

public sealed class AppState
{
    public string DashboardName { get; set; } = "StreamHub";
    public int Port { get; set; } = 8097;
    public bool RemoteAccessEnabled { get; set; }
    public bool DebugMode { get; set; }
    public string AdminUsername { get; set; } = "admin";
    public string PasswordSalt { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public List<ServiceItem> Services { get; set; } = [];
    public List<VpnProfile> VpnProfiles { get; set; } = [];
    public List<IptvPlaylist> IptvPlaylists { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamHub");
    public static string StateFile => Path.Combine(DataDirectory, "settings.json");

    public static AppState Load()
    {
        Directory.CreateDirectory(DataDirectory);
        if (File.Exists(StateFile))
        {
            try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile), JsonOptions) ?? CreateDefault(); }
            catch { }
        }
        return CreateDefault();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(StateFile, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void SetPassword(string password)
    {
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        PasswordSalt = Convert.ToBase64String(salt);
        PasswordHash = Convert.ToBase64String(hash);
    }

    public bool VerifyPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(PasswordSalt) || string.IsNullOrWhiteSpace(PasswordHash)) return false;
        try
        {
            var salt = Convert.FromBase64String(PasswordSalt);
            var expected = Convert.FromBase64String(PasswordHash);
            var actual = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(password, salt, 600_000, System.Security.Cryptography.HashAlgorithmName.SHA256, expected.Length);
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    private static AppState CreateDefault() => new()
    {
        Services =
        [
            new() { Name = "Netflix", Url = "https://www.netflix.com", Color = "#E50914" },
            new() { Name = "Tubi", Url = "https://tubitv.com", Color = "#FA4BFF" },
            new() { Name = "Hulu", Url = "https://www.hulu.com", Color = "#1CE783" },
            new() { Name = "YouTube", Url = "https://www.youtube.com", Color = "#FF0033" }
        ]
    };
}
