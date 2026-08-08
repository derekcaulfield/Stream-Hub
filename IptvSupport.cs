using System.Text.RegularExpressions;
using System.IO;
using System.Net.Http;

namespace StreamHub;

public static partial class M3uParser
{
    [GeneratedRegex("([A-Za-z0-9_-]+)=\"([^\"]*)\"")]
    private static partial Regex AttributePattern();

    public static List<IptvChannel> Parse(string content, Guid playlistId)
    {
        var channels = new List<IptvChannel>();
        string? metadata = null;
        string userAgent = "";
        foreach (var raw in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase)) { metadata = line; continue; }
            if (metadata is not null && line.StartsWith("#EXTVLCOPT:http-user-agent=", StringComparison.OrdinalIgnoreCase))
            {
                userAgent = line["#EXTVLCOPT:http-user-agent=".Length..].Trim();
                continue;
            }
            if (metadata is null || line.StartsWith('#')) continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out _)) { metadata = null; continue; }
            var attributes = AttributePattern().Matches(metadata).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);
            var comma = metadata.LastIndexOf(',');
            var name = comma >= 0 ? metadata[(comma + 1)..].Trim() : "Channel";
            channels.Add(new IptvChannel { PlaylistId = playlistId, Name = string.IsNullOrWhiteSpace(name) ? "Channel" : name, Url = line, Group = attributes.GetValueOrDefault("group-title", "Other"), Logo = attributes.GetValueOrDefault("tvg-logo", ""), TvgId = attributes.GetValueOrDefault("tvg-id", ""), UserAgent = userAgent });
            metadata = null; userAgent = "";
        }
        return channels;
    }

    public static async Task<string> ReadSourceAsync(string source)
    {
        var trimmed = source.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        if (trimmed.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) || (trimmed.Contains("#EXTINF", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('\n')))
            return source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamHub/1.0");
            return await client.GetStringAsync(uri);
        }
        return await File.ReadAllTextAsync(source);
    }
}
