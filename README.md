# LiveStreamGateway 🚀

> 基于 **.NET 10** 与 **FFmpeg** 构建的高性能多平台直播流中继网关与 IPTV (M3U) 转换服务。  
> 专为 **Jellyfin / Emby / Kodi / VLC / IPTV 客户端** 打造，彻底解决各大直播平台防盗链签名过期、断流、卡顿及直播边缘漂移等痛点问题。

<p align="center">
  <img src="docs/images/dashboard.png" alt="LiveStreamGateway Web Dashboard" width="850">
</p>

---

## 📖 诞生背景与初衷

### 💡 为什么开发这个项目？
许多家庭的（我的）老旧显示设备（例如 **极米 H1 投影仪**、老款智能电视、旧 Android 电视盒子等），其硬件解码芯片和运存配置较低。而如今各大直播平台（虎牙、斗鱼、B站）的官方 TV 版客户端越发臃肿：
- ❌ **客户端臃肿**：充斥着复杂的 UI、礼物动画、弹幕渲染、后台广告与高占用逻辑。
- ❌ **老设备频繁卡死**：在老旧设备上直接运行官方客户端，极易引发**内存溢出、严重卡顿、发热掉帧，甚至直接闪退与系统卡死**。
- ❌ **纯净流难以直接播放**：各大平台的直播流带有严苛的防盗链与短效签名机制（~110秒自动过期），直接把原始链接填进播放器会频繁出现 403 Forbidden 断流。

### 🎯 架构解耦思想
本项目采用 **“边缘中转 + 轻量渲染”** 的解耦架构：
1. **中转服务（Windows / 软路由 / NAS）**：在局域网主机上运行 `LiveStreamGateway`，负责处理最繁琐的 **直播流解析、动态反防盗链重签名、FFmpeg 守护切片与 M3U 网关分发**。
2. **终端播放（极米 H1 / 电视盒子）**：老旧设备上仅需安装极其轻量、纯粹的播放客户端（如 **Jellyfin TV / Emby / Kodi / TiviMate / APTV**），老设备只需负责硬件解码播放纯净流。

> **🎉 实测效果**：极米 H1 等老旧设备彻底摆脱客户端崩溃与卡顿，秒开直播，畅享丝滑稳定的 1080P/4K 原画赛事与主播流！

---

## 🌟 核心技术亮点与解决的痛点

### 1. 🛡️ 虎牙防盗链与短效签名过期 (403 Forbidden)
- **痛点**：虎牙 CDN 直播流的 HLS/FLV 签名（`wsSecret` 与 `wsTime`）通常只有约 110 秒有效期。常规播放器直接播放时，在分片刷新或重连时因签名过期必报 `403 Forbidden`。
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
- 全程采用视频/音频流直通复制（`-c:v copy -c:a copy`），无 CPU/GPU 软硬重编码损耗，即便在软路由、NAS（群晖/威联通/Unraid/飞牛 OS）等低功耗设备上亦可轻松并发多路 4K/原画推流。

### 4. 🔗 智能链接净化与自动秒级识别
- 任意粘贴包含复杂长追踪参数（如 `?hotRank=...&session_id=...`）的直播链接，系统自动过滤并净化为标准 URL；
- 平台全自动识别，无需手动选择虎牙/斗鱼/B站，自动拉取真实频道名称与房间号。

### 5. ⚡ 平台 Cookie 权威真伪鉴权 & 定时健康巡检
- 直连各大平台官方鉴权接口，实时验证 Cookie 真伪与大会员状态；
- 后台守护进程每隔 **30 分钟** 自动进行静默健康巡检；
- 大盘卡片极简状态联动，过期失效时自动以 **🔴 已过期** 红色醒目预警。

### 6. 📺 完美的 IPTV / Jellyfin 协议适配与守护自愈
- 提供标准的 IPTV M3U 播放列表端点 (`http://<ip>:9898/jellyfin.m3u`)；
- 支持独立频道使能开关，未启用频道自动停止推流并从 M3U 列表中过滤；
- 内置后台健康巡检，监控 HLS 切片文件的时间戳，遇到卡流或断流自动平滑重启。

---

## 🛠️ 支持平台

| 平台 | 状态 | 平台代号 (`Platform`) | 最高画质支持 | 说明 |
| :--- | :---: | :---: | :---: | :--- |
| **虎牙直播** | 🟢 完美支持 | `huya` | 4K / 原画 / 蓝光 | 支持动态反防盗链、自动重签、Cookie 鉴权 |
| **斗鱼直播** | 🟢 完美支持 | `douyu` | 原画 / 蓝光 8M | 支持房间长短号解析、Cookie 鉴权 |
| **哔哩哔哩** | 🟢 完美支持 | `bilibili` | 4K / 原画 1080P60 | 支持大会员高码率/高帧率流、Cookie 鉴权 |

---

## 🏗️ 架构流程图

```text
[虎牙 / 斗鱼 / B站 CDN 直播源]
             │
             ▼ (平台 API 解析 & 动态签名计算)
[LiveStreamGateway 中继网关 (Windows / NAS)]
             │
             ├─► [内置透明反向代理] ──► 动态重签 HLS / 扁平化主索引 / 剥除 ENDLIST
             │          │
             │          ▼
             ├─► [FFmpeg 守护引擎] ──► 极速切片 (Copy Mode, 2s/片, 零编解码)
             │          │
             │          ▼
             └─► [Kestrel HTTP 服务 (默认端口 9898)]
                        │
                        ├─► /jellyfin.m3u (标准 IPTV M3U 电视索引)
                        └─► /live/{channelId}/stream.m3u8 (HLS 播放流)
                                │
                                ▼
       [极米 H1 / Android 电视盒子 / Apple TV / PC]
       (运行 Jellyfin / Emby / Kodi / TiviMate / APTV)
```

---

## 🖥️ 现代化 Web 管理后台

程序启动后，直接在浏览器中打开：
👉 **`http://<主机IP>:9898`** (本地访问 `http://localhost:9898`)

即可进入现代简洁、自适应移动端的可视化管理控制台：

- 📊 **实时状态大盘**：实时展示当前活跃推流数、服务运行时间、各频道推流状态与重试计数；
- 📺 **频道热重载管理**：在网页端直接**添加、编辑、删除**频道，或者**一键开关、一键重启推流**，改动**实时热生效，无需重启服务**；
- 🔗 **一键识别录入**：粘贴任意直播间复杂链接，一键净化并自动识别平台、标题与房间号；
- 🍪 **平台 Cookie 折叠管理**：固定支持三大平台，支持折叠编辑、一键真伪鉴权检测与定时自动巡检；
- ▶️ **内置 HLS 试播播放器**：点击频道卡片上的【试播】按钮，直接在浏览器内实时弹窗播放直播流进行快速验证；
- 📋 **一键复制 M3U 订阅源**：一键复制 Jellyfin / IPTV 调谐器链接。

---

## 🐳 Docker / NAS 极速部署

本项目提供了官方多架构 Docker 镜像（支持 `linux/amd64` 与 `linux/arm64`），完美适配 **群晖 (Synology)、威联通 (QNAP)、Unraid、飞牛 OS (fnOS)、TrueNAS 及 Linux 软路由**。

### 方式 1：Docker Compose 一键启动（推荐）

在任意目录下创建 `docker-compose.yml`：

```yaml
version: '3.8'

services:
  live-stream-gateway:
    image: ghcr.io/yafeiml/livestreamgateway:latest
    container_name: live-stream-gateway
    restart: unless-stopped
    ports:
      - "9898:9898"
    volumes:
      # 持久化配置文件（请确保本地当前目录下有 config.json，可从 config.example.json 复制）
      - ./config.json:/app/config.json
    tmpfs:
      # 【NAS 核心保护优化】将 HLS 切片缓存挂载到系统内存（512M 足以支撑多路 1080P60 / 4K / 原画直播流并发），避免 7x24 小时读写机械硬盘导致磨损
      - /app/hls_stream:size=512M,mode=1777
    environment:
      - TZ=Asia/Shanghai
```

在同级目录下执行启动：
```bash
# 1. 复制一份初始配置文件（若已有可跳过）
curl -sSL https://raw.githubusercontent.com/Yafeiml/LiveStreamGateway/main/config.example.json -o config.json

# 2. 启动容器
docker compose up -d
```

#### 🔄 Docker 一键升级
- **Windows 用户**：直接双击仓库自带的 `upgrade.bat` 脚本即可全自动拉取最新镜像、平滑更新并清理旧镜像。
- **Linux / NAS 用户**：在 `docker-compose.yml` 所在目录执行：
  ```bash
  docker compose pull && docker compose up -d --remove-orphans && docker image prune -f
  ```

### 方式 2：Docker 命令行启动

```bash
docker run -d \
  --name live-stream-gateway \
  --restart unless-stopped \
  -p 9898:9898 \
  -v $(pwd)/config.json:/app/config.json \
  --tmpfs /app/hls_stream:size=512M \
  -e TZ=Asia/Shanghai \
  ghcr.io/yafeiml/livestreamgateway:latest
```

启动完成后，直接访问 `http://<NAS的IP>:9898` 即可通过 Web 界面可视化管理！

---

## 🚀 本地 Windows / 源码使用说明

### 步骤 1：准备运行环境
1. 确保安装了 [.NET 10 Runtime / SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（如果从 [Releases](https://github.com/Yafeiml/LiveStreamGateway/releases) 下载独立版则无需安装 .NET，解压即跑）。
2. **准备 FFmpeg**（支持以下任选一种方式）：
   - **方式 1（最简单）**：从 Releases 直接下载打包好的完整压缩包（已内置官方精简版 `ffmpeg.exe`）。
   - **方式 2（环境变量）**：程序会**自动扫描系统 `PATH` 环境变量**，已有 FFmpeg 的电脑无需额外配置。
   - **方式 3（Windows 终端一行命令安装）**：在终端运行 `winget install Gyan.FFmpeg` 即可全自动安装。

### 步骤 2：启动服务
在项目目录下执行：
```bash
dotnet run
```
或直接双击运行下载的 `LiveStreamGateway.exe`。

### 步骤 3：在客户端中挂载播放

#### 方式 A：接入 Jellyfin / Emby（推荐，极米 H1 / 电视盒端）
1. 登录 Jellyfin / Emby 管理后台。
2. 进入 **控制台 -> 电视 (Live TV)** -> **电视源 (Tuner Devices)**。
3. 点击 **添加**，类型选择 **M3U 播放列表 (M3U Tuner)**。
4. **文件或 URL** 填写：
   ```text
   http://<运行主机的局域网IP>:9898/jellyfin.m3u
   ```
5. 保存后，在电视端（如极米 H1 上的 Jellyfin 客户端）打开 **“直播电视” (Live TV)**，即可看到所有频道并流畅播放！

#### 方式 B：接入通用 IPTV 播放器 (TiviMate / APTV / PotPlayer / VLC)
- 直接在播放器中添加订阅源 URL：
  ```text
  http://<运行主机的局域网IP>:9898/jellyfin.m3u
  ```
- 或单独播放单个频道：
  ```text
  http://<运行主机的局域网IP>:9898/live/<频道Id>/stream.m3u8
  ```

---

## ⚙️ 配置文件详细说明 (`config.json`)

配置文件分为两大部分：**CookieProfiles（凭据池）** 与 **Channels（频道列表）**。

### 完整配置示例

```json
{
  "CookieProfiles": {
    "huya": "guid=xxx; udb_guiddata=xxx; udb_biztoken=xxx; ...",
    "douyu": "（可选：填入斗鱼网页版 Cookie）",
    "bilibili": "（可选：填入 B 站网页版 Cookie）"
  },
  "Channels": [
    {
      "Id": "huya_eslcs",
      "Name": "虎牙-CS赛事",
      "Platform": "huya",
      "Url": "https://www.huya.com/eslcs",
      "Quality": "OD",
      "CookieProfileKey": "huya",
      "Enable": true
    },
    {
      "Id": "douyu_dota2",
      "Name": "斗鱼-DOTA2赛事",
      "Platform": "douyu",
      "Url": "https://www.douyu.com/9999",
      "Quality": "OD",
      "CookieProfileKey": "douyu",
      "Enable": true
    },
    {
      "Id": "huya_streamer_backup",
      "Name": "虎牙-备用频道",
      "Platform": "huya",
      "Url": "https://www.huya.com/660004",
      "Quality": "OD",
      "CookieProfileKey": "huya",
      "Enable": false
    }
  ]
}
```

### 字段说明表

#### 1. `CookieProfiles`（可选）
| 字段 | 类型 | 说明 |
| :--- | :--- | :--- |
| `Key` (如 `huya`) | string | 平台代号（`huya` / `douyu` / `bilibili`） |
| `Value` | string | 从浏览器 F12 抓取的直播平台完整 Cookie 字符串（配置后可直接获取最高原画/蓝光 4K 码率） |

#### 2. `Channels`（频道列表）
| 字段 | 类型 | 必填 | 说明 |
| :--- | :--- | :---: | :--- |
| `Id` | string | 是 | 频道唯一英文字符串标识（用于 HLS 路由与缓存目录命名，如 `huya_eslcs`） |
| `Name` | string | 是 | 频道显示名称（在 Jellyfin / IPTV 电视列表中展示的频道名） |
| `Platform` | string | 是 | 直播平台代号：可选 `huya` (虎牙)、`douyu` (斗鱼)、`bilibili` (B站) |
| `Url` | string | 是 | 直播间的完整网页 URL（支持房间号、靓号或个性域名） |
| `Quality` | string | 否 | 画质偏好：`OD`(原画) / `BD`(蓝光) / `HD`(超清) / `SD`(高清)，默认 `OD` |
| `CookieProfileKey` | string | 否 | 关联的 CookieProfile 键名（如 `huya`），留空或不填则使用免登录画质 |
| `Enable` | bool | 否 | **频道开关**：`true` 正常拉流并导出到 M3U；`false` 禁用该频道（自动释放进程且不显示在列表中） |

---

## 📄 开源许可证

本项目基于 [MIT 许可证](LICENSE) 开源。仅供个人技术研究、局域网家庭多媒体串流与老旧设备复用交流，严禁用于任何商业营利用途。
