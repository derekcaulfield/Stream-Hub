# StreamHub

StreamHub is a Windows streaming-service launcher with an Emby-inspired library layout. It uses Microsoft WebView2 for in-app browsing and can fall back to the system browser when a provider does not support embedded playback.

## Current features

- Configurable streaming-service tiles
- Persistent settings stored under `%LOCALAPPDATA%\StreamHub`
- In-app WebView2 browser with navigation controls
- Per-service external-browser launch mode
- `.ovpn` profile import
- OpenVPN Community connect/disconnect controls
- WireGuard `.conf` import and Windows tunnel-service controls
- Custom dashboard name and reserved local-server port
- Authenticated local web dashboard for Tailscale Serve or Funnel
- PBKDF2-SHA256 password hashing and login-attempt throttling
- User-supplied M3U/M3U8 IPTV playlists from URLs or local files
- IPTV channel groups, search, embedded LibVLC playback, and fullscreen mode
- Optional XMLTV guide source stored per playlist

## Run from source

```powershell
dotnet run
```

The project targets .NET 10 for Windows. The WebView2 Runtime is included with most current Windows installations. OpenVPN control requires OpenVPN Community to be installed; connecting may prompt for administrator permission.

IPTV playback uses LibVLCSharp with the bundled Windows LibVLC runtime. StreamHub does not include channel lists; add only playlists you are authorized to use.

## Tailscale exposure

Enable remote access and create an administrator password in StreamHub settings. The server listens only on `127.0.0.1` at the configured port (default `8097`). On the machine that runs StreamHub and Tailscale, expose it with:

```powershell
tailscale serve 8097
```

For intentionally public internet access, use `tailscale funnel 8097` instead. Funnel supplies the public HTTPS endpoint; StreamHub still requires its own login.

## Notes

Streaming providers decide whether protected playback and sign-in work in embedded browsers. Use **Open externally** for services that reject WebView2 or require their native application. VPN profiles can contain sensitive details and are copied into the current Windows user's local application data folder.
