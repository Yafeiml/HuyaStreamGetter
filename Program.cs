#nullable enable

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using HuyaStreamGetter;

// -------------------------------------------------------------
// Top-Level Application Setup
// -------------------------------------------------------------
try {
    Console.Title = "HuyaStreamGetter - .NET 10";
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

// 1. 获取全局系统状态与频道状态大盘
app.MapGet("/api/status", () =>
{
    var uptime = DateTime.UtcNow - Globals.StartTimeUtc;
    var channelStatusList = new List<object>();

    lock (Globals.StatusLock)
    {
        foreach (var channel in Globals.Config.Channels)
        {
            var status = Globals.ChannelStatuses.FirstOrDefault(s => s.Id == channel.Id);
            string m3u8Path = Path.Combine(Globals.HLS_FULL_PATH, channel.Id, "stream.m3u8");
            bool isStreaming = File.Exists(m3u8Path) && channel.Enable && 
                (DateTime.Now - File.GetLastWriteTime(m3u8Path)).TotalSeconds <= 90;

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
                hlsUrl = $"/live/{channel.Id}/stream.m3u8",
                fullHlsUrl = $"http://{Globals.LocalIp}:{Globals.HTTP_PORT}/live/{channel.Id}/stream.m3u8"
            });
        }
    }

    int activeCount = channelStatusList.Count(c => (bool)((dynamic)c).isLive);

    return Results.Json(new
    {
        serverStatus = "运行中",
        localIp = Globals.LocalIp,
        httpPort = Globals.HTTP_PORT,
        m3uUrl = $"http://{Globals.LocalIp}:{Globals.HTTP_PORT}/jellyfin.m3u",
        uptimeSeconds = (int)uptime.TotalSeconds,
        uptimeText = $"{(int)uptime.TotalHours}小时 {uptime.Minutes}分 {uptime.Seconds}秒",
        activeStreams = activeCount,
        totalChannels = Globals.Config.Channels.Count,
        channels = channelStatusList
    });
});

// 2. 获取配置 (Channels + CookieProfiles)
app.MapGet("/api/config", () =>
{
    lock (Globals.ConfigLock)
    {
        return Results.Json(Globals.Config);
    }
});

// 3. 添加或更新频道
app.MapPost("/api/channels", async (ChannelConfig newChannel) =>
{
    if (string.IsNullOrWhiteSpace(newChannel.Name))
        return Results.BadRequest(new { error = "频道名称不能为空" });

    if (string.IsNullOrWhiteSpace(newChannel.Platform))
        return Results.BadRequest(new { error = "所属平台不能为空" });

    if (string.IsNullOrWhiteSpace(newChannel.Url))
        return Results.BadRequest(new { error = "直播间 URL 不能为空" });

    // 若 ID 为空，自动生成 ID
    if (string.IsNullOrWhiteSpace(newChannel.Id))
    {
        newChannel.Id = $"{newChannel.Platform.ToLower()}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }
    else
    {
        // 移除非法字符
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
            existing.CookieProfileKey = newChannel.CookieProfileKey;
            existing.Enable = newChannel.Enable;
        }
        else
        {
            newChannel.Quality = string.IsNullOrWhiteSpace(newChannel.Quality) ? "OD" : newChannel.Quality;
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

// 7. 添加或更新 Cookie Profile
app.MapPost("/api/cookies", async (CookieProfileRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Key))
        return Results.BadRequest(new { error = "Cookie 标识 (Key) 不能为空" });

    lock (Globals.ConfigLock)
    {
        Globals.Config.CookieProfiles[req.Key.Trim()] = req.Cookie ?? "";
        RefreshChannelCookies();
    }

    await SaveConfigAsync();
    Globals.StreamManager?.NotifyConfigChanged();

    return Results.Ok(new { success = true, key = req.Key });
});

// 8. 删除 Cookie Profile
app.MapDelete("/api/cookies/{key}", async (string key) =>
{
    bool removed = false;
    lock (Globals.ConfigLock)
    {
        removed = Globals.Config.CookieProfiles.Remove(key);
        if (removed)
        {
            // 清理对应频道的 CookieProfileKey 引用
            foreach (var ch in Globals.Config.Channels.Where(c => c.CookieProfileKey == key))
            {
                ch.CookieProfileKey = null;
                ch.Cookies = "";
            }
        }
    }

    if (removed)
    {
        await SaveConfigAsync();
        Globals.StreamManager?.NotifyConfigChanged();
        return Results.Ok(new { success = true, message = $"Cookie Profile '{key}' 已删除" });
    }

    return Results.NotFound(new { error = "未找到指定的 Cookie Profile" });
});

// Master Playlist Endpoint - 指向动态代理而非静态文件
app.MapGet("/jellyfin.m3u", () =>
{
    var m3uContent = new StringBuilder("#EXTM3U\n");
    lock (Globals.ConfigLock)
    {
        foreach (var channel in Globals.Config.Channels)
        {
            if (!channel.Enable) continue;
            m3uContent.AppendLine($"#EXTINF:-1 tvg-name=\"{channel.Name}\" tvg-id=\"{channel.Id}\" group-title=\"{channel.Platform}\",{channel.Name}");
            m3uContent.AppendLine($"http://{Globals.LocalIp}:{Globals.HTTP_PORT}/live/{channel.Id}/stream.m3u8");
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
    if (Globals.Config.CookieProfiles == null) return;
    foreach (var channel in Globals.Config.Channels)
    {
        if (!string.IsNullOrEmpty(channel.CookieProfileKey))
        {
            if (Globals.Config.CookieProfiles.TryGetValue(channel.CookieProfileKey, out var cookieString))
            {
                channel.Cookies = cookieString;
            }
        }
    }
}

static string GetLocalIPAddress()
{
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
