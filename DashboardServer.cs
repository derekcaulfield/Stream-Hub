using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace StreamHub;

public sealed class DashboardServer : IAsyncDisposable
{
    private readonly AppState _state;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new();
    private readonly ConcurrentDictionary<string, LoginAttempt> _attempts = new();
    private WebApplication? _app;

    public DashboardServer(AppState state) => _state = state;

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_state.Port}");
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
        });
        _app = builder.Build();
        _app.UseForwardedHeaders();
        _app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; img-src 'self' data: https:; media-src http: https: blob:; connect-src 'self' http: https:; frame-src http: https:";
            await next();
        });

        _app.MapGet("/health", () => Results.Ok(new { status = "ready" }));
        _app.MapGet("/", (HttpContext ctx) => IsAuthenticated(ctx) ? Results.Content(DashboardHtml, "text/html") : Results.Redirect("/login"));
        _app.MapGet("/login", (HttpContext ctx) => IsAuthenticated(ctx) ? Results.Redirect("/") : Results.Content(LoginHtml, "text/html"));
        _app.MapPost("/api/login", async (HttpContext ctx) =>
        {
            var key = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var now = DateTimeOffset.UtcNow;
            var attempt = _attempts.GetOrAdd(key, _ => new LoginAttempt());
            if (attempt.BlockedUntil > now) return Results.Json(new { error = "Too many attempts. Try again later." }, statusCode: 429);
            var login = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            if (login is null || !string.Equals(login.Username, _state.AdminUsername, StringComparison.Ordinal) || !_state.VerifyPassword(login.Password ?? ""))
            {
                attempt.Failures++;
                if (attempt.Failures >= 5) { attempt.Failures = 0; attempt.BlockedUntil = now.AddMinutes(5); }
                await Task.Delay(RandomNumberGenerator.GetInt32(250, 650));
                return Results.Json(new { error = "Invalid username or password." }, statusCode: 401);
            }
            _attempts.TryRemove(key, out _);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            _sessions[token] = now.AddHours(12);
            ctx.Response.Cookies.Append("streamhub_session", token, new CookieOptions { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromHours(12), Path = "/" });
            return Results.Ok(new { ok = true });
        });
        _app.MapPost("/api/logout", (HttpContext ctx) =>
        {
            if (ctx.Request.Cookies.TryGetValue("streamhub_session", out var token)) _sessions.TryRemove(token, out _);
            ctx.Response.Cookies.Delete("streamhub_session");
            return Results.Ok();
        });
        _app.MapGet("/api/services", (HttpContext ctx) => IsAuthenticated(ctx)
            ? Results.Ok(_state.Services.Select(x => new { x.Name, x.Url, x.Category, x.Color, x.OpenExternally }))
            : Results.Unauthorized());
        _app.MapGet("/api/channels", async Task<IResult> (HttpContext ctx) =>
        {
            if (!IsAuthenticated(ctx)) return Results.Unauthorized();
            var channels = new List<IptvChannel>();
            foreach (var playlist in _state.IptvPlaylists)
            {
                try { channels.AddRange(M3uParser.Parse(await M3uParser.ReadSourceAsync(playlist.Source), playlist.Id)); }
                catch { }
            }
            return Results.Ok(channels.Select(x => new { x.Name, x.Url, x.Group, x.Logo }));
        });
        await _app.StartAsync();
    }

    private bool IsAuthenticated(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue("streamhub_session", out var token)) return false;
        if (!_sessions.TryGetValue(token, out var expires) || expires <= DateTimeOffset.UtcNow) { _sessions.TryRemove(token, out _); return false; }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null) return;
        await _app.StopAsync(); await _app.DisposeAsync(); _app = null;
    }

    private sealed class LoginAttempt { public int Failures; public DateTimeOffset BlockedUntil; }
    private sealed record LoginRequest(string? Username, string? Password);

    private const string LoginHtml = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>StreamHub Login</title><style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;display:grid;place-items:center;font-family:Segoe UI,Arial;background:radial-gradient(circle at 80% 0,#1c3040,#090b10 55%);color:#fff}.card{width:min(420px,calc(100% - 32px));padding:38px;border:1px solid #2b3542;border-radius:22px;background:#11161edb;box-shadow:0 28px 80px #0008}.logo{width:48px;height:48px;display:grid;place-items:center;border-radius:14px;background:linear-gradient(135deg,#74f1cf,#36adef);color:#071016;font-size:26px;font-weight:800}h1{margin:24px 0 6px;font-size:28px}p{margin:0 0 26px;color:#98a3b5}label{display:block;margin:15px 0 7px;font-size:13px;color:#c9d0da}input{width:100%;padding:13px;border-radius:10px;border:1px solid #303a48;background:#171d26;color:#fff;outline:none}input:focus{border-color:#5ce1c0}button{width:100%;margin-top:22px;padding:13px;border:0;border-radius:10px;background:#5ce1c0;color:#07130f;font-weight:700;cursor:pointer}.error{height:20px;color:#ff8585;margin-top:14px;font-size:13px}</style></head><body><form class="card" id="login"><div class="logo">S</div><h1>Welcome back</h1><p>Sign in to your StreamHub.</p><label>Username</label><input id="user" autocomplete="username" required><label>Password</label><input id="pass" type="password" autocomplete="current-password" required><button>Sign in</button><div class="error" id="error"></div></form><script>login.onsubmit=async e=>{e.preventDefault();error.textContent='';const r=await fetch('/api/login',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({username:user.value,password:pass.value})});if(r.ok)location='/';else error.textContent=(await r.json()).error||'Unable to sign in.'}</script></body></html>
""";

    private const string DashboardHtml = """
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>StreamHub</title><script src="https://cdn.jsdelivr.net/npm/hls.js@1.6.16/dist/hls.min.js"></script><style>
*{box-sizing:border-box}body{margin:0;font-family:Segoe UI,Arial;background:radial-gradient(circle at 80% 0,#192638,#090b10 55%);color:#fff;min-height:100vh}.shell{display:grid;grid-template-columns:230px 1fr;min-height:100vh}aside{padding:28px 22px;border-right:1px solid #242b36;background:#0d1117dd}.brand{display:flex;align-items:center;gap:11px;font-weight:700}.logo{width:38px;height:38px;display:grid;place-items:center;border-radius:11px;background:linear-gradient(135deg,#74f1cf,#36adef);color:#071016;font-size:21px}.nav{margin-top:42px;color:#a5afbd}.nav button{width:100%;text-align:left;background:transparent;border-color:transparent}.nav .active{background:#24303c;color:#fff}main{padding:36px 42px;min-width:0}header,.section,.viewerbar{display:flex;justify-content:space-between;align-items:center}h1{font-size:30px;margin:0}header p{color:#909bad;margin:7px 0 0}button,.external{padding:10px 14px;border:1px solid #333c49;border-radius:9px;color:#fff;background:#1d2430;cursor:pointer;text-decoration:none}.hero{margin:28px 0;padding:28px;border-radius:18px;border:1px solid #344152;background:linear-gradient(135deg,#222e42,#121a25 55%,#163a3b)}.hero h2{margin:0 0 8px}.hero p{margin:0;color:#aeb9c8}.section{margin-bottom:16px}.section h2{font-size:20px}.section input{width:280px;padding:11px;border-radius:9px;border:1px solid #303946;background:#151a22;color:#fff}.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:18px}.card{position:relative;height:150px;padding:18px;border-radius:15px;color:#fff;overflow:hidden;border:1px solid #ffffff22;display:flex;flex-direction:column;justify-content:space-between;transition:.18s transform,.18s filter;cursor:pointer}.card:hover{transform:translateY(-4px);filter:brightness(1.12)}.mark{width:42px;height:42px;border-radius:12px;background:#ffffff28;display:grid;place-items:center;font-size:20px;font-weight:700}.arrow{position:absolute;right:18px;top:16px;font-size:20px}.name{font-size:19px;font-weight:650}.category{font-size:10px;letter-spacing:.08em;opacity:.75;margin-top:4px}.view{display:none}.view.active{display:block}.viewerbar{margin:0 0 12px}.viewer{width:100%;height:calc(100vh - 130px);border:1px solid #303947;border-radius:14px;background:#fff}.tv-layout{display:grid;grid-template-columns:340px 1fr;gap:18px}.channels{height:calc(100vh - 190px);overflow:auto}.channel{display:block;width:100%;text-align:left;margin:0 0 7px;padding:12px}.player{width:100%;height:calc(100vh - 190px);background:#000;border:1px solid #303947;border-radius:14px}.notice{color:#97a3b4;font-size:12px;margin-top:9px}@media(max-width:760px){.shell{display:block}aside{padding:14px}.brand{margin-bottom:10px}.nav{margin:0;display:flex}.nav button{width:auto}main{padding:20px}.tv-layout{display:block}.channels{height:260px;margin-bottom:14px}.player{height:45vh}.section{gap:12px;align-items:flex-start;flex-direction:column}.section input{width:100%}}</style></head><body><div class="shell"><aside><div class="brand"><div class="logo">S</div>STREAMHUB</div><div class="nav"><button class="active" data-view="home">⌂ &nbsp; Services</button><button data-view="tv">▶ &nbsp; Live TV</button></div></aside><main><header><div><h1>Your streaming space</h1><p>Everything you watch, one click away.</p></div><button id="logout">Sign out</button></header><section id="home" class="view active"><div class="hero"><h2>One home for every service.</h2><p>Services open inside StreamHub when their provider permits embedding.</p></div><div class="section"><h2>Your services</h2><input id="search" placeholder="Search services"></div><div class="grid" id="grid"></div></section><section id="viewer" class="view"><div class="viewerbar"><button id="back">← Back to services</button><div><span id="viewerName"></span> <a id="external" class="external" target="_blank" rel="noopener">Open externally ↗</a></div></div><iframe id="frame" class="viewer" referrerpolicy="no-referrer"></iframe><div class="notice">If a provider blocks embedded browsers or protected playback, use “Open externally”.</div></section><section id="tv" class="view"><div class="hero"><h2>Live TV</h2><p>Play channels from the playlists configured in the StreamHub desktop app.</p></div><div class="section"><h2>Channels</h2><input id="channelSearch" placeholder="Search channels"></div><div class="tv-layout"><div id="channels" class="channels"></div><div><video id="video" class="player" controls autoplay></video><div id="tvStatus" class="notice">Choose a channel to begin. Browser playback depends on stream CORS and codec support.</div></div></div></section></main></div><script>
let services=[],channelData=[],hls=null;const views=[home,viewer,tv];function show(v){views.forEach(x=>x.classList.toggle('active',x===v));document.querySelectorAll('.nav button').forEach(x=>x.classList.toggle('active',x.dataset.view===v.id))}function esc(v){return String(v).replace(/[&<>\"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','\"':'&quot;',"'":'&#39;'}[c]))}function paint(){const q=search.value.toLowerCase();grid.innerHTML=services.filter(s=>s.name.toLowerCase().includes(q)).map((s,i)=>`<div class="card" data-service="${services.indexOf(s)}" style="background:linear-gradient(135deg,${esc(s.color)},#13171f)"><div class="mark">${esc(s.name[0].toUpperCase())}</div><div class="arrow">↗</div><div><div class="name">${esc(s.name)}</div><div class="category">${esc(s.category.toUpperCase())}</div></div></div>`).join('');grid.querySelectorAll('[data-service]').forEach(x=>x.onclick=()=>openService(services[+x.dataset.service]))}function openService(s){if(s.openExternally){window.open(s.url,'_blank','noopener');return}viewerName.textContent=s.name;external.href=s.url;frame.src=s.url;show(viewer)}function paintChannels(){const q=channelSearch.value.toLowerCase();channels.innerHTML=channelData.filter(c=>c.name.toLowerCase().includes(q)).map(c=>`<button class="channel" data-channel="${channelData.indexOf(c)}"><b>${esc(c.name)}</b><br><small>${esc(c.group||'Other')}</small></button>`).join('');channels.querySelectorAll('[data-channel]').forEach(x=>x.onclick=()=>playChannel(channelData[+x.dataset.channel]))}function playChannel(c){tvStatus.textContent='Loading '+c.name+'…';if(hls){hls.destroy();hls=null}if(window.Hls&&Hls.isSupported()){hls=new Hls();hls.loadSource(c.url);hls.attachMedia(video);hls.on(Hls.Events.MANIFEST_PARSED,()=>video.play());hls.on(Hls.Events.ERROR,(_,d)=>{if(d.fatal)tvStatus.textContent='This stream could not play in the browser. Try it in the desktop app.'})}else{video.src=c.url;video.play().catch(()=>tvStatus.textContent='This browser cannot play the stream. Try the desktop app.')}tvStatus.textContent=c.name+' · '+(c.group||'Live TV')}
document.querySelectorAll('.nav button').forEach(x=>x.onclick=()=>show(document.getElementById(x.dataset.view)));back.onclick=()=>{frame.src='about:blank';show(home)};search.oninput=paint;channelSearch.oninput=paintChannels;fetch('/api/services').then(r=>{if(r.status===401)location='/login';return r.json()}).then(x=>{services=x;paint()});fetch('/api/channels').then(r=>r.json()).then(x=>{channelData=x;paintChannels()});logout.onclick=()=>fetch('/api/logout',{method:'POST'}).then(()=>location='/login');
</script></body></html>
""";
}
