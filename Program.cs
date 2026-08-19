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
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using LiveStreamGateway;

// -------------------------------------------------------------
// Top-Level Application Setup
// -------------------------------------------------------------
try {
    Console.Title = "LiveStreamGateway - .NET 10";
    Console.CursorVisible = false;
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

// Configure Kestrel to listen on the specified port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(Globals.HTTP_PORT);
});

// Add Hosted Services for HealthCheck and UI Render
builder.Services.AddHostedService<RenderService>();
builder.Services.AddHostedService<StreamManagerService>();

var app = builder.Build();

// Enable default files (index.html) and static files from wwwroot (Web UI)
app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure HLS directory exists for static files provider
if (!Directory.Exists(Globals.HLS_FULL_PATH))
{
    Directory.CreateDirectory(Globals.HLS_FULL_PATH);
}

// Serve Static Files (HLS stream segments)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(Globals.HLS_FULL_PATH),
    RequestPath = "/stream",
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/vnd.apple.mpegurl",
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
        ctx.Context.Response.Headers.Append("Expires", "0");
        
        if (ctx.File.Name.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.ContentType = "application/vnd.apple.mpegurl";
        }
        else if (ctx.File.Name.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.ContentType = "video/mp2t";
        }
    }
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
            bool isStreaming = File.Exists(m3u8Path) && channel.Enable && 
                (DateTime.Now - File.GetLastWriteTime(m3u8Path)).TotalSeconds <= 90;

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
                hlsUrl = $"/live/{channel.Id}/stream.m3u8",
                fullHlsUrl = $"{effectiveBaseUrl}/live/{channel.Id}/stream.m3u8"
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
        m3uUrl = $"{effectiveBaseUrl}/jellyfin.m3u",
        uptimeSeconds = (int)uptime.TotalSeconds,
        uptimeText = $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分 {uptime.Seconds}秒",
        activeStreams = activeCount,
        totalChannels = Globals.Config.Channels.Count,
        channels = channelStatusList,
        cookieStatuses = Globals.PlatformCookieStatuses
    });
});

// 2. 获取配置 (Channels + CookieProfiles + CookieStatuses + CustomHost)
app.MapGet("/api/config", () =>
{
    lock (Globals.ConfigLock)
    {
        return Results.Json(new
        {
            customHost = Globals.Config.CustomHost ?? "",
            channels = Globals.Config.Channels,
            cookieProfiles = Globals.Config.CookieProfiles,
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

    return Results.Ok(new { success = true, key, cookie = req.Cookie ?? "", status, statuses = Globals.PlatformCookieStatuses });
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

// Master Playlist Endpoint - 指向动态代理而非静态文件
app.MapGet("/jellyfin.m3u", (HttpRequest request) =>
{
    string effectiveBaseUrl = ResolveEffectiveBaseUrl(request);

    var m3uContent = new StringBuilder("#EXTM3U\n");
    lock (Globals.ConfigLock)
    {
        foreach (var channel in Globals.Config.Channels)
        {
            if (!channel.Enable) continue;
            m3uContent.AppendLine($"#EXTINF:-1 tvg-name=\"{channel.Name}\" tvg-id=\"{channel.Id}\" group-title=\"{channel.Platform}\",{channel.Name}");
            m3uContent.AppendLine($"{effectiveBaseUrl}/live/{channel.Id}/stream.m3u8");
        }
    }
    
    return Results.Content(m3uContent.ToString(), "application/x-mpegURL");
});

// 代理并动态重新签名虎牙 HLS 播放列表的端点。由本地 FFmpeg 调用，防止 wsSecret/wsTime 签名过期返回 403
// 优化：缓存上一次成功的响应 1.5 秒，消除 FFmpeg 每次刷新都对虎牙 CDN 发起双重 HTTP 请求的开销
app.MapGet("/huya-source/{channelId}/stream.m3u8", async (string channelId) =>
{
    if (Globals.Extractors.TryGetValue(channelId, out var extractor) && extractor is HuyaExtractor huyaExtractor)
    {
        string freshUrl = huyaExtractor.GetFreshUrl();
        if (string.IsNullOrEmpty(freshUrl))
        {
            Console.WriteLine($"[代理错误] 频道 {channelId} 的直播源元数据未初始化。");
            return Results.NotFound("Huya stream metadata not initialized.");
        }

        const double CACHE_TTL_SECONDS = 1.5;
        string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        try
        {
            // --- 优化：如果缓存未过期，直接返回上次结果 ---
            if (Globals.M3u8Cache.TryGetValue(channelId, out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < CACHE_TTL_SECONDS)
            {
                return Results.Content(cached.Content, "application/vnd.apple.mpegurl");
            }

            // --- 缓存失效，向虎牙 CDN 请求 ---
            // 【防卡顿优化】如果已有缓存的子播放列表 URL 模板，直接基于 freshUrl 构造子播放列表 URL
            // 避免每次都先请求 Master Playlist 再解析 Sub（2 次公网 RTT → 1 次）
            string? resolvedSubUrl = null;
            string targetUrl = freshUrl;
            
            if (Globals.M3u8Cache.TryGetValue(channelId, out var existingCache) &&
                !string.IsNullOrEmpty(existingCache.ResolvedSubPlaylistUrl))
            {
                // 子播放列表 URL 的路径段（如 /tx.hls/xxx/stream.m3u8）与主列表共享同一个域名和签名参数
                // freshUrl 已由 GetFreshUrl() 生成最新签名，我们取其 base 拼接已知的子路径
                try
                {
                    var freshUri = new Uri(freshUrl);
                    var subUri = new Uri(existingCache.ResolvedSubPlaylistUrl);
                    // 使用 fresh 的 scheme+host + cached sub 的 path + fresh 的 query（签名参数）
                    targetUrl = $"{freshUri.Scheme}://{freshUri.Host}{subUri.AbsolutePath}?{freshUri.Query.TrimStart('?')}";
                }
                catch
                {
                    targetUrl = freshUrl; // URI 解析失败时回退到主列表
                }
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            request.Headers.Add("User-Agent", userAgent);
            
            using var response = await Globals.HttpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[代理错误] 虎牙服务器返回 HTTP {(int)response.StatusCode}，URL: {freshUrl}");
                // 如果有旧缓存，降级返回旧内容而非直接报错，避免 FFmpeg 因偶发 CDN 错误中断
                if (Globals.M3u8Cache.TryGetValue(channelId, out var staleCache))
                {
                    Console.WriteLine($"[代理降级] 使用过期缓存内容（{channelId}）");
                    return Results.Content(staleCache.Content, "application/vnd.apple.mpegurl");
                }
                return Results.StatusCode((int)response.StatusCode);
            }

            string m3u8Content = await response.Content.ReadAsStringAsync();
            int lastSlashIndex = freshUrl.LastIndexOf('/');
            string baseUrl = freshUrl.Substring(0, lastSlashIndex);

            // 若是 Master Playlist，透明地拉取子播放列表
            if (m3u8Content.Contains("#EXT-X-STREAM-INF"))
            {
                var masterLines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                string subPlaylistUrl = "";
                for (int i = 0; i < masterLines.Length; i++)
                {
                    if (masterLines[i].StartsWith("#EXT-X-STREAM-INF") && i + 1 < masterLines.Length)
                    {
                        subPlaylistUrl = masterLines[i + 1].Trim();
                        break;
                    }
                }

                if (!string.IsNullOrEmpty(subPlaylistUrl))
                {
                    if (!subPlaylistUrl.StartsWith("http"))
                        subPlaylistUrl = $"{baseUrl}/{subPlaylistUrl}";

                    resolvedSubUrl = subPlaylistUrl;

                    using var subRequest = new HttpRequestMessage(HttpMethod.Get, subPlaylistUrl);
                    subRequest.Headers.Add("User-Agent", userAgent);
                    
                    using var subResponse = await Globals.HttpClient.SendAsync(subRequest);
                    if (subResponse.IsSuccessStatusCode)
                    {
                        m3u8Content = await subResponse.Content.ReadAsStringAsync();
                        freshUrl = subPlaylistUrl;
                        lastSlashIndex = freshUrl.LastIndexOf('/');
                        baseUrl = freshUrl.Substring(0, lastSlashIndex);
                    }
                    else
                    {
                        Console.WriteLine($"[代理错误] 获取子播放列表失败: {subPlaylistUrl}, HTTP {(int)subResponse.StatusCode}");
                    }
                }
            }

            // 重写 ts 路径为绝对 URL
            var lines = m3u8Content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var rewritten = new StringBuilder();
            foreach (var line in lines)
            {
                var cleanLine = line.Trim();
                if (cleanLine.Contains(".ts?") || (cleanLine.EndsWith(".ts") && !cleanLine.StartsWith("http")))
                {
                    rewritten.AppendLine(cleanLine.StartsWith("http") ? cleanLine : $"{baseUrl}/{cleanLine}");
                }
                else
                {
                    rewritten.AppendLine(cleanLine);
                }
            }

            string finalContent = rewritten.ToString();

            // 写入缓存
            Globals.M3u8Cache[channelId] = new M3u8CacheEntry
            {
                Content = finalContent,
                ResolvedSubPlaylistUrl = resolvedSubUrl,
                FetchedAt = DateTime.UtcNow
            };

            return Results.Content(finalContent, "application/vnd.apple.mpegurl");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[代理异常] 请求虎牙时发生异常: {ex.Message}");
            // 异常时也尝试降级返回旧缓存
            if (Globals.M3u8Cache.TryGetValue(channelId, out var staleCache))
                return Results.Content(staleCache.Content, "application/vnd.apple.mpegurl");
            return Results.Problem(ex.Message);
        }
    }
    Console.WriteLine($"[代理错误] 找不到频道 {channelId} 的解析器。");
    return Results.NotFound("Channel extractor not found.");
});

// 动态 m3u8 代理端点：读取 FFmpeg 生成的 stream.m3u8，剥除 #EXT-X-ENDLIST 标记
// OnDemand 模式：首次请求触发 FFmpeg 启动；后续请求更新最后访问时间，超时则自动停流
app.MapGet("/live/{channelId}/stream.m3u8", async (string channelId, HttpContext ctx) =>
{
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

        // 如果 m3u8 还不存在，说明 FFmpeg 还没启动，触发按需启动
        if (!File.Exists(m3u8Path) || new FileInfo(m3u8Path).Length == 0)
        {
            if (Globals.StreamManager == null)
                return Results.NotFound();

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

    if (!File.Exists(m3u8Path))
        return Results.NotFound();

    try
    {
        // 用 FileShare.ReadWrite 避免与 FFmpeg 写入时的文件锁竞争
        using var fs = new FileStream(m3u8Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        string m3u8Content = sr.ReadToEnd();
        // 关键！剥除 #EXT-X-ENDLIST，这样 Jellyfin 永远不会认为直播结束
        m3u8Content = m3u8Content.Replace("#EXT-X-ENDLIST", "").TrimEnd();
        // 【防卡顿关键】禁止所有中间层（浏览器/Jellyfin代理/Nginx/CDN）缓存 m3u8 播放列表
        ctx.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        ctx.Response.Headers["Pragma"] = "no-cache";
        ctx.Response.Headers["Expires"] = "0";
        return Results.Content(m3u8Content, "application/vnd.apple.mpegurl");
    }
    catch
    {
        return Results.NotFound();
    }
});

// .ts 分片也需要通过 /live/ 路径返回（因为 m3u8 中的路径是相对的）
app.MapGet("/live/{channelId}/{fileName}.ts", (string channelId, string fileName) =>
{
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
                lastStateChange = metrics?.LastStateChange,
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

// GET /api/health - 健康检查（区分 Web API 正常 / 频道待机 / 推流中 / FFmpeg 缺失）
app.MapGet("/api/health", () =>
{
    int streaming = 0, idle = 0, disabled = 0, error = 0;
    lock (Globals.StatusLock)
    {
        foreach (var s in Globals.ChannelStatuses)
        {
            switch (s.State)
            {
                case ChannelState.Streaming: streaming++; break;
                case ChannelState.Idle: idle++; break;
                case ChannelState.Disabled: disabled++; break;
                default: error++; break;
            }
        }
    }
    return Results.Json(new
    {
        version = Globals.APP_VERSION,
        status = "healthy",
        streaming,
        idle,
        disabled,
        error,
        uptime = (int)(DateTime.UtcNow - Globals.StartTimeUtc).TotalSeconds
    });
});

Globals.HttpServerStatus = $"服务已启动，播放列表地址：http://{Globals.LocalIp}:{Globals.HTTP_PORT}/jellyfin.m3u";

// We want to handle graceful exit on enter key
_ = Task.Run(async () =>
{
    Console.ReadLine();
    await app.StopAsync();
});

try
{
    await app.RunAsync();
}
finally
{
    try { Console.CursorVisible = true; } catch { }
    try { Console.Clear(); } catch { }
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
                Globals.ChannelStatuses.Add(new ChannelStatus { Id = channel.Id, Name = channel.Name });
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

static void ShowError(string message)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"\n{message}");
    Console.ResetColor();
    Console.WriteLine("按任意键退出...");
    Console.ReadKey();
}

// -------------------------------------------------------------
// Models
// -------------------------------------------------------------
/// <summary>频道流推状态机枚举</summary>
public enum ChannelState
{
    /// <summary>频道已禁用</summary>
    Disabled,
    /// <summary>待机：FFmpeg 未运行，等待客户端触发（OnDemand）或等待巡检启动（AlwaysOn）</summary>
    Idle,
    /// <summary>正在启动 FFmpeg，等待 m3u8 生成</summary>
    Starting,
    /// <summary>推流中，FFmpeg 正常运行</summary>
    Streaming,
    /// <summary>正在停止 FFmpeg（OnDemand 空闲超时或手动停止）</summary>
    Stopping,
    /// <summary>正在重连（FFmpeg 崩溃或陈旧，正在重建会话）</summary>
    Restarting
}

public class AppConfig
{
    public string CustomHost { get; set; } = "";
    /// <summary>推流模式：AlwaysOn（始终推流）或 OnDemand（按需推流，无人观看时停止 FFmpeg）。默认 OnDemand</summary>
    public string StreamingMode { get; set; } = "OnDemand";
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
    public string Message { get; set; } = "正在初始化...";
    public ConsoleColor Color { get; set; } = ConsoleColor.Gray;
    public int RetryCount { get; set; } = 0;
    // 状态机相关
    public ChannelState State { get; set; } = ChannelState.Idle;
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
    public string? ResolvedSubPlaylistUrl { get; set; }
    public DateTime FetchedAt { get; set; }
}

/// <summary>每频道的运行指标（无敏感数据）</summary>
public class ChannelMetrics
{
    public long M3u8RefreshCount { get; set; } = 0;
    public int StartCount { get; set; } = 0;
    public int RestartCount { get; set; } = 0;
    public int ErrorCount { get; set; } = 0;
    public DateTime? LastStateChange { get; set; }
    public DateTime? LastClientAccess { get; set; }
}

public static class Globals
{
    public const string APP_VERSION = "v1.5.0";
    public const int HTTP_PORT = 9898;
    public const string HLS_DIR = "hls_stream";
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
        lock (StatusLock)
        {
            var status = ChannelStatuses.FirstOrDefault(c => c.Id == channelId);
            if (status != null) status.State = newState;
        }
        var metrics = Metrics.GetOrAdd(channelId, _ => new ChannelMetrics());
        metrics.LastStateChange = DateTime.UtcNow;
    }

    public static ChannelState GetState(string channelId)
    {
        lock (StatusLock)
        {
            return ChannelStatuses.FirstOrDefault(c => c.Id == channelId)?.State ?? ChannelState.Idle;
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

    // 每个频道对应一个 StreamingSession（包含 Process + 日志资源，可靠释放）
    private readonly ConcurrentDictionary<string, StreamingSession> _sessions = new();

    // 【OnDemand 防启动风暴】每个频道一把启动锁，保证并发首次请求只创建一个 FFmpeg
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _startupLocks = new();

    // 触发器：API 更新配置时立即唤醒巡检
    private readonly SemaphoreSlim _triggerSemaphore = new(0, 1);
    private DateTime _lastCookieCheckTime = DateTime.MinValue;

    public StreamManagerService()
    {
        Globals.StreamManager = this;
    }

    public void NotifyConfigChanged()
    {
        try { _triggerSemaphore.Release(); } catch { }
    }

    /// <summary>手动重启频道（API 调用）</summary>
    public async Task RestartChannelAsync(string channelId)
    {
        Globals.UpdateState(channelId, ChannelState.Restarting);
        Globals.UpdateStatus(channelId, "手动重启中...", ConsoleColor.Yellow);
        await StopSessionAsync(channelId);
        NotifyConfigChanged();
    }

    /// <summary>停止并释放单个频道的 FFmpeg 会话（线程安全，正确释放所有资源）</summary>
    public async Task StopSessionAsync(string channelId)
    {
        if (_sessions.TryRemove(channelId, out var session))
        {
            await session.DisposeAsync();
            Globals.UpdateState(channelId, ChannelState.Idle);
        }
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
                        Globals.UpdateStatus(channel.Id, $"待机中（{idleTimeoutSeconds}s 无观看，已停流）", ConsoleColor.DarkYellow);
                        await StopSessionAsync(channel.Id);
                        hasSession = false;
                        session = null;
                        continue;
                    }
                }

                // -- AlwaysOn 模式：没有会话则需要启动 --
                if (!isOnDemand && !hasSession)
                {
                    var currentState = Globals.GetState(channel.Id);
                    if (currentState != ChannelState.Starting && currentState != ChannelState.Restarting)
                        await StartSingleChannelAsync(channel, status, stoppingToken);
                    continue;
                }

                // -- 检查现有会话健康状态 --
                if (hasSession && session != null)
                {
                    if (session.Process.HasExited)
                    {
                        var metrics = Globals.Metrics.GetOrAdd(channel.Id, _ => new ChannelMetrics());
                        metrics.RestartCount++;
                        Globals.UpdateStatus(channel.Id, "FFmpeg 进程已退出，正在重启...", ConsoleColor.Yellow);
                        Globals.UpdateState(channel.Id, ChannelState.Restarting);
                        await StopSessionAsync(channel.Id);
                        await StartSingleChannelAsync(channel, status, stoppingToken);
                    }
                    else
                    {
                        string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channel.Id, "stream.m3u8");
                        if (File.Exists(m3u8Path))
                        {
                            var lastWrite = File.GetLastWriteTimeUtc(m3u8Path);
                            if ((DateTime.UtcNow - lastWrite).TotalSeconds > STALE_THRESHOLD_SECONDS)
                            {
                                var metrics = Globals.Metrics.GetOrAdd(channel.Id, _ => new ChannelMetrics());
                                metrics.RestartCount++;
                                Globals.UpdateStatus(channel.Id, "HLS 文件陈旧（僵尸进程），正在强制重启...", ConsoleColor.Red);
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
        // 快速路径：已在推流
        if (_sessions.ContainsKey(channelId)) return true;

        // 获取或创建本频道的启动锁
        var startLock = _startupLocks.GetOrAdd(channelId, _ => new SemaphoreSlim(1, 1));

        // 等待锁（最多 35 秒）
        bool lockAcquired;
        try { lockAcquired = await startLock.WaitAsync(TimeSpan.FromSeconds(35), requestCancelled); }
        catch (OperationCanceledException) { return false; }

        if (!lockAcquired) return false;

        try
        {
            // 双重检查
            if (_sessions.ContainsKey(channelId)) return true;

            List<ChannelConfig> channels;
            int startupTimeout;
            lock (Globals.ConfigLock)
            {
                channels = [.. Globals.Config.Channels];
                startupTimeout = Globals.Config.StartupTimeoutSeconds;
            }

            var channel = channels.FirstOrDefault(c => c.Id == channelId && c.Enable);
            if (channel == null) return false;

            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channelId);
            if (status == null) return false;

            Globals.UpdateState(channelId, ChannelState.Starting);
            Globals.UpdateStatus(channelId, "客户端请求触发启动...", ConsoleColor.Cyan);

            await StartSingleChannelAsync(channel, status, requestCancelled);

            if (!_sessions.ContainsKey(channelId)) return false;

            // 等待 m3u8 生成（最多 startupTimeout 秒）
            string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channelId, "stream.m3u8");
            var deadline = DateTime.UtcNow.AddSeconds(startupTimeout);
            while (DateTime.UtcNow < deadline)
            {
                if (requestCancelled.IsCancellationRequested) return false;
                if (File.Exists(m3u8Path) && new FileInfo(m3u8Path).Length > 0)
                    return true;
                await Task.Delay(500, requestCancelled);
            }
            return File.Exists(m3u8Path) && new FileInfo(m3u8Path).Length > 0;
        }
        catch (OperationCanceledException) { return false; }
        finally
        {
            startLock.Release();
        }
    }

    private async Task StartSingleChannelAsync(ChannelConfig channel, ChannelStatus status, CancellationToken ct)
    {
        string channelHlsDir = Path.Combine(Globals.HLS_FULL_PATH, channel.Id);
        try
        {
            // 只删除旧的 m3u8 播放列表，保留已有 TS 分片避免播放端 404
            if (Directory.Exists(channelHlsDir) && !_sessions.ContainsKey(channel.Id))
            {
                foreach (var file in Directory.GetFiles(channelHlsDir, "*.m3u8*"))
                    try { File.Delete(file); } catch { }
            }
            Directory.CreateDirectory(channelHlsDir);
        }
        catch (Exception ex)
        {
            Globals.UpdateStatus(channel.Id, $"无法清理目录: {ex.Message}", ConsoleColor.Red);
            Globals.UpdateState(channel.Id, ChannelState.Idle);
            return;
        }

        Globals.UpdateStatus(channel.Id, "正在获取直播源...", ConsoleColor.DarkGray);

        var (sourceStreamUrl, error) = await GetSourceStreamUrlAsync(channel, ct);

        if (string.IsNullOrEmpty(sourceStreamUrl))
        {
            string errorMsg;
            ConsoleColor color;
            bool retryInc = true;

            if (error?.Contains("未开播") == true || error?.Contains("Not Live") == true)
            {
                errorMsg = "未开播";
                color = ConsoleColor.DarkYellow;
                retryInc = false;
                status.RetryCount = 0;
            }
            else if (error?.Contains("Cookie") == true || error?.Contains("登录") == true)
            {
                errorMsg = "Cookie失效或需登录";
                color = ConsoleColor.Red;
            }
            else
            {
                errorMsg = $"获取失败: {error}";
                color = ConsoleColor.Red;
            }

            Globals.UpdateStatus(channel.Id, errorMsg, color, incrementRetry: retryInc);
            Globals.UpdateState(channel.Id, ChannelState.Idle);
            var me = Globals.Metrics.GetOrAdd(channel.Id, _ => new ChannelMetrics());
            me.ErrorCount++;
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
            metrics.StartCount++;
        }
        else
        {
            Globals.UpdateStatus(channel.Id, "FFmpeg 进程启动失败", ConsoleColor.Red);
            Globals.UpdateState(channel.Id, ChannelState.Idle);
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
        string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        string referer = (platform ?? "").ToLower() switch
        {
            "bilibili" => "https://live.bilibili.com/",
            "huya"     => "https://www.huya.com/",
            "douyu"    => "https://www.douyu.com/",
            _          => "https://www.huya.com/"
        };

        string arguments = $"-fflags +genpts+discardcorrupt -err_detect ignore_err "
            + $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 10 -reconnect_on_network_error 1 "
            + $"-rw_timeout 15000000 "
            + $"-headers \"Referer: {referer}\\r\\n\" "
            + $"-user_agent \"{userAgent}\" "
            + $"-i \"{sourceStreamUrl}\" "
            + $"-c:v copy -c:a copy -sn -f hls -hls_time 3 -hls_list_size 15 -hls_allow_cache 0 "
            + $"-hls_delete_threshold 10 "
            + $"-hls_flags delete_segments+temp_file "
            + $"\"{m3u8Path}\"";

        var psi = new ProcessStartInfo
        {
            FileName = FFMPEG_EXE_PATH,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

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
    public Process Process { get; }
    private readonly StreamWriter _logWriter;
    private readonly SemaphoreSlim _logSemaphore;
    private readonly CancellationTokenSource _cts;
    private readonly Task _stderrTask;
    private readonly Task _stdoutTask;

    public StreamingSession(Process process, string logFilePath)
    {
        Process = process;
        _logWriter = new StreamWriter(logFilePath, append: true, Encoding.UTF8) { AutoFlush = false };
        _logSemaphore = new SemaphoreSlim(1, 1);
        _cts = new CancellationTokenSource();
        _stderrTask = Task.Run(() => DrainReaderAsync(process.StandardError, _logWriter, _logSemaphore, _cts.Token));
        _stdoutTask = Task.Run(() => DrainReaderAsync(process.StandardOutput, _logWriter, _logSemaphore, _cts.Token));
    }

    private static async Task DrainReaderAsync(StreamReader reader, StreamWriter writer, SemaphoreSlim semaphore, CancellationToken ct)
    {
        try
        {
            int lineCount = 0;
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                await semaphore.WaitAsync(ct);
                try
                {
                    await writer.WriteLineAsync(line.AsMemory(), ct);
                    if (++lineCount % 20 == 0) await writer.FlushAsync(ct);
                }
                finally { semaphore.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            try
            {
                await semaphore.WaitAsync(CancellationToken.None);
                try { await writer.FlushAsync(CancellationToken.None); } finally { semaphore.Release(); }
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
