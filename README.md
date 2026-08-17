# HuyaStreamGetter / LiveStreamGateway 🚀

> 一个基于 **.NET 10** 与 **FFmpeg** 的高性能多平台直播流中继网关与 IPTV (M3U) 转换服务。
> 专为 **Jellyfin / Emby / Kodi / VLC / IPTV 客户端** 打造，彻底解决各大直播平台防盗链签名过期、断流、卡顿及直播边缘漂移等痛点问题。

---

## 🌟 项目亮点与核心解决的问题

在将虎牙、斗鱼、B站等国内直播源接入 Jellyfin / Emby 等家庭影院系统时，常规方式通常会遭遇以下几个致命痛点，本项目针对性地进行了底层架构重构与深度优化：

### 1. 🛡️ 虎牙防盗链与短效签名过期问题 (403 Forbidden)
- **痛点**：虎牙 CDN 直播流的 HLS/FLV 签名（`wsSecret` 与 `wsTime`）通常只有约 110 秒有效期。常规播放器或直接调用 FFmpeg 拉流时，在切片更新或断线重连时使用原始链接必报 `403 Forbidden` 断流。
- **解决方案**：
  - 内置**动态透明反向代理端点** (`/huya-source/{channelId}/stream.m3u8`)。
  - **扁平化 Master Playlist (Master Playlist Flattening)**：自动解析多码率主索引，直接提取并重签底层包含实际 TS 分片的 Media Playlist，杜绝播放器/FFmpeg 绕过代理。
  - 动态重签机制 + 1.5s 智能防抖缓存，彻底消除 CDN 重复请求与 403 过期错误。

### 2. ⚡ 直播边缘漂移与长时间播放变卡 (Live Edge Drift)
- **痛点**：HLS 直播采用滑动窗口切片（Sliding Window）。当客户端网络轻微波动或解码延迟，播放位置逐渐落后于最新分片，最终请求到已被服务端删除的旧分片导致 404、严重卡顿、慢动作。
- **解决方案**：
  - 优化切片策略：`-hls_time 2 -hls_list_size 10 -hls_delete_threshold 5`，大幅收窄延迟漂移空间并保留安全缓冲。
  - 注入 `#EXT-X-ALLOW-CACHE:NO` 标签，强制 Jellyfin 等客户端紧跟实时直播边缘。
  - 动态剥离 `#EXT-X-ENDLIST` 标记，避免推流短暂重连时播放器误判直播结束自动退出。

### 3. 🎯 零转码、超低资源消耗 (Zero-Transcoding)
- 全程采用视频/音频流直通复制（`-c:v copy -c:a copy`），无 CPU/GPU 软硬重编码损耗，即便在软路由、NAS（如群晖、威联通、Unraid、飞牛 OS）等低功耗设备上亦可轻松并发多路 4K/原画推流。

### 4. 📺 完美的 IPTV / Jellyfin 协议适配
- 提供标准的 IPTV M3U 播放列表端点 (`http://<ip>:9898/jellyfin.m3u`)。
- 支持独立频道使能开关 (`"Enable": true/false`)，未启用频道自动停止推流并从 M3U 列表中过滤。
- 控制台实时刷新仪表盘，直观展示各频道状态、拉流重试次数及健康监控信息。

### 5. 🔄 守护进程与自愈机制 (Process Supervision)
- 内置后台健康巡检，监控 HLS 切片文件的时间戳。若检测到流挂起/僵尸进程，自动释放旧资源并平滑重启推流，保障 7×24 小时无人值守稳定运行。

---

## 🛠️ 支持平台

| 平台 | 状态 | 说明 |
| :--- | :---: | :--- |
| **虎牙直播 (Huya)** | 🟢 完美支持 | 支持全码率、动态反防盗链、自动重签 |
| **斗鱼直播 (Douyu)** | 🟢 完美支持 | 支持房间号与短号解析 |
| **哔哩哔哩 (Bilibili)** | 🟢 完美支持 | 支持原画、超清等多画质解析 |

---

## 🏗️ 架构流程图

```text
[虎牙/斗鱼/B站 CDN]
        │
        ▼ (平台 API 解析 & 动态签名计算)
[HuyaStreamGetter 核心网关]
        │
        ├─► [内置透明反向代理] ──► 动态重签 HLS / 剥除 ENDLIST
        │          │
        │          ▼
        ├─► [FFmpeg 守护引擎] ──► 极速切片 (Copy Mode, 2s/片, 零编解码)
        │          │
        │          ▼
        └─► [Kestrel HTTP 服务 (9898端口)]
                   │
                   ├─► /jellyfin.m3u (标准 IPTV M3U 索引)
                   └─► /live/{channelId}/stream.m3u8 (HLS 播放流)
                           │
                           ▼
             [Jellyfin / Emby / IPTV / VLC]
```

---

## 🚀 快速上手

### 前置要求
1. 安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更高版本。
2. 下载 [FFmpeg](https://ffmpeg.org/download.html)，将 `ffmpeg.exe` 放置在程序根目录（或构建输出的运行目录中）。

### 1. 克隆项目
```bash
git clone https://github.com/<your-username>/HuyaStreamGetter.git
cd HuyaStreamGetter
```

### 2. 配置文件
复制一份配置文件：
```bash
cp config.example.json config.json
```
根据需求编辑 `config.json`：
```json
{
  "CookieProfiles": {
    "huya_main": "（可选：填入虎牙 Cookie 可观看最高原画码率）"
  },
  "Channels": [
    {
      "Id": "huya_eslcs",
      "Name": "虎牙-CS赛事",
      "Platform": "huya",
      "Url": "https://www.huya.com/eslcs",
      "Quality": "OD",
      "CookieProfileKey": "huya_main",
      "Enable": true
    },
    {
      "Id": "huya_streamer",
      "Name": "虎牙-主播房间",
      "Platform": "huya",
      "Url": "https://www.huya.com/660004",
      "Quality": "OD",
      "CookieProfileKey": "huya_main",
      "Enable": false
    }
  ]
}
```

### 3. 构建与运行
```bash
dotnet run
```

### 4. 接入 Jellyfin / Emby / IPTV
1. 打开 Jellyfin 控制台 -> **电视 (Live TV)** -> **电视源 (Tuner Devices)**。
2. 添加 **M3U 调谐器**：
   - 文件或 URL：`http://<运行主机的局域网IP>:9898/jellyfin.m3u`
3. 保存并刷新指南即可畅享流畅直播！

---

## ⚙️ 配置说明

| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| `Id` | string | 频道唯一英文标识（用于 HLS 路由与目录命名） |
| `Name` | string | 频道显示名称（M3U 中展示的电视频道名） |
| `Platform` | string | 直播平台：`huya` / `douyu` / `bilibili` |
| `Url` | string | 直播间完整网页 URL |
| `Quality` | string | 画质等级：`OD`(原画) / `BD`(蓝光) / `HD`(超清) / `SD`(高清) |
| `CookieProfileKey` | string | 对应的 Cookie 配置项 Key |
| `Enable` | bool | `true` 开启推流并加入 M3U；`false` 禁用并自动关闭进程 |

---

## 📄 开源许可证

本项目基于 [MIT 许可证](LICENSE) 开源。仅供个人技术研究、局域网家庭多媒体串流交流使用，严禁用于商业牟利。
