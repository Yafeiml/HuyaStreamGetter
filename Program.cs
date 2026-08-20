#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using LiveStreamGateway;

// -------------------------------------------------------------
// Top-Level Application Setup
// -------------------------------------------------------------
bool interactiveConsole = !Console.IsInputRedirected && !Console.IsOutputRedirected;
try {
    Console.Title = "LiveStreamGateway - .NET 10";
    if (interactiveConsole) Console.CursorVisible = false;
    Console.OutputEncoding = Encoding.UTF8;
} catch { }

if (!await LoadConfigAsync())
{
    ShowError($"无法加载 {Globals.CONFIG_FILE_NAME}，请检查文件。");
    return;
}

Globals.LocalIp = GetLocalIPAddress();

// Configure WebApplication
var builder = WebApplication.CreateBuilder(args);

// 容器环境中保留应用生命周期和故障日志，但关闭逐请求 Information 噪声。
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

// Configure Kestrel to listen on the specified port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(Globals.HTTP_PORT);
});

// Add Hosted Services for HealthCheck and UI Render
builder.Services.AddHostedService<RenderService>();
builder.Services.AddHostedService<StreamManagerService>();
builder.Services.AddSingleton<AdminAuthService>();

var app = builder.Build();
var adminAuth = app.Services.GetRequiredService<AdminAuthService>();

// 管理 API 安全边界：播放链路及健康检查保持公开，其余 /api/* 必须持有登录会话。
// SameSite=Strict 会话 Cookie 配合自定义请求头，阻断常见 CSRF 表单请求。
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments("/api"))
            context.Response.Headers["Cache-Control"] = "no-store";
        return Task.CompletedTask;
    });

    string path = context.Request.Path.Value ?? "";
    bool publicApi = path.Equals("/api/health", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/ready", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase) ||
                     path.Equals("/api/auth/setup", StringComparison.OrdinalIgnoreCase);

    if (context.Request.Path.StartsWithSegments("/api") && !publicApi)
    {
        if (!adminAuth.IsAuthenticated(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "LSG-Session realm=\"LiveStreamGateway\"";
            await context.Response.WriteAsJsonAsync(new { error = "管理会话无效或已过期，请重新登录" });
            return;
        }

        bool unsafeMethod = !HttpMethods.IsGet(context.Request.Method) &&
                            !HttpMethods.IsHead(context.Request.Method) &&
                            !HttpMethods.IsOptions(context.Request.Method);
        if (unsafeMethod && context.Request.Headers["X-LSG-Request"] != "1")
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = "缺少管理请求校验头" });
            return;
        }
    }

    await next();
});

// Enable default files (index.html) and static files from wwwroot (Web UI)
app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure HLS directory exists for static files provider
if (!Directory.Exists(Globals.HLS_FULL_PATH))
{
    Directory.CreateDirectory(Globals.HLS_FULL_PATH);
}

// HLS 文件不通过通用静态目录暴露，避免绕过独立播放令牌；仅由下方受保护端点读取。

// -------------------------------------------------------------
// Management Authentication APIs
// -------------------------------------------------------------

app.MapGet("/api/auth/session", (HttpContext context) =>
{
    bool authenticated = adminAuth.IsAuthenticated(context.Request);
    bool setupRequired = !adminAuth.IsPasswordConfigured;
    return Results.Json(new
    {
        authenticated,
        setupRequired,
        setupCodeRequired = setupRequired && !AdminAuthService.CanSetupWithoutCode(context),
        sessionLifetimeSeconds = (int)AdminAuthService.SessionLifetime.TotalSeconds,
        secureTransport = context.Request.IsHttps
    });
});

app.MapPost("/api/auth/login", (AuthLoginRequest request, HttpContext context) =>
{
    if (!adminAuth.IsPasswordConfigured)
        return Results.Json(new { error = "管理员密码尚未初始化" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    if (request.Password.Length > 256)
        return Results.BadRequest(new { error = "密码格式无效" });

    string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    bool valid = adminAuth.TryValidateCredentials(request.Password, clientKey, out bool rateLimited, out TimeSpan retryAfter);
    if (!valid)
    {
        if (rateLimited)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers["Retry-After"] = seconds.ToString();
            Console.WriteLine($"[Security] 管理端登录触发限速，来源={clientKey}，等待={seconds}s");
            return Results.Json(new { error = "登录尝试过于频繁，请稍后再试", retryAfterSeconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests);
        }

        return Results.Json(new { error = "密码错误" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    string token = adminAuth.CreateSession(request.Password);
    AdminAuthService.AppendSessionCookie(context.Response, token, context.Request.IsHttps);
    return Results.Ok(new { success = true, expiresInSeconds = (int)AdminAuthService.SessionLifetime.TotalSeconds });
});

app.MapPost("/api/auth/logout", (HttpContext context) =>
{
    adminAuth.RevokeSession(context.Request);
    AdminAuthService.DeleteSessionCookie(context.Response, context.Request.IsHttps);
    return Results.Ok(new { success = true });
});

// 首次安装引导：本机直连可直接设置；Docker/NAS/反代访问需提供启动日志中的一次性验证码。
app.MapPost("/api/auth/setup", async (HttpContext context) =>
{
    if (adminAuth.IsPasswordConfigured)
        return Results.Conflict(new { error = "管理员密码已经初始化" });
    if (context.Request.Headers["X-LSG-Request"] != "1")
        return Results.BadRequest(new { error = "缺少首次设置请求校验头" });

    if (context.Request.ContentLength is > 2048)
        return Results.BadRequest(new { error = "密码请求体过大" });

    var setupRequest = new AuthSetupRequest();
    if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
    {
        try
        {
            setupRequest = await context.Request.ReadFromJsonAsync<AuthSetupRequest>() ?? new AuthSetupRequest();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "首次设置请求格式无效" });
        }
        catch (BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "首次设置请求格式无效" });
        }
    }
    else
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
        setupRequest.Password = (await reader.ReadToEndAsync()).TrimEnd('\r', '\n');
    }

    if (!AdminAuthService.CanSetupWithoutCode(context))
    {
        string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        bool validCode = adminAuth.TryValidateSetupCode(setupRequest.SetupCode, clientKey, out bool rateLimited, out TimeSpan retryAfter);
        if (!validCode)
        {
            if (rateLimited)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                context.Response.Headers["Retry-After"] = seconds.ToString();
                Console.WriteLine($"[Security] 首次设置验证码触发限速，来源={clientKey}，等待={seconds}s");
                return Results.Json(
                    new { error = $"初始化码尝试过于频繁，请在 {seconds} 秒后重试", retryAfterSeconds = seconds },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            return Results.Json(
                new { error = "一次性初始化码无效，请查看本次启动的最新服务日志" },
                statusCode: StatusCodes.Status403Forbidden);
        }
    }

    string password = setupRequest.Password;
    string? passwordError = AdminAuthService.ValidateNewPassword(password);
    if (passwordError != null)
        return Results.BadRequest(new { error = passwordError });

    PlaybackTokenCredentials playbackCredentials = PlaybackTokenProtector.Create(password);
    lock (Globals.ConfigLock)
    {
        if (!string.IsNullOrWhiteSpace(Globals.Config.AdminPasswordHash))
            return Results.Conflict(new { error = "管理员密码已经初始化" });
        Globals.Config.AdminPasswordHash = AdminAuthService.HashPassword(password);
        Globals.Config.PlaybackTokenHash = playbackCredentials.Hash;
        Globals.Config.PlaybackTokenEncrypted = playbackCredentials.Encrypted;
    }

    if (!await SaveConfigAsync())
    {
        lock (Globals.ConfigLock)
        {
            Globals.Config.AdminPasswordHash = "";
            Globals.Config.PlaybackTokenHash = "";
            Globals.Config.PlaybackTokenEncrypted = "";
        }
        return Results.Json(new { error = "密码配置写入失败" }, statusCode: StatusCodes.Status500InternalServerError);
    }

    adminAuth.CompleteInitialSetup();
    adminAuth.InvalidateAllSessions();
    string token = adminAuth.CreateSession(password);
    AdminAuthService.AppendSessionCookie(context.Response, token, context.Request.IsHttps);
    Console.WriteLine("[Security] 管理员密码与独立播放令牌已通过首次设置引导创建，一次性初始化码已作废");
    return Results.Ok(new
    {
        success = true,
        playbackToken = playbackCredentials.Token,
        m3uPath = $"/p/{playbackCredentials.Token}/jellyfin.m3u"
    });
});

app.MapPost("/api/auth/change-password", async (AuthPasswordChangeRequest request, HttpContext context) =>
{
    string? passwordError = AdminAuthService.ValidateNewPassword(request.NewPassword);
    if (passwordError != null)
        return Results.BadRequest(new { error = passwordError });
    if (request.NewPassword == request.CurrentPassword)
        return Results.BadRequest(new { error = "新密码不能与当前密码相同" });

    string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    bool valid = adminAuth.TryValidateCredentials(request.CurrentPassword, clientKey, out bool rateLimited, out TimeSpan retryAfter);
    if (!valid)
    {
        if (rateLimited)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers["Retry-After"] = seconds.ToString();
            return Results.Json(new { error = "验证尝试过于频繁，请稍后再试", retryAfterSeconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        return Results.Json(new { error = "当前密码错误" }, statusCode: StatusCodes.Status403Forbidden);
    }

    string oldHash;
    string oldPlaybackHash;
    string oldPlaybackEncrypted;
    lock (Globals.ConfigLock)
    {
        oldHash = Globals.Config.AdminPasswordHash;
        oldPlaybackHash = Globals.Config.PlaybackTokenHash;
        oldPlaybackEncrypted = Globals.Config.PlaybackTokenEncrypted;
    }

    string playbackToken;
    if (string.IsNullOrWhiteSpace(oldPlaybackHash) && string.IsNullOrWhiteSpace(oldPlaybackEncrypted))
    {
        playbackToken = PlaybackTokenProtector.Create(request.CurrentPassword).Token;
    }
    else if (!PlaybackTokenProtector.TryUnprotect(oldPlaybackEncrypted, request.CurrentPassword, out playbackToken) ||
             !PlaybackTokenProtector.ValidateToken(playbackToken, oldPlaybackHash))
    {
        return Results.Json(new { error = "无法解密现有播放令牌，密码未更改；请先修复配置或轮换播放令牌" }, statusCode: StatusCodes.Status500InternalServerError);
    }

    string newPlaybackHash = PlaybackTokenProtector.HashToken(playbackToken);
    string newPlaybackEncrypted = PlaybackTokenProtector.Protect(playbackToken, request.NewPassword);
    lock (Globals.ConfigLock)
    {
        Globals.Config.AdminPasswordHash = AdminAuthService.HashPassword(request.NewPassword);
        Globals.Config.PlaybackTokenHash = newPlaybackHash;
        Globals.Config.PlaybackTokenEncrypted = newPlaybackEncrypted;
    }

    if (!await SaveConfigAsync())
    {
        lock (Globals.ConfigLock)
        {
            Globals.Config.AdminPasswordHash = oldHash;
            Globals.Config.PlaybackTokenHash = oldPlaybackHash;
            Globals.Config.PlaybackTokenEncrypted = oldPlaybackEncrypted;
        }
        return Results.Json(new { error = "新密码写入失败" }, statusCode: StatusCodes.Status500InternalServerError);
    }

    adminAuth.InvalidateAllSessions();
    string token = adminAuth.CreateSession(request.NewPassword);
    AdminAuthService.AppendSessionCookie(context.Response, token, context.Request.IsHttps);
    Console.WriteLine("[Security] 管理员密码已修改，旧会话已全部失效");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/playback-token/rotate", async (PlaybackTokenRotateRequest request, HttpContext context) =>
{
    if (request.CurrentPassword.Length > 256)
        return Results.BadRequest(new { error = "密码格式无效" });

    string clientKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    bool valid = adminAuth.TryValidateCredentials(request.CurrentPassword, clientKey, out bool rateLimited, out TimeSpan retryAfter);
    if (!valid)
    {
        if (rateLimited)
        {
            int seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
            context.Response.Headers["Retry-After"] = seconds.ToString();
            return Results.Json(new { error = "验证尝试过于频繁，请稍后再试", retryAfterSeconds = seconds }, statusCode: StatusCodes.Status429TooManyRequests);
        }
        return Results.Json(new { error = "当前管理员密码错误" }, statusCode: StatusCodes.Status403Forbidden);
    }

    PlaybackTokenCredentials credentials = PlaybackTokenProtector.Create(request.CurrentPassword);
    string oldHash;
    string oldEncrypted;
    lock (Globals.ConfigLock)
    {
        oldHash = Globals.Config.PlaybackTokenHash;
        oldEncrypted = Globals.Config.PlaybackTokenEncrypted;
        Globals.Config.PlaybackTokenHash = credentials.Hash;
        Globals.Config.PlaybackTokenEncrypted = credentials.Encrypted;
    }

    if (!await SaveConfigAsync())
    {
        lock (Globals.ConfigLock)
        {
            Globals.Config.PlaybackTokenHash = oldHash;
            Globals.Config.PlaybackTokenEncrypted = oldEncrypted;
        }
        return Results.Json(new { error = "播放令牌写入失败，旧令牌仍然有效" }, statusCode: StatusCodes.Status500InternalServerError);
    }

    adminAuth.InvalidateAllSessions();
    string sessionToken = adminAuth.CreateSession(request.CurrentPassword);
    AdminAuthService.AppendSessionCookie(context.Response, sessionToken, context.Request.IsHttps);
    string m3uUrl = $"{ResolveEffectiveBaseUrl(context.Request)}/p/{credentials.Token}/jellyfin.m3u";
    Console.WriteLine("[Security] 独立播放令牌已轮换，旧 M3U/HLS 地址立即失效");
    return Results.Ok(new { success = true, playbackToken = credentials.Token, m3uUrl });
});

// -------------------------------------------------------------
// RESTful Management APIs
// -------------------------------------------------------------

// 0. 自动获取直播间主播名称与推荐 ID (自动探测平台与清理 URL)
app.MapGet("/api/channels/fetch-info", async (string? url, string? platform) =>
{
    if (string.IsNullOrWhiteSpace(url))
        return Results.BadRequest(new { error = "请输入直播间链接或房间号" });

    string input = url.Trim();
    string detectedPlatform = (platform ?? "").ToLower();
    string cleanUrl = input;
    string roomId = "";

    // 1. 智能探测平台 & 清理 URL 冗余跟踪参数
    if (input.Contains("bilibili.com") || input.Contains("b23.tv"))
    {
        detectedPlatform = "bilibili";
        var m = System.Text.RegularExpressions.Regex.Match(input, @"(?:live\.bilibili\.com/|b23\.tv/)(\d+)");
        if (m.Success)
        {
            roomId = m.Groups[1].Value;
            cleanUrl = $"https://live.bilibili.com/{roomId}";
        }
        else
        {
            roomId = input.Split('?')[0].TrimEnd('/').Split('/').Last();
            cleanUrl = $"https://live.bilibili.com/{roomId}";
        }
    }
    else if (input.Contains("huya.com"))
    {
        detectedPlatform = "huya";
        var m = System.Text.RegularExpressions.Regex.Match(input, @"huya\.com/([a-zA-Z0-9_-]+)");
        if (m.Success)
        {
            roomId = m.Groups[1].Value;
            cleanUrl = $"https://www.huya.com/{roomId}";
        }
        else
        {
            roomId = input.Split('?')[0].TrimEnd('/').Split('/').Last();
            cleanUrl = $"https://www.huya.com/{roomId}";
        }
    }
    else if (input.Contains("douyu.com"))
    {
        detectedPlatform = "douyu";
        var mRid = System.Text.RegularExpressions.Regex.Match(input, @"rid=(\d+)");
        var mNum = System.Text.RegularExpressions.Regex.Match(input, @"douyu\.com/(\d+)");
        if (mRid.Success) roomId = mRid.Groups[1].Value;
        else if (mNum.Success) roomId = mNum.Groups[1].Value;
        else
        {
            roomId = input.Split('?')[0].TrimEnd('/').Split('/').Last();
        }
        cleanUrl = $"https://www.douyu.com/{roomId}";
    }
    else
    {
        // 纯数字或房间号输入
        if (string.IsNullOrEmpty(detectedPlatform))
        {
            detectedPlatform = "huya";
        }
        roomId = input.Split('?')[0].TrimEnd('/').Split('/').Last();
        if (detectedPlatform == "huya") cleanUrl = $"https://www.huya.com/{roomId}";
        else if (detectedPlatform == "douyu") cleanUrl = $"https://www.douyu.com/{roomId}";
        else if (detectedPlatform == "bilibili") cleanUrl = $"https://live.bilibili.com/{roomId}";
    }

    try
    {
        string? name = null;
        string? suggestedId = null;
        string? title = null;

        if (detectedPlatform == "huya")
        {
            suggestedId = $"huya_{roomId.ToLower()}";

            string pageUrl = cleanUrl.StartsWith("http") ? cleanUrl : $"https://www.huya.com/{roomId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            
            using var resp = await Globals.HttpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                string html = await resp.Content.ReadAsStringAsync();
                var hostMatch = System.Text.RegularExpressions.Regex.Match(html, @"host-name"" title=""([^""]+)""");
                var titleMatch = System.Text.RegularExpressions.Regex.Match(html, @"host-title"" title=""([^""]+)""");
                
                string hostName = hostMatch.Success ? hostMatch.Groups[1].Value.Trim() : "";
                title = titleMatch.Success ? titleMatch.Groups[1].Value.Trim() : "";
                
                if (!string.IsNullOrEmpty(hostName))
                {
                    name = $"虎牙-{hostName}";
                }
                else
                {
                    name = $"虎牙-{roomId}";
                }
            }
        }
        else if (detectedPlatform == "douyu")
        {
            suggestedId = $"douyu_{roomId}";
            string apiUrl = $"http://open.douyucdn.cn/api/RoomApi/room/{roomId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            
            using var resp = await Globals.HttpClient.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var jsonStr = await resp.Content.ReadAsStringAsync();
                var json = System.Text.Json.Nodes.JsonNode.Parse(jsonStr);
                string owner = json?["data"]?["owner_name"]?.GetValue<string>() ?? "";
                title = json?["data"]?["room_name"]?.GetValue<string>() ?? "";
                
                name = !string.IsNullOrEmpty(owner) ? $"斗鱼-{owner}" : $"斗鱼-{roomId}";
            }
        }
        else if (detectedPlatform == "bilibili")
        {
            suggestedId = $"bilibili_{roomId}";

            string userApi = $"https://api.live.bilibili.com/live_user/v1/UserInfo/get_anchor_in_room?roomid={roomId}";
            string roomApi = $"https://api.live.bilibili.com/room/v1/Room/get_info?room_id={roomId}";
            
            using var uReq = new HttpRequestMessage(HttpMethod.Get, userApi);
            uReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var uResp = await Globals.HttpClient.SendAsync(uReq);

            string anchorName = "";
            if (uResp.IsSuccessStatusCode)
            {
                var uJson = System.Text.Json.Nodes.JsonNode.Parse(await uResp.Content.ReadAsStringAsync());
                anchorName = uJson?["data"]?["info"]?["uname"]?.GetValue<string>() ?? "";
            }

            using var rReq = new HttpRequestMessage(HttpMethod.Get, roomApi);
            rReq.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            using var rResp = await Globals.HttpClient.SendAsync(rReq);
            if (rResp.IsSuccessStatusCode)
            {
                var rJson = System.Text.Json.Nodes.JsonNode.Parse(await rResp.Content.ReadAsStringAsync());
                title = rJson?["data"]?["title"]?.GetValue<string>() ?? "";
            }

            name = !string.IsNullOrEmpty(anchorName) ? $"B站-{anchorName}" : $"B站-{roomId}";
        }

        if (string.IsNullOrEmpty(name))
        {
            name = $"{detectedPlatform}_{roomId}";
        }

        return Results.Json(new
        {
            success = true,
            platform = detectedPlatform,
            cleanUrl,
            roomId,
            name,
            suggestedId,
            title
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { 
            success = false, 
            platform = detectedPlatform,
            cleanUrl,
            name = $"{detectedPlatform}_{roomId}",
            suggestedId = $"{detectedPlatform}_{roomId}",
            message = $"自动获取失败: {ex.Message}" 
        });
    }
});

// 1. 获取全局系统状态与频道状态大盘
app.MapGet("/api/status", (HttpRequest request) =>
{
    var uptime = DateTime.UtcNow - Globals.StartTimeUtc;
    var channelStatusList = new List<object>();
    bool playbackTokenAvailable = adminAuth.TryGetPlaybackToken(request, out string playbackToken);
    string playbackPrefix = playbackTokenAvailable ? $"/p/{Uri.EscapeDataString(playbackToken)}" : "";
    bool playbackTokenConfigured;
    lock (Globals.ConfigLock)
        playbackTokenConfigured = !string.IsNullOrWhiteSpace(Globals.Config.PlaybackTokenHash);

    string effectiveBaseUrl = ResolveEffectiveBaseUrl(request);
    string displayHost = "";
    string clientHost = "";
    try
    {
        var uri = new Uri(effectiveBaseUrl);
        displayHost = uri.Authority;
        clientHost = uri.Host;
    }
    catch
    {
        displayHost = request.Host.HasValue ? request.Host.Value : $"{Globals.LocalIp}:{Globals.HTTP_PORT}";
        clientHost = request.Host.Host;
    }

    lock (Globals.StatusLock)
    {
        foreach (var channel in Globals.Config.Channels)
        {
            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channel.Id);
            string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channel.Id, "stream.m3u8");
            bool isStreaming = channel.Enable &&
                status?.State == ChannelState.Streaming &&
                IsFreshNonEmptyFile(m3u8Path, TimeSpan.FromSeconds(Globals.HLS_MANIFEST_FRESH_SECONDS));

            string platformKey = (channel.Platform ?? "").ToLower();
            Globals.PlatformCookieStatuses.TryGetValue(platformKey, out var cookieStatus);

            channelStatusList.Add(new
            {
                id = channel.Id,
                name = channel.Name,
                platform = channel.Platform,
                url = channel.Url,
                quality = string.IsNullOrEmpty(channel.Quality) ? "OD" : channel.Quality,
                cookieProfileKey = channel.CookieProfileKey ?? "",
                enable = channel.Enable,
                statusMessage = status?.Message ?? "未知",
                color = status?.Color.ToString() ?? "Gray",
                retryCount = status?.RetryCount ?? 0,
                channelState = status?.State.ToString() ?? "Idle",
                isLive = isStreaming,
                isCookieConfigured = cookieStatus?.Configured ?? (!string.IsNullOrWhiteSpace(channel.Cookies)),
                isCookieValid = cookieStatus?.IsValid ?? false,
                isCookieNetworkError = cookieStatus?.IsNetworkError ?? false,
                cookieUsername = cookieStatus?.Username ?? "",
                cookieStatusMessage = cookieStatus?.Message ?? "",
                hlsUrl = playbackTokenAvailable ? $"{playbackPrefix}/live/{channel.Id}/stream.m3u8" : (string?)null,
                fullHlsUrl = playbackTokenAvailable ? $"{effectiveBaseUrl}{playbackPrefix}/live/{channel.Id}/stream.m3u8" : (string?)null
            });
        }
    }

    int activeCount = channelStatusList.Count(c => (bool)((dynamic)c).isLive);

    return Results.Json(new
    {
        version = Globals.APP_VERSION,
        serverStatus = "运行中",
        localIp = clientHost,
        httpPort = request.Host.Port ?? Globals.HTTP_PORT,
        displayHost = displayHost,
        customHost = Globals.Config.CustomHost ?? "",
        m3uUrl = playbackTokenAvailable ? $"{effectiveBaseUrl}{playbackPrefix}/jellyfin.m3u" : (string?)null,
        playbackTokenConfigured,
        playbackTokenAvailable,
        uptimeSeconds = (int)uptime.TotalSeconds,
        uptimeText = $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分 {uptime.Seconds}秒",
        activeStreams = activeCount,
        totalChannels = Globals.Config.Channels.Count,
        channels = channelStatusList,
        cookieStatuses = Globals.PlatformCookieStatuses
    });
});

// 2. 获取配置（绝不回传管理员哈希、Cookie 原文或频道运行时 Cookie 副本）
app.MapGet("/api/config", () =>
{
    lock (Globals.ConfigLock)
    {
        var safeChannels = Globals.Config.Channels.Select(channel => new
        {
            id = channel.Id,
            name = channel.Name,
            platform = channel.Platform,
            url = channel.Url,
            quality = channel.Quality,
            cookieProfileKey = channel.CookieProfileKey ?? "",
            enable = channel.Enable
        }).ToArray();
        var cookieConfigured = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["huya"] = Globals.Config.CookieProfiles.TryGetValue("huya", out string? huya) && !string.IsNullOrWhiteSpace(huya),
            ["douyu"] = Globals.Config.CookieProfiles.TryGetValue("douyu", out string? douyu) && !string.IsNullOrWhiteSpace(douyu),
            ["bilibili"] = Globals.Config.CookieProfiles.TryGetValue("bilibili", out string? bilibili) && !string.IsNullOrWhiteSpace(bilibili)
        };

        return Results.Json(new
        {
            customHost = Globals.Config.CustomHost ?? "",
            channels = safeChannels,
            cookieConfigured,
            cookieStatuses = Globals.PlatformCookieStatuses
        });
    }
});

// 3. 添加或更新频道
app.MapPost("/api/channels", async (ChannelConfig newChannel) =>
{
    if (string.IsNullOrWhiteSpace(newChannel.Url))
        return Results.BadRequest(new { error = "直播间链接不能为空" });

    string url = newChannel.Url.Trim();
    string platform = (newChannel.Platform ?? "").ToLower();

    // 自动判定平台
    if (string.IsNullOrEmpty(platform) || platform == "auto")
    {
        if (url.Contains("bilibili.com") || url.Contains("b23.tv")) platform = "bilibili";
        else if (url.Contains("huya.com")) platform = "huya";
        else if (url.Contains("douyu.com")) platform = "douyu";
        else platform = "huya";
    }

    // 自动清洗 URL 冗余参数
    if (platform == "bilibili")
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:live\.bilibili\.com/|b23\.tv/)(\d+)");
        if (m.Success) newChannel.Url = $"https://live.bilibili.com/{m.Groups[1].Value}";
        else newChannel.Url = url.Split('?')[0];
    }
    else if (platform == "huya")
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"huya\.com/([a-zA-Z0-9_-]+)");
        if (m.Success) newChannel.Url = $"https://www.huya.com/{m.Groups[1].Value}";
        else newChannel.Url = url.Split('?')[0];
    }
    else if (platform == "douyu")
    {
        var mRid = System.Text.RegularExpressions.Regex.Match(url, @"rid=(\d+)");
        var mNum = System.Text.RegularExpressions.Regex.Match(url, @"douyu\.com/(\d+)");
        if (mRid.Success) newChannel.Url = $"https://www.douyu.com/{mRid.Groups[1].Value}";
        else if (mNum.Success) newChannel.Url = $"https://www.douyu.com/{mNum.Groups[1].Value}";
        else newChannel.Url = url.Split('?')[0];
    }

    newChannel.Platform = platform;
    newChannel.CookieProfileKey = platform; // 固化绑定平台 Cookie

    if (string.IsNullOrWhiteSpace(newChannel.Name))
    {
        newChannel.Name = $"{platform}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    if (string.IsNullOrWhiteSpace(newChannel.Id))
    {
        newChannel.Id = $"{platform}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }
    else
    {
        newChannel.Id = newChannel.Id.Trim().Replace(" ", "_");
    }

    lock (Globals.ConfigLock)
    {
        var existing = Globals.Config.Channels.FirstOrDefault(c => c.Id == newChannel.Id);
        if (existing != null)
        {
            existing.Name = newChannel.Name;
            existing.Platform = newChannel.Platform;
            existing.Url = newChannel.Url;
            existing.Quality = string.IsNullOrWhiteSpace(newChannel.Quality) ? "OD" : newChannel.Quality;
            existing.CookieProfileKey = platform;
            existing.Enable = newChannel.Enable;
        }
        else
        {
            newChannel.Quality = string.IsNullOrWhiteSpace(newChannel.Quality) ? "OD" : newChannel.Quality;
            newChannel.CookieProfileKey = platform;
            Globals.Config.Channels.Add(newChannel);
        }

        RefreshChannelCookies();
    }

    await SaveConfigAsync();
    Globals.StreamManager?.NotifyConfigChanged();

    return Results.Ok(new { success = true, channel = newChannel });
});

// 4. 删除频道
app.MapDelete("/api/channels/{id}", async (string id) =>
{
    ChannelConfig? removed = null;
    lock (Globals.ConfigLock)
    {
        removed = Globals.Config.Channels.FirstOrDefault(c => c.Id == id);
        if (removed != null)
        {
            Globals.Config.Channels.Remove(removed);
        }
    }

    if (removed != null)
    {
        Globals.Extractors.TryRemove(id, out _);
        Globals.M3u8Cache.TryRemove(id, out _);
        if (Globals.StreamManager != null)
            await Globals.StreamManager.StopAndCleanChannelAsync(id);
        
        lock (Globals.StatusLock)
        {
            var st = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == id);
            if (st != null) Globals.ChannelStatuses.Remove(st);
        }

        await SaveConfigAsync();
        Globals.StreamManager?.NotifyConfigChanged();
        return Results.Ok(new { success = true, message = $"频道 {removed.Name} 已删除" });
    }

    return Results.NotFound(new { error = "未找到指定频道" });
});

// 5. 一键切换频道启用/禁用状态
app.MapPost("/api/channels/{id}/toggle", async (string id) =>
{
    bool newState = false;
    lock (Globals.ConfigLock)
    {
        var channel = Globals.Config.Channels.FirstOrDefault(c => c.Id == id);
        if (channel == null)
            return Results.NotFound(new { error = "未找到指定频道" });

        channel.Enable = !channel.Enable;
        newState = channel.Enable;
    }

    await SaveConfigAsync();
    Globals.StreamManager?.NotifyConfigChanged();

    return Results.Ok(new { success = true, id, enable = newState });
});

// 6. 手动重启指定频道流
app.MapPost("/api/channels/{id}/restart", async (string id) =>
{
    var channel = Globals.Config.Channels.FirstOrDefault(c => c.Id == id);
    if (channel == null)
        return Results.NotFound(new { error = "未找到指定频道" });

    Globals.Extractors.TryRemove(id, out _);
    Globals.M3u8Cache.TryRemove(id, out _);
    if (Globals.StreamManager != null)
        await Globals.StreamManager.RestartChannelAsync(id);

    return Results.Ok(new { success = true, message = $"已触发频道 {channel.Name} 重启" });
});

// 7. 保存指定平台 Cookie (huya, douyu, bilibili) 并自动检测有效性
app.MapPost("/api/cookies", async (CookieProfileRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Key))
        return Results.BadRequest(new { error = "平台类型不能为空" });

    string key = req.Key.Trim().ToLower();
    if (key != "huya" && key != "douyu" && key != "bilibili")
    {
        return Results.BadRequest(new { error = "仅支持配置 huya (虎牙), douyu (斗鱼), bilibili (B站) 的 Cookie" });
    }

    lock (Globals.ConfigLock)
    {
        Globals.Config.CookieProfiles[key] = req.Cookie ?? "";
        RefreshChannelCookies();
    }

    await SaveConfigAsync();
    Globals.StreamManager?.NotifyConfigChanged();

    // 自动发起一次有效性检测
    var status = await Globals.CheckPlatformCookieAsync(key);

    return Results.Ok(new { success = true, key, configured = !string.IsNullOrWhiteSpace(req.Cookie), status, statuses = Globals.PlatformCookieStatuses });
});

// 8. 清空指定平台 Cookie
app.MapDelete("/api/cookies/{key}", async (string key) =>
{
    string k = (key ?? "").Trim().ToLower();
    lock (Globals.ConfigLock)
    {
        if (Globals.Config.CookieProfiles.ContainsKey(k))
        {
            Globals.Config.CookieProfiles[k] = "";
            RefreshChannelCookies();
        }
    }

    Globals.PlatformCookieStatuses[k] = new PlatformCookieStatus
    {
        Platform = k,
        Configured = false,
        IsValid = false,
        Message = "未配置",
        LastChecked = DateTime.Now
    };

    await SaveConfigAsync();
    Globals.StreamManager?.NotifyConfigChanged();
    return Results.Ok(new { success = true, message = $"已清空平台 '{k}' 的 Cookie", statuses = Globals.PlatformCookieStatuses });
});

// 9. 手动检测平台 Cookie 有效性
app.MapPost("/api/cookies/verify", async (HttpRequest request) =>
{
    string? platform = request.Query["platform"].ToString()?.Trim()?.ToLower();
    if (string.IsNullOrWhiteSpace(platform) || platform == "all")
    {
        await Globals.CheckAllPlatformCookiesAsync();
        return Results.Ok(new { success = true, statuses = Globals.PlatformCookieStatuses });
    }
    else
    {
        var status = await Globals.CheckPlatformCookieAsync(platform);
        return Results.Ok(new { success = true, status, statuses = Globals.PlatformCookieStatuses });
    }
});

// 10. 获取各平台 Cookie 实时健康状态
app.MapGet("/api/cookies/status", () =>
{
    return Results.Ok(Globals.PlatformCookieStatuses);
});

// 11. 设置/保存自定义局域网主机 IP 或域名
app.MapPost("/api/config/host", async (JsonNode body) =>
{
    string host = body?["customHost"]?.GetValue<string>()?.Trim() ?? "";
    lock (Globals.ConfigLock)
    {
        Globals.Config.CustomHost = host;
    }
    await SaveConfigAsync();
    return Results.Ok(new { success = true, customHost = host });
});

// 独立播放令牌不依赖网页登录会话；随机路径适配 Jellyfin/IPTV 长期远程拉取。
app.MapGet("/p/{playbackToken}/jellyfin.m3u", (string playbackToken, HttpRequest request, HttpResponse response) =>
{
    if (!IsPlaybackTokenValid(playbackToken)) return Results.NotFound();
    SetNoStoreHeaders(response);
    string effectiveBaseUrl = ResolveEffectiveBaseUrl(request);
    string playbackPrefix = $"/p/{Uri.EscapeDataString(playbackToken)}";

    var m3uContent = new StringBuilder("#EXTM3U\n");
    lock (Globals.ConfigLock)
    {
        foreach (var channel in Globals.Config.Channels)
        {
            if (!channel.Enable) continue;
            m3uContent.AppendLine($"#EXTINF:-1 tvg-name=\"{channel.Name}\" tvg-id=\"{channel.Id}\" group-title=\"{channel.Platform}\",{channel.Name}");
            m3uContent.AppendLine($"{effectiveBaseUrl}{playbackPrefix}/live/{channel.Id}/stream.m3u8");
        }
    }
    
    return Results.Content(m3uContent.ToString(), "application/x-mpegURL");
});

// 代理并动态重新签名虎牙 HLS 播放列表的端点。由本地 FFmpeg 调用，防止 wsSecret/wsTime 签名过期返回 403。
// 每次缓存过期后都从新签名的 Master Playlist 重新解析子列表，避免复用旧 CDN 路径造成域名重复拼接。
app.MapGet("/huya-source/{channelId}/stream.m3u8", async (string channelId, HttpContext ctx) =>
{
    SetNoStoreHeaders(ctx.Response);
    if (!AdminAuthService.IsLoopbackRequest(ctx))
        return Results.NotFound();

    if (Globals.Extractors.TryGetValue(channelId, out var extractor) && extractor is HuyaExtractor huyaExtractor)
    {
        string freshUrl = huyaExtractor.GetFreshUrl();
        if (string.IsNullOrEmpty(freshUrl))
        {
            Console.WriteLine($"[代理错误] 频道 {channelId} 的直播源元数据未初始化。");
            return Results.NotFound("Huya stream metadata not initialized.");
        }

        string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        try
        {
            if (Globals.M3u8Cache.TryGetValue(channelId, out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < Globals.HUYA_M3U8_CACHE_TTL_SECONDS)
            {
                return Results.Content(cached.Content, "application/vnd.apple.mpegurl");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, freshUrl);
            request.Headers.Add("User-Agent", userAgent);

            using var response = await Globals.HttpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted);
            if (!response.IsSuccessStatusCode)
            {
                return HuyaProxyFailure(channelId, $"Master Playlist 返回 HTTP {(int)response.StatusCode}");
            }

            string m3u8Content = await response.Content.ReadAsStringAsync(ctx.RequestAborted);
            Uri playlistUri = response.RequestMessage?.RequestUri ?? new Uri(freshUrl);

            // 若是 Master Playlist，透明地拉取子播放列表
            if (m3u8Content.Contains("#EXT-X-STREAM-INF"))
            {
                string? subPlaylistReference = FindFirstHlsVariantReference(m3u8Content);
                if (string.IsNullOrWhiteSpace(subPlaylistReference) ||
                    !TryResolveHttpUri(playlistUri, subPlaylistReference, out var subPlaylistUri))
                {
                    return HuyaProxyFailure(channelId, "Master Playlist 中没有有效的子播放列表");
                }

                using var subRequest = new HttpRequestMessage(HttpMethod.Get, subPlaylistUri!);
                subRequest.Headers.Add("User-Agent", userAgent);

                using var subResponse = await Globals.HttpClient.SendAsync(
                    subRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    ctx.RequestAborted);
                if (!subResponse.IsSuccessStatusCode)
                    return HuyaProxyFailure(channelId, $"子播放列表返回 HTTP {(int)subResponse.StatusCode}");

                m3u8Content = await subResponse.Content.ReadAsStringAsync(ctx.RequestAborted);
                playlistUri = subResponse.RequestMessage?.RequestUri ?? subPlaylistUri!;
            }

            if (!m3u8Content.Contains("#EXTM3U", StringComparison.Ordinal))
            {
                return HuyaProxyFailure(channelId, "上游响应不是有效的 HLS 播放列表");
            }

            string finalContent = RewriteHlsPlaylistUris(m3u8Content, playlistUri);

            Globals.M3u8Cache[channelId] = new M3u8CacheEntry
            {
                Content = finalContent,
                FetchedAt = DateTime.UtcNow
            };

            return Results.Content(finalContent, "application/vnd.apple.mpegurl");
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            return HuyaProxyFailure(channelId, $"请求异常 ({ex.GetType().Name})");
        }
    }
    Console.WriteLine($"[代理错误] 找不到频道 {channelId} 的解析器。");
    return Results.NotFound("Channel extractor not found.");
});

// 动态 m3u8 代理端点：读取 FFmpeg 生成的 stream.m3u8，剥除 #EXT-X-ENDLIST 标记
// OnDemand 模式：首次请求触发 FFmpeg 启动；后续请求更新最后访问时间，超时则自动停流
app.MapGet("/p/{playbackToken}/live/{channelId}/stream.m3u8", async (string playbackToken, string channelId, HttpContext ctx) =>
{
    SetNoStoreHeaders(ctx.Response);
    if (!IsPlaybackTokenValid(playbackToken)) return Results.NotFound();
    string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channelId, "stream.m3u8");

    // 检查频道是否存在且已启用
    ChannelConfig? channelCfg;
    string streamingMode;
    lock (Globals.ConfigLock)
    {
        channelCfg = Globals.Config.Channels.FirstOrDefault(c => c.Id == channelId);
        streamingMode = Globals.Config.StreamingMode;
    }
    if (channelCfg == null || !channelCfg.Enable)
        return Results.NotFound();

    bool isOnDemand = string.Equals(streamingMode, "OnDemand", StringComparison.OrdinalIgnoreCase);

    // 【OnDemand 核心】：更新客户端最后访问时间，触发按需启动
    if (isOnDemand)
    {
        Globals.LastClientAccessTime[channelId] = DateTime.UtcNow;
        var m = Globals.Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        m.LastClientAccess = DateTime.UtcNow;
        m.M3u8RefreshCount++;

        // 按实际会话与清单新鲜度判断，不能被停流后遗留的旧 m3u8 欺骗。
        if (Globals.StreamManager == null)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        if (!Globals.StreamManager.IsChannelSessionRunning(channelId) ||
            !IsFreshNonEmptyFile(m3u8Path, TimeSpan.FromSeconds(Globals.HLS_MANIFEST_FRESH_SECONDS)))
        {
            bool started = await Globals.StreamManager.EnsureChannelStreamingAsync(channelId, ctx.RequestAborted);
            if (!started)
            {
                ctx.Response.Headers["Retry-After"] = "5";
                return Results.StatusCode(503);
            }
        }
    }
    else
    {
        // AlwaysOn 模式下也记录访问指标
        var m = Globals.Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        m.M3u8RefreshCount++;
    }

    if (!IsFreshNonEmptyFile(m3u8Path, TimeSpan.FromSeconds(Globals.HLS_MANIFEST_FRESH_SECONDS)))
    {
        ctx.Response.Headers["Retry-After"] = "2";
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        // 用 FileShare.ReadWrite 避免与 FFmpeg 写入时的文件锁竞争
        using var fs = new FileStream(m3u8Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        string m3u8Content = sr.ReadToEnd();
        // 关键！剥除 #EXT-X-ENDLIST，这样 Jellyfin 永远不会认为直播结束
        m3u8Content = m3u8Content.Replace("#EXT-X-ENDLIST", "").TrimEnd();
        return Results.Content(m3u8Content, "application/vnd.apple.mpegurl");
    }
    catch
    {
        ctx.Response.Headers["Retry-After"] = "1";
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
});

// .ts 分片通过同一受保护路径返回；m3u8 中的相对引用会自然继承 token 路径。
app.MapGet("/p/{playbackToken}/live/{channelId}/{fileName}.ts", (string playbackToken, string channelId, string fileName, HttpContext ctx) =>
{
    SetNoStoreHeaders(ctx.Response);
    if (!IsPlaybackTokenValid(playbackToken) ||
        string.IsNullOrWhiteSpace(fileName) ||
        fileName.Contains("..", StringComparison.Ordinal) ||
        !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
    {
        return Results.NotFound();
    }

    bool channelEnabled;
    lock (Globals.ConfigLock)
        channelEnabled = Globals.Config.Channels.Any(c => c.Id == channelId && c.Enable);
    if (!channelEnabled) return Results.NotFound();

    string tsPath = Path.Combine(Globals.HLS_FULL_PATH, channelId, $"{fileName}.ts");
    if (!File.Exists(tsPath))
    {
        return Results.NotFound();
    }
    return Results.File(tsPath, "video/mp2t");
});

// GET /api/metrics - 每频道运行指标（无敏感数据）
app.MapGet("/api/metrics", () =>
{
    var channelMetrics = new List<object>();
    List<ChannelConfig> channels;
    string streamingMode;
    lock (Globals.ConfigLock)
    {
        channels = [.. Globals.Config.Channels];
        streamingMode = Globals.Config.StreamingMode;
    }
    lock (Globals.StatusLock)
    {
        foreach (var ch in channels)
        {
            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == ch.Id);
            Globals.Metrics.TryGetValue(ch.Id, out var metrics);
            Globals.LastClientAccessTime.TryGetValue(ch.Id, out var lastAccess);
            channelMetrics.Add(new
            {
                id = ch.Id,
                name = ch.Name,
                state = status?.State.ToString() ?? "Unknown",
                m3u8RefreshCount = metrics?.M3u8RefreshCount ?? 0,
                startCount = metrics?.StartCount ?? 0,
                restartCount = metrics?.RestartCount ?? 0,
                errorCount = metrics?.ErrorCount ?? 0,
                probeCount = metrics?.ProbeCount ?? 0,
                offlineProbeCount = metrics?.OfflineProbeCount ?? 0,
                lastStateChange = metrics?.LastStateChange,
                lastProbeAt = metrics?.LastProbeAt,
                lastErrorAt = metrics?.LastErrorAt,
                lastRestartReason = metrics?.LastRestartReason ?? "",
                lastClientAccess = lastAccess == default ? (DateTime?)null : lastAccess
            });
        }
    }
    return Results.Json(new
    {
        streamingMode,
        channels = channelMetrics
    });
});

// 健康检查：正常下播属于 idle；FFmpeg 缺失、明确错误或卡死状态会返回 HTTP 503。
app.MapGet("/api/health", BuildHealthResult);
app.MapGet("/api/ready", BuildHealthResult);

Globals.HttpServerStatus = $"服务已启动，管理后台：http://{Globals.LocalIp}:{Globals.HTTP_PORT}（M3U 地址请登录后复制）";

// 仅交互式终端监听回车；Docker 的 stdin=/dev/null 会立即返回 null，绝不能据此停止宿主。
if (interactiveConsole)
{
    _ = Task.Run(async () =>
    {
        string? line = Console.ReadLine();
        if (line != null)
            await app.StopAsync();
    });
}

try
{
    await app.RunAsync();
}
finally
{
    if (interactiveConsole)
    {
        try { Console.CursorVisible = true; } catch { }
        try { Console.Clear(); } catch { }
    }
    Console.WriteLine("服务器已停止。");
}

// -------------------------------------------------------------
// Helper Methods (Local Functions for Top-Level Statements)
// -------------------------------------------------------------
static async Task<bool> LoadConfigAsync()
{
    string configPath = Path.Combine(AppContext.BaseDirectory, Globals.CONFIG_FILE_NAME);
    if (!File.Exists(configPath))
    {
        string examplePath = Path.Combine(AppContext.BaseDirectory, "config.example.json");
        if (File.Exists(examplePath))
        {
            try
            {
                File.Copy(examplePath, configPath);
                Console.WriteLine($"已为您自动从模板创建 {Globals.CONFIG_FILE_NAME}，请根据需要修改配置。");
            }
            catch { }
        }
    }

    if (!File.Exists(configPath))
    {
        Console.WriteLine($"错误：未在程序目录中找到 {Globals.CONFIG_FILE_NAME}！");
        return false;
    }

    try
    {
        string jsonString = await File.ReadAllTextAsync(configPath, Encoding.UTF8);
        lock (Globals.ConfigLock)
        {
            Globals.Config = JsonSerializer.Deserialize<AppConfig>(
                jsonString,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? new AppConfig();

            RefreshChannelCookies();
        }

        lock (Globals.StatusLock)
        {
            Globals.ChannelStatuses.Clear();
            foreach (var channel in Globals.Config.Channels)
            {
                Globals.ChannelStatuses.Add(new ChannelStatus 
                { 
                    Id = channel.Id, 
                    Name = channel.Name,
                    Message = channel.Enable ? "检测中..." : "已禁用",
                    State = channel.Enable ? ChannelState.Offline : ChannelState.Disabled,
                    Color = ConsoleColor.DarkGray
                });
            }
        }

        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"读取或解析 {Globals.CONFIG_FILE_NAME} 失败: {ex.Message}");
        return false;
    }
}

static async Task<bool> SaveConfigAsync()
{
    try
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, Globals.CONFIG_FILE_NAME);
        string jsonString;
        lock (Globals.ConfigLock)
        {
            var options = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            jsonString = JsonSerializer.Serialize(Globals.Config, options);
        }

        await File.WriteAllTextAsync(configPath, jsonString, Encoding.UTF8);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                configPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[保存配置错误] {ex.Message}");
        return false;
    }
}

static void RefreshChannelCookies()
{
    if (Globals.Config.CookieProfiles == null)
        Globals.Config.CookieProfiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // 固化三家平台默认 key
    if (!Globals.Config.CookieProfiles.ContainsKey("huya")) Globals.Config.CookieProfiles["huya"] = "";
    if (!Globals.Config.CookieProfiles.ContainsKey("douyu")) Globals.Config.CookieProfiles["douyu"] = "";
    if (!Globals.Config.CookieProfiles.ContainsKey("bilibili")) Globals.Config.CookieProfiles["bilibili"] = "";

    // 过滤掉默认模板残留的占位提示文字
    Func<string?, string> cleanCookieStr = (str) =>
    {
        if (string.IsNullOrWhiteSpace(str)) return "";
        if (str.Contains("这里粘贴") || str.Contains("示例") || str.Trim().Length < 5) return "";
        return str.Trim();
    };

    if (Globals.Config.CookieProfiles.TryGetValue("huya_main", out var oldHuya) && string.IsNullOrEmpty(Globals.Config.CookieProfiles["huya"]))
        Globals.Config.CookieProfiles["huya"] = cleanCookieStr(oldHuya);
    if (Globals.Config.CookieProfiles.TryGetValue("bilibili_main", out var oldBili) && string.IsNullOrEmpty(Globals.Config.CookieProfiles["bilibili"]))
        Globals.Config.CookieProfiles["bilibili"] = cleanCookieStr(oldBili);
    if (Globals.Config.CookieProfiles.TryGetValue("douyu_main", out var oldDouyu) && string.IsNullOrEmpty(Globals.Config.CookieProfiles["douyu"]))
        Globals.Config.CookieProfiles["douyu"] = cleanCookieStr(oldDouyu);

    Globals.Config.CookieProfiles["huya"] = cleanCookieStr(Globals.Config.CookieProfiles["huya"]);
    Globals.Config.CookieProfiles["douyu"] = cleanCookieStr(Globals.Config.CookieProfiles["douyu"]);
    Globals.Config.CookieProfiles["bilibili"] = cleanCookieStr(Globals.Config.CookieProfiles["bilibili"]);

    // 移除旧格式 key 避免冗余
    Globals.Config.CookieProfiles.Remove("huya_main");
    Globals.Config.CookieProfiles.Remove("douyu_main");
    Globals.Config.CookieProfiles.Remove("bilibili_main");

    foreach (var channel in Globals.Config.Channels)
    {
        string platformKey = (channel.Platform ?? "").Trim().ToLower();
        if (string.IsNullOrEmpty(platformKey))
        {
            if (channel.Url?.Contains("huya.com") == true) platformKey = "huya";
            else if (channel.Url?.Contains("douyu.com") == true) platformKey = "douyu";
            else if (channel.Url?.Contains("bilibili.com") == true || channel.Url?.Contains("b23.tv") == true) platformKey = "bilibili";
            else platformKey = "huya";
        }

        channel.Platform = platformKey;
        channel.CookieProfileKey = platformKey;

        if (Globals.Config.CookieProfiles.TryGetValue(platformKey, out var cookieString) && !string.IsNullOrWhiteSpace(cookieString))
        {
            channel.Cookies = cookieString;
        }
        else
        {
            channel.Cookies = "";
        }
    }
}

static string ResolveEffectiveBaseUrl(HttpRequest request)
{
    // 1. 如果用户在配置中显式设置了 CustomHost (如 192.168.10.2 或 192.168.10.2:9898)
    lock (Globals.ConfigLock)
    {
        if (!string.IsNullOrWhiteSpace(Globals.Config.CustomHost))
        {
            string custom = Globals.Config.CustomHost.Trim();
            if (!custom.Contains(':'))
            {
                int port = request.Host.Port ?? Globals.HTTP_PORT;
                custom = $"{custom}:{port}";
            }
            string scheme = string.IsNullOrEmpty(request.Scheme) ? "http" : request.Scheme;
            return $"{scheme}://{custom}";
        }
    }

    // 2. 检查环境变量 HOST_IP / GATEWAY_HOST
    string? envHost = Environment.GetEnvironmentVariable("HOST_IP") ?? Environment.GetEnvironmentVariable("GATEWAY_HOST");
    if (!string.IsNullOrWhiteSpace(envHost))
    {
        string custom = envHost.Trim();
        if (!custom.Contains(':'))
        {
            int port = request.Host.Port ?? Globals.HTTP_PORT;
            custom = $"{custom}:{port}";
        }
        string scheme = string.IsNullOrEmpty(request.Scheme) ? "http" : request.Scheme;
        return $"{scheme}://{custom}";
    }

    // 3. 动态检测：如果请求来自于真实的局域网 IP / 域名 (非 localhost / 127.0.0.1 / 172.x)
    string reqHost = request.Host.Host;
    if (!string.IsNullOrEmpty(reqHost) &&
        !reqHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
        !reqHost.Equals("127.0.0.1") &&
        !reqHost.Equals("::1") &&
        !reqHost.StartsWith("172."))
    {
        string scheme = string.IsNullOrEmpty(request.Scheme) ? "http" : request.Scheme;
        return $"{scheme}://{request.Host.Value}";
    }

    // 4. 回退检查 LocalIp (如果非 127 / 172)
    if (!string.IsNullOrEmpty(Globals.LocalIp) && !Globals.LocalIp.StartsWith("127.") && !Globals.LocalIp.StartsWith("172."))
    {
        string scheme = string.IsNullOrEmpty(request.Scheme) ? "http" : request.Scheme;
        return $"{scheme}://{Globals.LocalIp}:{Globals.HTTP_PORT}";
    }

    // 5. 默认返回请求自带 Host
    string fallbackScheme = string.IsNullOrEmpty(request.Scheme) ? "http" : request.Scheme;
    string fallbackHost = request.Host.HasValue ? request.Host.Value : $"{Globals.LocalIp}:{Globals.HTTP_PORT}";
    return $"{fallbackScheme}://{fallbackHost}";
}

static string GetLocalIPAddress()
{
    string? envIp = Environment.GetEnvironmentVariable("HOST_IP") 
        ?? Environment.GetEnvironmentVariable("GATEWAY_HOST")
        ?? Environment.GetEnvironmentVariable("SERVER_IP");
    if (!string.IsNullOrWhiteSpace(envIp))
    {
        return envIp.Trim();
    }

    try
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("114.114.114.114", 65530);
        if (socket.LocalEndPoint is IPEndPoint endPoint)
        {
            return endPoint.Address.ToString();
        }
        return "127.0.0.1";
    }
    catch
    {
        return "127.0.0.1";
    }
}

static IResult BuildHealthResult()
{
    int streaming = 0, idle = 0, disabled = 0, transitional = 0, error = 0, stuck = 0;
    HashSet<string> enabledIds;
    lock (Globals.ConfigLock)
        enabledIds = Globals.Config.Channels.Where(c => c.Enable).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    lock (Globals.StatusLock)
    {
        foreach (var status in Globals.ChannelStatuses)
        {
            if (!enabledIds.Contains(status.Id) || status.State == ChannelState.Disabled)
            {
                disabled++;
                continue;
            }

            switch (status.State)
            {
                case ChannelState.Streaming:
                    streaming++;
                    break;
                case ChannelState.Ready:
                case ChannelState.Offline:
                    idle++;
                    break;
                case ChannelState.Starting:
                case ChannelState.Restarting:
                case ChannelState.Stopping:
                    transitional++;
                    if (Globals.Metrics.TryGetValue(status.Id, out var metrics) &&
                        metrics.LastStateChange.HasValue &&
                        DateTime.UtcNow - metrics.LastStateChange.Value > TimeSpan.FromSeconds(60))
                        stuck++;
                    break;
                case ChannelState.Error:
                default:
                    error++;
                    break;
            }
        }
    }

    bool ffmpegAvailable = StreamManagerService.IsFfmpegAvailable;
    bool healthy = ffmpegAvailable && error == 0 && stuck == 0;
    return Results.Json(new
    {
        version = Globals.APP_VERSION,
        status = healthy ? "healthy" : "degraded",
        ffmpegAvailable,
        streaming,
        idle,
        disabled,
        transitional,
        error,
        stuck,
        uptime = (int)(DateTime.UtcNow - Globals.StartTimeUtc).TotalSeconds
    }, statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
}

static void SetNoStoreHeaders(HttpResponse response)
{
    response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    response.Headers["Pragma"] = "no-cache";
    response.Headers["Expires"] = "0";
}

static bool IsPlaybackTokenValid(string? token)
{
    string tokenHash;
    lock (Globals.ConfigLock)
        tokenHash = Globals.Config.PlaybackTokenHash ?? "";
    return PlaybackTokenProtector.ValidateToken(token, tokenHash);
}

static bool IsFreshNonEmptyFile(string path, TimeSpan maxAge)
{
    try
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length > 0 && DateTime.UtcNow - file.LastWriteTimeUtc <= maxAge;
    }
    catch
    {
        return false;
    }
}

static string? FindFirstHlsVariantReference(string playlist)
{
    bool expectingVariant = false;
    foreach (string rawLine in playlist.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
    {
        string line = rawLine.Trim().TrimStart('\uFEFF');
        if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            expectingVariant = true;
            continue;
        }

        if (expectingVariant && line.Length > 0 && !line.StartsWith('#'))
            return line;
    }

    return null;
}

static bool TryResolveHttpUri(Uri baseUri, string reference, out Uri? resolvedUri)
{
    if (Uri.TryCreate(baseUri, reference, out var candidate) &&
        (candidate.Scheme == Uri.UriSchemeHttp || candidate.Scheme == Uri.UriSchemeHttps))
    {
        resolvedUri = candidate;
        return true;
    }

    resolvedUri = null;
    return false;
}

static string RewriteHlsPlaylistUris(string playlist, Uri playlistUri)
{
    var output = new StringBuilder();
    foreach (string rawLine in playlist.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
    {
        string line = rawLine.Trim().TrimStart('\uFEFF');
        if (line.Equals("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            continue;

        if (line.Length > 0 && !line.StartsWith('#'))
        {
            output.AppendLine(TryResolveHttpUri(playlistUri, line, out var resolved)
                ? resolved!.AbsoluteUri
                : line);
            continue;
        }

        const string uriMarker = "URI=\"";
        int markerIndex = line.IndexOf(uriMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            int valueStart = markerIndex + uriMarker.Length;
            int valueEnd = line.IndexOf('"', valueStart);
            if (valueEnd > valueStart)
            {
                string reference = line[valueStart..valueEnd];
                if (TryResolveHttpUri(playlistUri, reference, out var resolved))
                    line = string.Concat(line.AsSpan(0, valueStart), resolved!.AbsoluteUri, line.AsSpan(valueEnd));
            }
        }

        output.AppendLine(line);
    }

    return output.ToString();
}

static IResult HuyaProxyFailure(string channelId, string reason)
{
    Console.WriteLine($"[代理错误] 频道 {channelId}: {reason}");
    if (Globals.M3u8Cache.TryGetValue(channelId, out var cached))
    {
        double ageSeconds = (DateTime.UtcNow - cached.FetchedAt).TotalSeconds;
        if (ageSeconds <= Globals.HUYA_STALE_CACHE_MAX_SECONDS)
        {
            Console.WriteLine($"[代理降级] 频道 {channelId} 使用 {ageSeconds:F1} 秒前的播放列表");
            return Results.Content(cached.Content, "application/vnd.apple.mpegurl");
        }
    }

    return Results.StatusCode(StatusCodes.Status502BadGateway);
}

static void ShowError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n{message}");
    Console.ResetColor();
    if (!Console.IsInputRedirected)
    {
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}

// -------------------------------------------------------------
// Models
// -------------------------------------------------------------
/// <summary>频道流推状态机枚举</summary>
public enum ChannelState
{
    /// <summary>频道已禁用</summary>
    Disabled,
    /// <summary>未开播：主播离线</summary>
    Offline,
    /// <summary>已开播：主播在线，待机中（无客户端连接，零媒体流量消耗）</summary>
    Ready,
    /// <summary>启动中：客户端请求连接，正在启动 FFmpeg</summary>
    Starting,
    /// <summary>推流中：客户端正在观看，FFmpeg 正常推流</summary>
    Streaming,
    /// <summary>正在停止 FFmpeg</summary>
    Stopping,
    /// <summary>正在重连恢复</summary>
    Restarting,
    /// <summary>异常状态（Cookie失效或无法解析）</summary>
    Error
}

public class AppConfig
{
    public string CustomHost { get; set; } = "";
    /// <summary>管理员密码的 PBKDF2-SHA256 自包含哈希；永不通过管理 API 返回。</summary>
    public string AdminPasswordHash { get; set; } = "";
    /// <summary>独立播放令牌的 SHA-256 摘要，用于固定时间校验公开播放路径。</summary>
    public string PlaybackTokenHash { get; set; } = "";
    /// <summary>播放令牌的 AES-256-GCM 密文，仅用于登录后恢复订阅地址；原文不落盘。</summary>
    public string PlaybackTokenEncrypted { get; set; } = "";
    /// <summary>推流模式：AlwaysOn（始终推流，默认）或 OnDemand（按需推流，无人观看时停止 FFmpeg）</summary>
    public string StreamingMode { get; set; } = "AlwaysOn";
    /// <summary>OnDemand 模式：最后一次 m3u8 请求后多久无新请求则停止 FFmpeg（秒），默认 300 秒</summary>
    public int IdleTimeoutSeconds { get; set; } = 300;
    /// <summary>OnDemand 模式：启动 FFmpeg 后等待 m3u8 生成的超时时间（秒），默认 30 秒</summary>
    public int StartupTimeoutSeconds { get; set; } = 30;
    /// <summary>OnDemand 模式：应用启动时预热的频道 ID 列表（提前启动 FFmpeg）</summary>
    public List<string> PrewarmEnabledChannels { get; set; } = [];
    public List<ChannelConfig> Channels { get; set; } = [];
    public Dictionary<string, string> CookieProfiles { get; set; } = [];
}

public class ChannelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Platform { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string Cookies { get; set; } = string.Empty;
    public string? CookieProfileKey { get; set; }
    public bool Enable { get; set; } = true;
}

public class ChannelStatus
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = "检测中...";
    public ConsoleColor Color { get; set; } = ConsoleColor.DarkGray;
    public int RetryCount { get; set; } = 0;
    // 状态机相关
    public ChannelState State { get; set; } = ChannelState.Offline;
}

public class CookieProfileRequest
{
    public string Key { get; set; } = string.Empty;
    public string? Cookie { get; set; }
}

// -------------------------------------------------------------
// Globals & Constants
// -------------------------------------------------------------
public class M3u8CacheEntry
{
    public string Content { get; set; } = "";
    public DateTime FetchedAt { get; set; }
}

/// <summary>每频道的运行指标（无敏感数据）</summary>
public class ChannelMetrics
{
    public long M3u8RefreshCount { get; set; } = 0;
    public int StartCount { get; set; } = 0;
    public int RestartCount { get; set; } = 0;
    public int ErrorCount { get; set; } = 0;
    public long ProbeCount { get; set; } = 0;
    public long OfflineProbeCount { get; set; } = 0;
    public DateTime? LastStateChange { get; set; }
    public DateTime? LastClientAccess { get; set; }
    public DateTime? LastProbeAt { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public string LastRestartReason { get; set; } = "";
}

public static class Globals
{
    public const string APP_VERSION = "v1.5.6";
    public const int HTTP_PORT = 9898;
    public const string HLS_DIR = "hls_stream";
    public const int HLS_MANIFEST_FRESH_SECONDS = 30;
    public const int HLS_SEGMENT_RETENTION_SECONDS = 120;
    public const double HUYA_M3U8_CACHE_TTL_SECONDS = 1.5;
    public const double HUYA_STALE_CACHE_MAX_SECONDS = 6;
    public static readonly string HLS_FULL_PATH = Path.Combine(AppContext.BaseDirectory, HLS_DIR);
    public const string CONFIG_FILE_NAME = "config.json";
    
    public static readonly DateTime StartTimeUtc = DateTime.UtcNow;
    public static AppConfig Config = new();
    public static readonly object ConfigLock = new();
    public static List<ChannelStatus> ChannelStatuses = [];
    public static readonly object StatusLock = new();
    public static string HttpServerStatus = "HTTP 服务器正在启动...";
    public static string LocalIp = "127.0.0.1";
    public static readonly ConcurrentDictionary<string, BaseExtractor> Extractors = new();

    // 【资源优化】使用共享的长连接 HttpClient，配置连接生命周期和空闲超时
    public static readonly HttpClient HttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 6,
        ConnectTimeout = TimeSpan.FromSeconds(8)
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public static readonly ConcurrentDictionary<string, M3u8CacheEntry> M3u8Cache = new();
    public static readonly ConcurrentDictionary<string, PlatformCookieStatus> PlatformCookieStatuses = new(StringComparer.OrdinalIgnoreCase);

    // 【OnDemand】每个频道最后一次 m3u8 请求时间，用于判断是否进入空闲
    public static readonly ConcurrentDictionary<string, DateTime> LastClientAccessTime = new();

    // 每频道运行指标
    public static readonly ConcurrentDictionary<string, ChannelMetrics> Metrics = new();

    public static StreamManagerService? StreamManager;

    public static void UpdateStatus(string channelId, string message, ConsoleColor color, bool incrementRetry = false)
    {
        lock (StatusLock)
        {
            var status = ChannelStatuses.FirstOrDefault(c => c.Id == channelId);
            if (status != null)
            {
                status.Message = message;
                status.Color = color;
                if (incrementRetry) status.RetryCount++;
            }
        }
    }

    public static void UpdateState(string channelId, ChannelState newState)
    {
        bool changed = false;
        lock (StatusLock)
        {
            var status = ChannelStatuses.FirstOrDefault(c => c.Id == channelId);
            if (status != null && status.State != newState)
            {
                status.State = newState;
                changed = true;
            }
        }
        if (changed)
        {
            var metrics = Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
            lock (metrics) metrics.LastStateChange = DateTime.UtcNow;
        }
    }

    public static void RecordProbe(string channelId, bool offline = false)
    {
        var metrics = Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        lock (metrics)
        {
            metrics.ProbeCount++;
            if (offline) metrics.OfflineProbeCount++;
            metrics.LastProbeAt = DateTime.UtcNow;
        }
    }

    public static void RecordError(string channelId)
    {
        var metrics = Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        lock (metrics)
        {
            metrics.ErrorCount++;
            metrics.LastErrorAt = DateTime.UtcNow;
        }
    }

    public static void RecordRestart(string channelId, string reason)
    {
        var metrics = Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        lock (metrics)
        {
            metrics.RestartCount++;
            metrics.LastRestartReason = reason;
        }
    }

    public static ChannelState GetState(string channelId)
    {
        lock (StatusLock)
        {
            return ChannelStatuses.FirstOrDefault(c => c.Id == channelId)?.State ?? ChannelState.Offline;
        }
    }

    public static async Task<PlatformCookieStatus> CheckPlatformCookieAsync(string platform)
    {
        string p = (platform ?? "").Trim().ToLower();
        string cookie = "";
        lock (ConfigLock)
        {
            if (Config.CookieProfiles.TryGetValue(p, out var c))
                cookie = c;
        }

        var previousStatus = PlatformCookieStatuses.TryGetValue(p, out var oldSt) ? oldSt : null;
        var newStatus = await CookieVerifier.VerifyAsync(p, cookie);

        // 【关键防误判机制】：网络/SSL等非认证错误绝不能改变已授权凭据的有效状态
        if (newStatus.Configured && newStatus.IsNetworkError)
        {
            if (previousStatus != null && previousStatus.Configured && previousStatus.IsValid)
            {
                // 维持上一次已授权有效状态与用户名
                newStatus.IsValid = true;
                newStatus.Username = previousStatus.Username;
                newStatus.Message = string.IsNullOrWhiteSpace(previousStatus.Username)
                    ? "已授权有效 (网络波动，保持状态)"
                    : $"{previousStatus.Message} (网络检测波动)";
            }
            else
            {
                // 首次若遇网络异常，默认视作已配置有效状态，不误判为过期
                newStatus.IsValid = true;
                newStatus.Message = "已配置 (网络波动，待下次复判)";
            }
        }

        PlatformCookieStatuses[p] = newStatus;
        return newStatus;
    }

    public static async Task CheckAllPlatformCookiesAsync()
    {
        await CheckPlatformCookieAsync("huya");
        await CheckPlatformCookieAsync("douyu");
        await CheckPlatformCookieAsync("bilibili");
    }
}

// -------------------------------------------------------------
// Background Services
// -------------------------------------------------------------

public class RenderService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine("[Console] 检测到重定向输出，已禁用每秒仪表盘重绘；请使用 Web 管理端和结构化生命周期日志。");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) { }
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try 
            {
                int safeWidth = 100;
                int safeHeight = 20;
                try { 
                    safeWidth = Math.Max(1, Console.WindowWidth - 1); 
                    safeHeight = Math.Max(10, Console.WindowHeight - 1);
                } catch { }

                try 
                {
                    Console.SetCursorPosition(0, 0);
                    Console.ForegroundColor = ConsoleColor.White;

                    Console.WriteLine("此窗口必须保持打开。按 [回车键] 可随时停止服务器...".PadRight(safeWidth));
                    Console.WriteLine(Globals.HttpServerStatus.PadRight(safeWidth));
                    Console.WriteLine(new string('=', safeWidth));
                    Console.WriteLine("频道状态 (管理后台: http://localhost:9898)：".PadRight(safeWidth));
                    Console.WriteLine(new string('-', safeWidth));

                    string timeText = $"最后刷新时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}";
                    Console.WriteLine(timeText.PadRight(safeWidth));
                    Console.WriteLine(new string('-', safeWidth));

                    int lineIndex = 7;
                    lock (Globals.StatusLock)
                    {
                        foreach (var status in Globals.ChannelStatuses)
                        {
                            if (lineIndex >= safeHeight) break;
                            Console.SetCursorPosition(0, lineIndex++);
                            Console.ForegroundColor = status.Color;

                            string cleanMessage = status.Message.Replace("\r", "").Replace("\n", " | ");
                            string msg = $"[{status.Name}] {cleanMessage}";
                            if (status.RetryCount > 0) msg += $"  (重试 {status.RetryCount} 次)";

                            Console.Write(msg.PadRight(safeWidth));
                        }

                        for (; lineIndex < safeHeight; lineIndex++)
                        {
                            Console.SetCursorPosition(0, lineIndex);
                            Console.Write(new string(' ', safeWidth));
                        }
                    }

                    Console.ResetColor();
                } catch (IOException) { }
            }
            catch { }

            try
            {
                await Task.Delay(1000, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }
}

public class StreamManagerService : BackgroundService
{
    private const int HEALTH_CHECK_SECONDS = 10;
    private const int STALE_THRESHOLD_SECONDS = 30;
    private static readonly string? FFMPEG_EXE_PATH = ResolveFfmpegPath();
    public static bool IsFfmpegAvailable => !string.IsNullOrEmpty(FFMPEG_EXE_PATH) && File.Exists(FFMPEG_EXE_PATH);

    // 每个频道对应一个 StreamingSession（包含 Process + 日志资源，可靠释放）
    private readonly ConcurrentDictionary<string, StreamingSession> _sessions = new();

    // 【OnDemand 防启动风暴】每个频道一把启动锁，保证并发首次请求只创建一个 FFmpeg
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startupLocks = new();

    // 【OnDemand 主播在线探测记录】控制待机频道探测频率，避免频繁请求上游
    private readonly ConcurrentDictionary<string, DateTime> _lastProbeTimes = new();

    // 触发器：API 更新配置时立即唤醒巡检
    private readonly SemaphoreSlim _triggerSemaphore = new(0, 1);
    private DateTime _lastCookieCheckTime = DateTime.MinValue;

    public StreamManagerService()
    {
        Globals.StreamManager = this;
    }

    private static bool IsOfflineResult(string? error) =>
        error?.Contains("未开播", StringComparison.OrdinalIgnoreCase) == true ||
        error?.Contains("Not Live", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsAuthenticationError(string? error) =>
        error?.Contains("Cookie", StringComparison.OrdinalIgnoreCase) == true ||
        error?.Contains("登录", StringComparison.OrdinalIgnoreCase) == true;

    private static string SafeLogValue(string? value)
    {
        string safe = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return safe.Length <= 180 ? safe : safe[..180];
    }

    private static void LogLifecycle(string channelId, string eventName, string details) =>
        Console.WriteLine($"[StreamLifecycle] channel={SafeLogValue(channelId)} event={SafeLogValue(eventName)} {SafeLogValue(details)}");

    public void NotifyConfigChanged()
    {
        try { _triggerSemaphore.Release(); } catch { }
    }

    /// <summary>频道是否有仍在运行的 FFmpeg 会话。</summary>
    public bool IsChannelSessionRunning(string channelId)
    {
        try
        {
            return _sessions.TryGetValue(channelId, out var session) && !session.Process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>手动重启频道（API 调用）</summary>
    public async Task RestartChannelAsync(string channelId)
    {
        var startLock = _startupLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));
        await startLock.WaitAsync();
        try
        {
            ChannelConfig? channel;
            lock (Globals.ConfigLock)
                channel = Globals.Config.Channels.FirstOrDefault(c => c.Id == channelId && c.Enable);

            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channelId);
            if (channel == null || status == null)
                return;

            Globals.RecordRestart(channelId, "manual");
            LogLifecycle(channelId, "restart", "reason=manual");
            Globals.UpdateState(channelId, ChannelState.Restarting);
            Globals.UpdateStatus(channelId, "手动重启中...", ConsoleColor.Yellow);
            await StopSessionAsync(channelId);
            Globals.Extractors.TryRemove(channelId, out _);
            Globals.M3u8Cache.TryRemove(channelId, out _);
            await StartSingleChannelCoreAsync(channel, status, CancellationToken.None);
        }
        finally
        {
            startLock.Release();
            NotifyConfigChanged();
        }
    }

    /// <summary>停止并释放单个频道的 FFmpeg 会话（线程安全，正确释放所有资源）</summary>
    public async Task StopSessionAsync(string channelId)
    {
        if (_sessions.TryRemove(channelId, out var session))
        {
            await session.DisposeAsync();
        }

        // 会话停止后立即撤下旧清单；否则 OnDemand 的下一位客户端会播放停流前的时间轴。
        RemovePublishedPlaylist(channelId);
        CleanupOldHlsArtifacts(Path.Combine(Globals.HLS_FULL_PATH, channelId));
    }

    /// <summary>删除频道时调用：停止会话并清理目录和缓存</summary>
    public async Task StopAndCleanChannelAsync(string channelId)
    {
        await StopSessionAsync(channelId);
        string dir = Path.Combine(Globals.HLS_FULL_PATH, channelId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Globals.Extractors.TryRemove(channelId, out _);
        Globals.M3u8Cache.TryRemove(channelId, out _);
        Globals.LastClientAccessTime.TryRemove(channelId, out _);
        _lastProbeTimes.TryRemove(channelId, out _);
        _startupLocks.TryRemove(channelId, out _);
    }

    private static void RemovePublishedPlaylist(string channelId)
    {
        string channelDir = Path.Combine(Globals.HLS_FULL_PATH, channelId);
        try
        {
            if (!Directory.Exists(channelDir)) return;
            foreach (string file in Directory.GetFiles(channelDir, "stream.m3u8*", SearchOption.TopDirectoryOnly))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private static void CleanupOldHlsArtifacts(string channelDir)
    {
        try
        {
            if (!Directory.Exists(channelDir)) return;

            var referencedSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string playlistPath = Path.Combine(channelDir, "stream.m3u8");
            if (File.Exists(playlistPath))
            {
                try
                {
                    using var fs = new FileStream(playlistPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(fs, Encoding.UTF8);
                    while (reader.ReadLine() is { } rawLine)
                    {
                        string line = rawLine.Trim();
                        if (line.Length == 0 || line.StartsWith('#')) continue;
                        string pathOnly = line.Split('?', 2)[0];
                        string fileName = Path.GetFileName(pathOnly.Replace('/', Path.DirectorySeparatorChar));
                        if (fileName.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                            referencedSegments.Add(fileName);
                    }
                }
                catch { }
            }

            DateTime cutoff = DateTime.UtcNow.AddSeconds(-Globals.HLS_SEGMENT_RETENTION_SECONDS);
            foreach (string file in Directory.GetFiles(channelDir, "*.ts", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!referencedSegments.Contains(Path.GetFileName(file)) && File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }

            foreach (string file in Directory.GetFiles(channelDir, "*.tmp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch { }
            }
        }
        catch { }
    }

    private static string? ResolveFfmpegPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string localPath = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(localPath)) return localPath;

        string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string fullPath = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(fullPath)) return fullPath;
            }
            catch { }
        }
        return null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(FFMPEG_EXE_PATH) || !File.Exists(FFMPEG_EXE_PATH))
        {
            string err = "错误：未找到 FFmpeg！请将 ffmpeg.exe 放入程序同目录，或安装 FFmpeg 并加入系统 PATH 环境变量。";
            if (Globals.ChannelStatuses.Count > 0)
                Globals.UpdateStatus(Globals.ChannelStatuses.First().Id, err, ConsoleColor.Red);
            Console.WriteLine($"\n[FFmpeg缺失] {err}\n");
            return;
        }

        // OnDemand 模式：预热指定频道
        string initMode;
        lock (Globals.ConfigLock) { initMode = Globals.Config.StreamingMode; }
        if (string.Equals(initMode, "OnDemand", StringComparison.OrdinalIgnoreCase))
        {
            List<string> prewarm;
            lock (Globals.ConfigLock) { prewarm = [.. Globals.Config.PrewarmEnabledChannels]; }
            foreach (var pwId in prewarm)
            {
                List<ChannelConfig> channels;
                lock (Globals.ConfigLock) { channels = [.. Globals.Config.Channels]; }
                var ch = channels.FirstOrDefault(c => c.Id == pwId && c.Enable);
                if (ch != null)
                {
                    var st = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == pwId);
                    if (st != null) await StartSingleChannelAsync(ch, st, stoppingToken);
                }
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            // 定期后台巡检 Cookie（每 30 分钟，启动时立即触发）
            if ((DateTime.UtcNow - _lastCookieCheckTime).TotalMinutes >= 30)
            {
                _lastCookieCheckTime = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    try { await Globals.CheckAllPlatformCookiesAsync(); }
                    catch (Exception ex) { Console.WriteLine($"[Cookie巡检异常] {ex.Message}"); }
                }, stoppingToken);
            }

            List<ChannelConfig> currentChannels;
            string streamingMode;
            lock (Globals.ConfigLock)
            {
                currentChannels = [.. Globals.Config.Channels];
                streamingMode = Globals.Config.StreamingMode;
            }

            var currentChannelIds = currentChannels.Select(c => c.Id).ToHashSet();
            bool isOnDemand = string.Equals(streamingMode, "OnDemand", StringComparison.OrdinalIgnoreCase);

            // ---- 清理已被删除频道的会话 ----
            foreach (var runningId in _sessions.Keys.ToArray())
            {
                if (!currentChannelIds.Contains(runningId))
                    await StopAndCleanChannelAsync(runningId);
            }

            // ---- 同步 ChannelStatuses 列表 ----
            lock (Globals.StatusLock)
            {
                Globals.ChannelStatuses.RemoveAll(s => !currentChannelIds.Contains(s.Id));
                foreach (var ch in currentChannels)
                {
                    if (!Globals.ChannelStatuses.Any(s => s.Id == ch.Id))
                        Globals.ChannelStatuses.Add(new ChannelStatus { Id = ch.Id, Name = ch.Name });
                }
            }

            int idleTimeoutSeconds;
            lock (Globals.ConfigLock) { idleTimeoutSeconds = Globals.Config.IdleTimeoutSeconds; }

            // ---- 遍历每个频道执行状态机逻辑 ----
            foreach (var channel in currentChannels)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channel.Id);
                if (status == null) continue;

                CleanupOldHlsArtifacts(Path.Combine(Globals.HLS_FULL_PATH, channel.Id));

                bool hasSession = _sessions.TryGetValue(channel.Id, out var session);

                // -- 频道已禁用 --
                if (!channel.Enable)
                {
                    if (hasSession)
                    {
                        Globals.UpdateStatus(channel.Id, "已禁用，正在停止...", ConsoleColor.DarkGray);
                        await StopSessionAsync(channel.Id);
                    }
                    else
                    {
                        Globals.UpdateStatus(channel.Id, "已禁用", ConsoleColor.DarkGray);
                        Globals.UpdateState(channel.Id, ChannelState.Disabled);
                    }
                    continue;
                }

                // -- OnDemand 模式：检查是否超过空闲超时 --
                if (isOnDemand && hasSession)
                {
                    bool hasRecentClient = false;
                    if (Globals.LastClientAccessTime.TryGetValue(channel.Id, out var lastAccess))
                        hasRecentClient = (DateTime.UtcNow - lastAccess).TotalSeconds < idleTimeoutSeconds;

                    if (!hasRecentClient)
                    {
                        Globals.UpdateStatus(channel.Id, "已开播", ConsoleColor.Cyan);
                        Globals.UpdateState(channel.Id, ChannelState.Ready);
                        await StopSessionAsync(channel.Id);
                        hasSession = false;
                        session = null;
                        continue;
                    }
                }

                // -- OnDemand 模式且无推流会话：轻量探测主播开播状态（已开播/未开播）--
                if (isOnDemand && !hasSession)
                {
                    var currentState = Globals.GetState(channel.Id);
                    if (currentState != ChannelState.Starting && currentState != ChannelState.Restarting)
                    {
                        bool needProbe = !_lastProbeTimes.TryGetValue(channel.Id, out var lastProbe) ||
                                         (DateTime.UtcNow - lastProbe).TotalSeconds >= 30;

                        if (needProbe)
                        {
                            _lastProbeTimes[channel.Id] = DateTime.UtcNow;
                            var (probeUrl, probeErr) = await GetSourceStreamUrlAsync(channel, stoppingToken);
                            bool isOfflineResult = IsOfflineResult(probeErr);
                            Globals.RecordProbe(channel.Id, isOfflineResult);
                            if (!string.IsNullOrEmpty(probeUrl))
                            {
                                Globals.UpdateState(channel.Id, ChannelState.Ready);
                                Globals.UpdateStatus(channel.Id, "已开播", ConsoleColor.Cyan);
                                status.RetryCount = 0;
                            }
                            else if (isOfflineResult)
                            {
                                Globals.UpdateState(channel.Id, ChannelState.Offline);
                                Globals.UpdateStatus(channel.Id, "未开播", ConsoleColor.DarkYellow);
                                status.RetryCount = 0;
                            }
                            else if (IsAuthenticationError(probeErr))
                            {
                                Globals.RecordError(channel.Id);
                                Globals.UpdateState(channel.Id, ChannelState.Error);
                                Globals.UpdateStatus(channel.Id, "Cookie失效或需登录", ConsoleColor.Red);
                            }
                            else
                            {
                                Globals.RecordError(channel.Id);
                                Globals.UpdateState(channel.Id, ChannelState.Offline);
                                Globals.UpdateStatus(channel.Id, string.IsNullOrEmpty(probeErr) ? "未开播" : probeErr, ConsoleColor.DarkYellow);
                            }
                        }
                    }
                    continue;
                }

                // -- AlwaysOn 模式：没有会话则需要启动 --
                if (!isOnDemand && !hasSession)
                {
                    var currentState = Globals.GetState(channel.Id);
                    var metrics = Globals.Metrics.GetOrAdd(channel.Id, _ => new ChannelMetrics());
                    // 自愈保护：若因异常卡在 Starting/Restarting 超过 30 秒，强制解除并尝试重新拉起
                    bool isStuckStarting = (currentState == ChannelState.Starting || currentState == ChannelState.Restarting) &&
                                           metrics.LastStateChange.HasValue &&
                                           (DateTime.UtcNow - metrics.LastStateChange.Value).TotalSeconds > 30;

                    bool probeThrottled = (currentState == ChannelState.Offline || currentState == ChannelState.Error) &&
                                          _lastProbeTimes.TryGetValue(channel.Id, out var lastProbe) &&
                                          (DateTime.UtcNow - lastProbe).TotalSeconds < 30;

                    if (isStuckStarting)
                    {
                        Globals.RecordRestart(channel.Id, "state-stuck");
                        LogLifecycle(channel.Id, "restart", $"reason=state-stuck state={currentState}");
                    }

                    if (!probeThrottled &&
                        (currentState != ChannelState.Starting && currentState != ChannelState.Restarting || isStuckStarting))
                    {
                        _lastProbeTimes[channel.Id] = DateTime.UtcNow;
                        await StartSingleChannelAsync(channel, status, stoppingToken);
                    }
                    continue;
                }

                // -- 检查现有会话健康状态 --
                if (hasSession && session != null)
                {
                    if (session.Process.HasExited)
                    {
                        int? exitCode = null;
                        try { exitCode = session.Process.ExitCode; } catch { }
                        Globals.RecordRestart(channel.Id, "ffmpeg-exited");
                        Globals.RecordError(channel.Id);
                        LogLifecycle(channel.Id, "restart", $"reason=ffmpeg-exited exitCode={exitCode?.ToString() ?? "unknown"}");
                        Globals.UpdateStatus(channel.Id, "FFmpeg 进程已退出，正在重启...", ConsoleColor.Yellow);
                        Globals.UpdateState(channel.Id, ChannelState.Restarting);
                        await StopSessionAsync(channel.Id);
                        await StartSingleChannelAsync(channel, status, stoppingToken);
                    }
                    else
                    {
                        string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channel.Id, "stream.m3u8");
                        bool m3u8Exists = File.Exists(m3u8Path) && new FileInfo(m3u8Path).Length > 0;

                        if (m3u8Exists)
                        {
                            var lastWrite = File.GetLastWriteTimeUtc(m3u8Path);
                            if ((DateTime.UtcNow - lastWrite).TotalSeconds > STALE_THRESHOLD_SECONDS)
                            {
                                double manifestAgeSeconds = (DateTime.UtcNow - lastWrite).TotalSeconds;
                                Globals.RecordRestart(channel.Id, "manifest-stale");
                                Globals.RecordError(channel.Id);
                                LogLifecycle(channel.Id, "restart", $"reason=manifest-stale ageSeconds={manifestAgeSeconds:F1}");
                                Globals.UpdateStatus(channel.Id, "HLS 文件陈旧（推流卡死），正在强制重启...", ConsoleColor.Red);
                                Globals.UpdateState(channel.Id, ChannelState.Restarting);
                                await StopSessionAsync(channel.Id);
                                await StartSingleChannelAsync(channel, status, stoppingToken);
                            }
                            else
                            {
                                Globals.UpdateStatus(channel.Id, "推流中", ConsoleColor.Green);
                                Globals.UpdateState(channel.Id, ChannelState.Streaming);
                                status.RetryCount = 0;
                            }
                        }
                        else
                        {
                            // stream.m3u8 尚未生成或为空：检查是否启动超时
                            int startupTimeout = Globals.Config.StartupTimeoutSeconds > 0 ? Globals.Config.StartupTimeoutSeconds : 30;
                            if ((DateTime.UtcNow - session.CreatedAtUtc).TotalSeconds > startupTimeout)
                            {
                                double sessionAgeSeconds = (DateTime.UtcNow - session.CreatedAtUtc).TotalSeconds;
                                LogLifecycle(channel.Id, "startup-timeout", $"ageSeconds={sessionAgeSeconds:F1} timeoutSeconds={startupTimeout}");

                                // 终止卡死/无响应的 FFmpeg 会话
                                await StopSessionAsync(channel.Id);

                                // 探测上游直播源是否已下播或鉴权失效
                                var (probeUrl, probeErr) = await GetSourceStreamUrlAsync(channel, stoppingToken);
                                bool isOfflineResult = IsOfflineResult(probeErr);
                                Globals.RecordProbe(channel.Id, isOfflineResult);
                                if (string.IsNullOrEmpty(probeUrl))
                                {
                                    if (isOfflineResult)
                                    {
                                        Globals.UpdateStatus(channel.Id, "未开播", ConsoleColor.DarkYellow);
                                        Globals.UpdateState(channel.Id, ChannelState.Offline);
                                        status.RetryCount = 0;
                                    }
                                    else if (IsAuthenticationError(probeErr))
                                    {
                                        Globals.RecordError(channel.Id);
                                        Globals.UpdateStatus(channel.Id, "Cookie失效或需登录", ConsoleColor.Red);
                                        Globals.UpdateState(channel.Id, ChannelState.Error);
                                    }
                                    else
                                    {
                                        Globals.RecordError(channel.Id);
                                        Globals.UpdateStatus(channel.Id, $"获取源失败: {probeErr}", ConsoleColor.Red);
                                        Globals.UpdateState(channel.Id, ChannelState.Error);
                                    }
                                }
                                else
                                {
                                    // 上游在线但推流进程启动超时，触发重启重试
                                    Globals.RecordError(channel.Id);
                                    Globals.RecordRestart(channel.Id, "startup-timeout");
                                    LogLifecycle(channel.Id, "restart", "reason=startup-timeout upstream=online");
                                    Globals.UpdateStatus(channel.Id, "推流启动超时，正在重试...", ConsoleColor.Yellow);
                                    Globals.UpdateState(channel.Id, ChannelState.Restarting);
                                    await StartSingleChannelAsync(channel, status, stoppingToken);
                                }
                            }
                        }
                    }
                }
            }

            // 等待下一个巡检周期，或由 API 立即唤醒
            try
            {
                await _triggerSemaphore.WaitAsync(TimeSpan.FromSeconds(HEALTH_CHECK_SECONDS), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }

        // 关闭时释放所有会话
        await StopAllSessionsAsync();
    }

    /// <summary>
    /// OnDemand 核心：客户端首次访问触发 FFmpeg 按需启动。
    /// 使用 per-channel 锁防止并发请求产生多个 FFmpeg（启动风暴）。
    /// </summary>
    public async Task<bool> EnsureChannelStreamingAsync(string channelId, CancellationToken requestCancelled)
    {
        string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channelId, "stream.m3u8");

        // 快速路径必须同时满足进程存活和清单新鲜；字典中残留的退出进程不能算成功。
        if (IsChannelSessionRunning(channelId) &&
            IsManifestFresh(m3u8Path))
        {
            MarkChannelStreaming(channelId);
            return true;
        }

        // 获取或创建本频道的启动锁
        var startLock = _startupLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));

        int configuredStartupTimeout;
        lock (Globals.ConfigLock)
            configuredStartupTimeout = Globals.Config.StartupTimeoutSeconds;
        int startupTimeout = configuredStartupTimeout > 0 ? configuredStartupTimeout : 30;

        // 等待同频道正在进行的启动完成。
        bool lockAcquired;
        try { lockAcquired = await startLock.WaitAsync(TimeSpan.FromSeconds(startupTimeout + 5), requestCancelled); }
        catch (OperationCanceledException) { return false; }

        if (!lockAcquired) return false;

        try
        {
            List<ChannelConfig> channels;
            lock (Globals.ConfigLock)
            {
                channels = [.. Globals.Config.Channels];
            }

            var channel = channels.FirstOrDefault(c => c.Id == channelId && c.Enable);
            if (channel == null) return false;

            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channelId);
            if (status == null) return false;

            if (_sessions.TryGetValue(channelId, out var existingSession))
            {
                bool processRunning;
                try { processRunning = !existingSession.Process.HasExited; }
                catch { processRunning = false; }

                if (processRunning)
                {
                    if (IsManifestFresh(m3u8Path))
                    {
                        MarkChannelStreaming(channelId);
                        return true;
                    }

                    double remainingStartupSeconds = startupTimeout -
                        (DateTime.UtcNow - existingSession.CreatedAtUtc).TotalSeconds;
                    if (remainingStartupSeconds > 0 &&
                        await WaitForFreshManifestAsync(channelId, m3u8Path, remainingStartupSeconds, requestCancelled))
                    {
                        MarkChannelStreaming(channelId);
                        return true;
                    }

                    if (requestCancelled.IsCancellationRequested) return false;
                }

                // 退出进程或长时间不再更新清单：先完整释放，再启动新时间轴。
                await StopSessionAsync(channelId);
            }

            Globals.UpdateState(channelId, ChannelState.Starting);
            Globals.UpdateStatus(channelId, "客户端请求触发启动...", ConsoleColor.Cyan);

            await StartSingleChannelCoreAsync(channel, status, requestCancelled);

            if (!IsChannelSessionRunning(channelId)) return false;

            bool manifestReady = await WaitForFreshManifestAsync(channelId, m3u8Path, startupTimeout, requestCancelled);
            if (manifestReady) MarkChannelStreaming(channelId);
            return manifestReady;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            startLock.Release();
        }
    }

    private static void MarkChannelStreaming(string channelId)
    {
        if (Globals.GetState(channelId) != ChannelState.Streaming)
            Globals.UpdateState(channelId, ChannelState.Streaming);
        Globals.UpdateStatus(channelId, "推流中", ConsoleColor.Green);
        lock (Globals.StatusLock)
        {
            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channelId);
            if (status != null) status.RetryCount = 0;
        }
    }

    private async Task<bool> WaitForFreshManifestAsync(
        string channelId,
        string m3u8Path,
        double timeoutSeconds,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(0.5, timeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (IsManifestFresh(m3u8Path)) return true;
            if (!IsChannelSessionRunning(channelId)) return false;
            await Task.Delay(250, cancellationToken);
        }

        return IsManifestFresh(m3u8Path);
    }

    private static bool IsManifestFresh(string m3u8Path)
    {
        try
        {
            var file = new FileInfo(m3u8Path);
            return file.Exists && file.Length > 0 &&
                DateTime.UtcNow - file.LastWriteTimeUtc <= TimeSpan.FromSeconds(Globals.HLS_MANIFEST_FRESH_SECONDS);
        }
        catch
        {
            return false;
        }
    }

    private async Task StartSingleChannelAsync(ChannelConfig channel, ChannelStatus status, CancellationToken ct)
    {
        var startLock = _startupLocks.GetOrAdd(channel.Id, _ => new SemaphoreSlim(1, 1));
        try
        {
            await startLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (IsChannelSessionRunning(channel.Id)) return;
            if (_sessions.ContainsKey(channel.Id))
                await StopSessionAsync(channel.Id);
            await StartSingleChannelCoreAsync(channel, status, ct);
        }
        finally
        {
            startLock.Release();
        }
    }

    private async Task StartSingleChannelCoreAsync(ChannelConfig channel, ChannelStatus status, CancellationToken ct)
    {
        string channelHlsDir = Path.Combine(Globals.HLS_FULL_PATH, channel.Id);
        try
        {
            Directory.CreateDirectory(channelHlsDir);
            RemovePublishedPlaylist(channel.Id);
            CleanupOldHlsArtifacts(channelHlsDir);
            Globals.M3u8Cache.TryRemove(channel.Id, out _);
        }
        catch (Exception ex)
        {
            Globals.RecordError(channel.Id);
            LogLifecycle(channel.Id, "start-failed", "reason=hls-directory");
            Globals.UpdateStatus(channel.Id, $"无法清理目录: {ex.Message}", ConsoleColor.Red);
            Globals.UpdateState(channel.Id, ChannelState.Error);
            return;
        }

        Globals.UpdateStatus(channel.Id, "正在获取直播源...", ConsoleColor.DarkGray);

        var (sourceStreamUrl, error) = await GetSourceStreamUrlAsync(channel, ct);
        bool isOfflineResult = IsOfflineResult(error);
        Globals.RecordProbe(channel.Id, isOfflineResult);

        if (string.IsNullOrEmpty(sourceStreamUrl))
        {
            string errorMsg;
            ConsoleColor color;
            bool retryInc = true;

            if (isOfflineResult)
            {
                errorMsg = "未开播";
                color = ConsoleColor.DarkYellow;
                retryInc = false;
                status.RetryCount = 0;
                Globals.UpdateState(channel.Id, ChannelState.Offline);
            }
            else if (IsAuthenticationError(error))
            {
                errorMsg = "Cookie失效或需登录";
                color = ConsoleColor.Red;
                Globals.UpdateState(channel.Id, ChannelState.Error);
            }
            else
            {
                errorMsg = $"获取失败: {error}";
                color = ConsoleColor.Red;
                Globals.UpdateState(channel.Id, ChannelState.Error);
            }

            Globals.UpdateStatus(channel.Id, errorMsg, color, incrementRetry: retryInc);
            if (!isOfflineResult)
            {
                Globals.RecordError(channel.Id);
                LogLifecycle(channel.Id, "source-unavailable", $"category={(IsAuthenticationError(error) ? "authentication" : "extractor")}");
            }
            return;
        }

        Globals.UpdateStatus(channel.Id, "成功获取源，正在启动 FFmpeg...", ConsoleColor.Cyan);

        string inputUrl = sourceStreamUrl;
        if (channel.Platform?.ToLower() == "huya")
            inputUrl = $"http://127.0.0.1:{Globals.HTTP_PORT}/huya-source/{channel.Id}/stream.m3u8";

        var newSession = CreateSession(inputUrl, channelHlsDir, channel.Platform ?? "");
        if (newSession != null)
        {
            _sessions[channel.Id] = newSession;
            Globals.UpdateState(channel.Id, ChannelState.Starting);
            Globals.UpdateStatus(channel.Id, "已启动推流", ConsoleColor.Green);
            var metrics = Globals.Metrics.GetOrAdd(channel.Id, _ => new ChannelMetrics());
            lock (metrics) metrics.StartCount++;
            LogLifecycle(channel.Id, "started", $"pid={newSession.Process.Id}");
        }
        else
        {
            Globals.RecordError(channel.Id);
            LogLifecycle(channel.Id, "start-failed", "reason=ffmpeg-process");
            Globals.UpdateStatus(channel.Id, "FFmpeg 进程启动失败", ConsoleColor.Red);
            Globals.UpdateState(channel.Id, ChannelState.Error);
        }
    }

    private static async Task<(string? Url, string? Error)> GetSourceStreamUrlAsync(ChannelConfig channel, CancellationToken ct)
    {
        try
        {
            if (!Globals.Extractors.TryGetValue(channel.Id, out var extractor))
            {
                extractor = channel.Platform?.ToLower() switch
                {
                    "bilibili" => new BilibiliExtractor(channel.Cookies),
                    "huya"     => new HuyaExtractor(channel.Cookies),
                    "douyu"    => new DouyuExtractor(channel.Cookies),
                    _          => throw new Exception($"未知的平台: {channel.Platform}")
                };
                Globals.Extractors[channel.Id] = extractor;
            }

            string? url = await extractor.GetStreamUrlAsync(channel.Url ?? "", channel.Quality ?? "OD");
            return !string.IsNullOrEmpty(url)
                ? (url, null)
                : (null, "未获取到直播流地址 (可能未开播或需要Cookie)");
        }
        catch (OperationCanceledException)
        {
            return (null, "操作已取消");
        }
        catch (Exception ex)
        {
            return (null, $"解析失败: {ex.Message}");
        }
    }

    private static StreamingSession? CreateSession(string sourceStreamUrl, string channelHlsDir, string platform)
    {
        string m3u8Path = Path.Combine(channelHlsDir, "stream.m3u8");
        string segmentPath = Path.Combine(channelHlsDir, "segment_%010d.ts");
        string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        string referer = (platform ?? "").ToLower() switch
        {
            "bilibili" => "https://live.bilibili.com/",
            "huya"     => "https://www.huya.com/",
            "douyu"    => "https://www.douyu.com/",
            _          => "https://www.huya.com/"
        };

        var psi = new ProcessStartInfo
        {
            FileName = FFMPEG_EXE_PATH,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        // ArgumentList 逐项传参，避免 URL、路径或 HTTP 头中的引号/空格被二次解析。
        // FFmpeg 的 -headers 要求以真实 CRLF 结尾；字面量 "\\r\\n" 会产生启动警告。
        string[] arguments =
        [
            "-hide_banner", "-nostats", "-loglevel", "warning",
            "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err",
            "-reconnect", "1", "-reconnect_streamed", "1",
            "-reconnect_delay_max", "10", "-reconnect_on_network_error", "1",
            "-rw_timeout", "15000000",
            "-headers", $"Referer: {referer}\r\n",
            "-user_agent", userAgent,
            "-i", sourceStreamUrl,
            "-c:v", "copy", "-c:a", "copy", "-sn",
            "-f", "hls", "-hls_time", "3", "-hls_list_size", "15",
            "-hls_allow_cache", "0", "-hls_delete_threshold", "10",
            "-hls_start_number_source", "epoch",
            "-hls_segment_filename", segmentPath,
            "-hls_flags", "delete_segments+temp_file+discont_start+program_date_time",
            m3u8Path
        ];
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        try
        {
            var process = Process.Start(psi);
            if (process == null) return null;
            string logFilePath = Path.Combine(channelHlsDir, "ffmpeg.log");
            return new StreamingSession(process, logFilePath);
        }
        catch { return null; }
    }

    private async Task StopAllSessionsAsync()
    {
        var ids = _sessions.Keys.ToArray();
        foreach (var id in ids)
            await StopSessionAsync(id);
    }
}

/// <summary>
/// 封装单次 FFmpeg 推流会话的所有资源，确保可靠释放。
/// 解决了旧版本中 StreamWriter / SemaphoreSlim 每次重启后无法释放的泄漏问题。
/// </summary>
public sealed class StreamingSession : IAsyncDisposable
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    public Process Process { get; }
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;
    private readonly StreamWriter _logWriter;
    private readonly SemaphoreSlim _logSemaphore;
    private readonly CancellationTokenSource _cts;
    private readonly Task _stderrTask;
    private readonly Task _stdoutTask;
    private long _loggedBytes;
    private bool _limitNoticeWritten;

    public StreamingSession(Process process, string logFilePath)
    {
        Process = process;
        var logStream = new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _logWriter = new StreamWriter(logStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _logSemaphore = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();
        _stderrTask = Task.Run(() => DrainReaderAsync(process.StandardError, _cts.Token));
        _stdoutTask = Task.Run(() => DrainReaderAsync(process.StandardOutput, _cts.Token));
    }

    private async Task DrainReaderAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                await _logSemaphore.WaitAsync(ct);
                try
                {
                    long lineBytes = Encoding.UTF8.GetByteCount(line) + 1L;
                    if (_loggedBytes + lineBytes <= MaxLogBytes)
                    {
                        await _logWriter.WriteLineAsync(line.AsMemory(), ct);
                        _loggedBytes += lineBytes;
                    }
                    else if (!_limitNoticeWritten)
                    {
                        const string notice = "[LiveStreamGateway] FFmpeg 日志达到 2 MiB 上限，后续输出已丢弃。";
                        await _logWriter.WriteLineAsync(notice.AsMemory(), ct);
                        _limitNoticeWritten = true;
                    }
                }
                finally { _logSemaphore.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try
            {
                await _logSemaphore.WaitAsync(CancellationToken.None);
                try { await _logWriter.FlushAsync(CancellationToken.None); } finally { _logSemaphore.Release(); }
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 1. 取消日志任务
        try { _cts.Cancel(); } catch { }

        // 2. 终止整个进程树并等待退出
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
        }
        catch { }

        // 3. 等待两个日志任务真正完成（确保 StreamWriter 不再被写入）
        try { await Task.WhenAll(_stderrTask, _stdoutTask); } catch { }

        // 4. 释放所有资源
        try { _cts.Dispose(); } catch { }
        try { _logSemaphore.Dispose(); } catch { }
        try { await _logWriter.DisposeAsync(); } catch { }
        try { Process.Dispose(); } catch { }
    }
}
