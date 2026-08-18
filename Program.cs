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
        Globals.StreamManager?.StopChannel(id);
        
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
app.MapPost("/api/channels/{id}/restart", (string id) =>
{
    var channel = Globals.Config.Channels.FirstOrDefault(c => c.Id == id);
    if (channel == null)
        return Results.NotFound(new { error = "未找到指定频道" });

    Globals.Extractors.TryRemove(id, out _);
    Globals.M3u8Cache.TryRemove(id, out _);
    Globals.StreamManager?.RestartChannel(id);

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
            // --- 确定最终要拉取的 Media Playlist URL ---
            // 优化1：缓存已解析的子播放列表 URL，跳过主列表拉取
            string targetUrl = freshUrl;
            if (Globals.M3u8Cache.TryGetValue(channelId, out var existingCache) &&
                !string.IsNullOrEmpty(existingCache.ResolvedSubPlaylistUrl))
            {
                // 已有缓存的子播放列表 URL，直接用（签名由 GetFreshUrl 每次重新生成）
                // 注意：子播放列表 URL 本身也含签名，此处不能直接复用，仍需向主列表请求一次
                // 但我们可以跳过主列表的解析逻辑，只有在子 URL 失效时才回退
                targetUrl = freshUrl; // 下方会直接尝试子 URL
            }

            // --- 优化2：如果缓存未过期，直接返回上次结果 ---
            if (Globals.M3u8Cache.TryGetValue(channelId, out var cached) &&
                (DateTime.UtcNow - cached.FetchedAt).TotalSeconds < CACHE_TTL_SECONDS)
            {
                return Results.Content(cached.Content, "application/vnd.apple.mpegurl");
            }

            // --- 缓存失效，重新向虎牙 CDN 请求 ---
            string? resolvedSubUrl = null;

            using var request = new HttpRequestMessage(HttpMethod.Get, freshUrl);
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
// 这可以防止 Jellyfin 在 FFmpeg 重启或短暂中断时误以为直播已结束
app.MapGet("/live/{channelId}/stream.m3u8", (string channelId) =>
{
    string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channelId, "stream.m3u8");
    if (!File.Exists(m3u8Path))
    {
        return Results.NotFound();
    }
    try
    {
        // 用 FileShare.ReadWrite 避免与 FFmpeg 写入时的文件锁竞争
        using var fs = new FileStream(m3u8Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, System.Text.Encoding.UTF8);
        string content = sr.ReadToEnd();
        // 关键！剥除 #EXT-X-ENDLIST，这样 Jellyfin 永远不会认为直播结束
        content = content.Replace("#EXT-X-ENDLIST", "").TrimEnd();
        return Results.Content(content, "application/vnd.apple.mpegurl");
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
public class AppConfig
{
    public string CustomHost { get; set; } = "";
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

public static class Globals
{
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
    public static readonly HttpClient HttpClient = new();
    public static readonly ConcurrentDictionary<string, M3u8CacheEntry> M3u8Cache = new();
    public static readonly ConcurrentDictionary<string, PlatformCookieStatus> PlatformCookieStatuses = new(StringComparer.OrdinalIgnoreCase);
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
    private const int HEALTH_CHECK_SECONDS = 25;
    private const int STALE_THRESHOLD_SECONDS = 90;
    private static readonly string? FFMPEG_EXE_PATH = ResolveFfmpegPath();
    
    private readonly ConcurrentDictionary<string, Process> _ffmpegProcesses = new();
    private readonly AutoResetEvent _triggerEvent = new(false);
    private DateTime _lastCookieCheckTime = DateTime.MinValue;

    public StreamManagerService()
    {
        Globals.StreamManager = this;
    }

    public void NotifyConfigChanged()
    {
        _triggerEvent.Set();
    }

    public void RestartChannel(string channelId)
    {
        if (_ffmpegProcesses.TryRemove(channelId, out var proc))
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }
        _triggerEvent.Set();
    }

    public void StopChannel(string channelId)
    {
        if (_ffmpegProcesses.TryRemove(channelId, out var proc))
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }
        string dir = Path.Combine(Globals.HLS_FULL_PATH, channelId);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static string? ResolveFfmpegPath()
    {
        // 1. 优先检查当前程序同目录下的 ffmpeg.exe (或 Linux/macOS 下的 ffmpeg)
        string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string localPath = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(localPath)) return localPath;

        // 2. 检查系统环境变量 PATH
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

        while (!stoppingToken.IsCancellationRequested)
        {
            // 定期后台巡检已配置的 Cookie (每 30 分钟一次，启动时立即触发)
            if ((DateTime.UtcNow - _lastCookieCheckTime).TotalMinutes >= 30)
            {
                _lastCookieCheckTime = DateTime.UtcNow;
                _ = Task.Run(Globals.CheckAllPlatformCookiesAsync, stoppingToken);
            }

            List<ChannelConfig> currentChannels;
            lock (Globals.ConfigLock)
            {
                currentChannels = [.. Globals.Config.Channels];
            }

            var currentChannelIds = currentChannels.Select(c => c.Id).ToHashSet();

            // 1. 清理已在配置中被删除的频道的进程与状态
            foreach (var runningId in _ffmpegProcesses.Keys.ToArray())
            {
                if (!currentChannelIds.Contains(runningId))
                {
                    StopChannel(runningId);
                }
            }

            lock (Globals.StatusLock)
            {
                Globals.ChannelStatuses.RemoveAll(s => !currentChannelIds.Contains(s.Id));
                foreach (var ch in currentChannels)
                {
                    if (!Globals.ChannelStatuses.Any(s => s.Id == ch.Id))
                    {
                        Globals.ChannelStatuses.Add(new ChannelStatus { Id = ch.Id, Name = ch.Name });
                    }
                }
            }

            // 2. 遍历检查各个频道
            foreach (var channel in currentChannels)
            {
                if (stoppingToken.IsCancellationRequested) break;

                var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channel.Id);
                if (status == null) continue;

                // 如果频道未启用
                if (!channel.Enable)
                {
                    if (_ffmpegProcesses.TryGetValue(channel.Id, out var activeProcess))
                    {
                        Globals.UpdateStatus(channel.Id, "已禁用，正在停止 FFmpeg...", ConsoleColor.DarkGray);
                        try { activeProcess.Kill(); } catch { }
                        _ffmpegProcesses.TryRemove(channel.Id, out _);
                    }
                    else
                    {
                        Globals.UpdateStatus(channel.Id, "已禁用", ConsoleColor.DarkGray);
                    }
                    continue;
                }

                bool needsRestart = false;

                if (_ffmpegProcesses.TryGetValue(channel.Id, out var process))
                {
                    if (process.HasExited)
                    {
                        Globals.UpdateStatus(channel.Id, "FFmpeg 进程已退出，正在重启...", ConsoleColor.Yellow);
                        _ffmpegProcesses.TryRemove(channel.Id, out _);
                        needsRestart = true;
                    }
                    else
                    {
                        string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channel.Id, "stream.m3u8");
                        if (File.Exists(m3u8Path))
                        {
                            var lastWriteTime = File.GetLastWriteTime(m3u8Path);
                            if ((DateTime.Now - lastWriteTime).TotalSeconds > STALE_THRESHOLD_SECONDS)
                            {
                                Globals.UpdateStatus(channel.Id, "HLS 文件陈旧 (僵尸进程)，正在强制重启...", ConsoleColor.Red);
                                try { process.Kill(); } catch { }
                                _ffmpegProcesses.TryRemove(channel.Id, out _);
                                needsRestart = true;
                            }
                            else
                            {
                                Globals.UpdateStatus(channel.Id, "推流中", ConsoleColor.Green);
                                status.RetryCount = 0;
                            }
                        }
                    }
                }
                else
                {
                    needsRestart = true;
                }

                if (needsRestart)
                {
                    await StartSingleChannelAsync(channel, status);
                }
            }

            try
            {
                // 等待下一个健康巡检周期，或由 API 立即唤醒
                await Task.Run(() => _triggerEvent.WaitOne(TimeSpan.FromSeconds(HEALTH_CHECK_SECONDS)), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception) { }
        }
        
        // Cleanup on stop
        await StopAllStreamingSessionsAsync();
    }

    private async Task StartSingleChannelAsync(ChannelConfig channel, ChannelStatus status)
    {
        string channelHlsDir = Path.Combine(Globals.HLS_FULL_PATH, channel.Id);
        try
        {
            if (Directory.Exists(channelHlsDir) && !_ffmpegProcesses.ContainsKey(channel.Id))
            {
                foreach (var file in Directory.GetFiles(channelHlsDir, "*.ts")) 
                    try { File.Delete(file); } catch { }
                foreach (var file in Directory.GetFiles(channelHlsDir, "*.m3u8*")) 
                    try { File.Delete(file); } catch { }
            }
            Directory.CreateDirectory(channelHlsDir);
        }
        catch (Exception ex)
        {
            Globals.UpdateStatus(channel.Id, $"无法清理目录: {ex.Message}", ConsoleColor.Red);
            return;
        }

        Globals.UpdateStatus(channel.Id, "正在获取直播源...", ConsoleColor.DarkGray);

        var (sourceStreamUrl, error) = await GetSourceStreamUrlAsync(channel);

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
            return;
        }

        Globals.UpdateStatus(channel.Id, "成功获取源，正在启动 FFmpeg...", ConsoleColor.Cyan);

        string inputUrl = sourceStreamUrl;
        if (channel.Platform?.ToLower() == "huya")
        {
            inputUrl = $"http://127.0.0.1:{Globals.HTTP_PORT}/huya-source/{channel.Id}/stream.m3u8";
        }

        Process? ffmpegProcess = StartFfmpegHls(inputUrl, channelHlsDir);
        if (ffmpegProcess != null)
        {
            _ffmpegProcesses[channel.Id] = ffmpegProcess;
            Globals.UpdateStatus(channel.Id, "已启动推流", ConsoleColor.Green);
        }
        else
        {
            Globals.UpdateStatus(channel.Id, "FFmpeg 进程启动失败", ConsoleColor.Red);
        }
    }

    private async Task<(string? Url, string? Error)> GetSourceStreamUrlAsync(ChannelConfig channel)
    {
        try
        {
            if (!Globals.Extractors.TryGetValue(channel.Id, out var extractor))
            {
                extractor = channel.Platform?.ToLower() switch
                {
                    "bilibili" => new BilibiliExtractor(channel.Cookies),
                    "huya" => new HuyaExtractor(channel.Cookies),
                    "douyu" => new DouyuExtractor(channel.Cookies),
                    _ => throw new Exception($"未知的平台: {channel.Platform}")
                };
                Globals.Extractors[channel.Id] = extractor;
            }

            string? url = await extractor.GetStreamUrlAsync(channel.Url ?? "", channel.Quality ?? "OD");
            if (!string.IsNullOrEmpty(url))
            {
                return (url, null);
            }
            return (null, "未获取到直播流地址 (可能未开播或需要Cookie)");
        }
        catch (Exception ex)
        {
            return (null, $"解析失败: {ex.Message}");
        }
    }

    private Process? StartFfmpegHls(string sourceStreamUrl, string channelHlsDir)
    {
        string m3u8Path = Path.Combine(channelHlsDir, "stream.m3u8");
        string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        string arguments = $"-fflags +genpts+discardcorrupt -err_detect ignore_err "
            + $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 10 -reconnect_on_network_error 1 "
            + $"-rw_timeout 15000000 "
            + $"-headers \"Referer: https://live.bilibili.com/\r\n\" "
            + $"-user_agent \"{userAgent}\" "
            + $"-i \"{sourceStreamUrl}\" "
            + $"-c:v copy -c:a copy -sn -f hls -hls_time 2 -hls_list_size 10 -hls_allow_cache 0 "
            + $"-hls_delete_threshold 5 "
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
            if (process != null)
            {
                string logFilePath = Path.Combine(channelHlsDir, "ffmpeg.log");
                object logLock = new();
                _ = Task.Run(() => RedirectOutputToFileAsync(process.StandardError, logFilePath, logLock));
                _ = Task.Run(() => RedirectOutputToFileAsync(process.StandardOutput, logFilePath, logLock));
            }
            return process;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task StopAllStreamingSessionsAsync()
    {
        var procs = _ffmpegProcesses.Values.ToArray();
        foreach (var process in procs)
        {
            try
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }
        _ffmpegProcesses.Clear();
    }

    private static async Task RedirectOutputToFileAsync(StreamReader reader, string logFilePath, object logLock)
    {
        try
        {
            while (await reader.ReadLineAsync() is string line)
            {
                lock (logLock)
                {
                    File.AppendAllText(logFilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
        }
        catch (Exception) { }
    }
}
