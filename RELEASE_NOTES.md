## 🚀 v1.5.0 更新说明 (Release Notes)

### 🛡️ Docker 崩溃根治：按需推流模式 + 资源生命周期全面修复

本版本针对 **Docker Desktop 持续内存泄漏导致容器崩溃** 的问题进行了架构级修复。

#### 核心变更

1. **🆕 新增按需推流模式 (OnDemand)**
   - 支持 `StreamingMode` 切换（`"AlwaysOn"` 默认常驻推流 / `"OnDemand"` 按需推流）；
   - 在 `OnDemand` 模式下无人观看时 FFmpeg 自动停止（默认空闲超时 300 秒），彻底消除持续媒体流量触发 Docker Desktop 网络后端内存泄漏的根因；
   - 客户端访问 `/live/{channelId}/stream.m3u8` 自动触发 FFmpeg 启动，启动期间返回 `503 + Retry-After`，播放器会在 5 秒后自动重试；
   - 并发首次请求使用 per-channel 启动锁，确保同一频道只创建一个 FFmpeg（防启动风暴）；
   - 新增配置字段：`StreamingMode`（默认 `AlwaysOn`）、`IdleTimeoutSeconds`（默认 300）、`StartupTimeoutSeconds`（默认 30）、`PrewarmEnabledChannels`（预热列表）；
   - **完全向后兼容**：旧 `config.json` 无需修改。

2. **🔧 FFmpeg 资源生命周期全面修复**
   - 引入 `StreamingSession`（`IAsyncDisposable`）封装 FFmpeg 进程、日志 `StreamWriter`、`SemaphoreSlim` 和日志任务，确保每次 FFmpeg 重启后所有资源可靠释放，彻底消除文件句柄泄漏；
   - FFmpeg 停止时调用 `Process.Kill(entireProcessTree: true)` + `await WaitForExitAsync()` 终止完整进程树，避免孤儿进程遗留；
   - 日志任务支持协作取消（`CancellationToken`），进程停止后任务可干净退出。

3. **🌐 HttpClient 资源优化**
   - 全局共享 `HttpClient` 改用配置了连接生命周期（10 分钟）和空闲超时（2 分钟）的 `SocketsHttpHandler`，避免长期运行时连接池资源耗尽。

4. **📊 新增接口**
   - `GET /api/metrics`：返回每频道运行指标（推流状态、m3u8 刷新次数、启动/重启/错误计数、最后客户端访问时间）；
   - `GET /api/health`：健康检查接口，可被 Docker healthcheck 和监控系统调用。

5. **🐳 Docker Compose 改进**
   - 新增 `logging` 日志轮转（单文件 50MB，最多保留 3 个）；
   - 新增 `healthcheck`（每 30 秒检查 `/api/health`，连续 3 次失败触发重启）；
   - 新增 `deploy.resources.limits.memory: 1G` 防止 OOM 无限占用。

---

## 🚀 v1.4.0 更新说明 (Release Notes)

### ⚡ 直播流中继全链路防卡顿与性能专项升级

本版本针对高码率直播中继过程中的**间歇性卡顿、播放停顿与死锁缓冲**进行了全链路深度重构与优化：

1. **🚫 m3u8 播放列表端点显式禁用缓存**
   - 解决客户端、Jellyfin 代理层及反向代理缓存 `.m3u8` 导致客户端播放完旧分片后陷入停顿的问题；
   - 在 `/live/{channelId}/stream.m3u8` 响应中统一注入 `Cache-Control: no-cache, no-store, must-revalidate`、`Pragma: no-cache` 和 `Expires: 0`。

2. **🌐 跨平台 Referer 动态自适应**
   - 彻底修复各平台 FFmpeg 进程全部硬编码 B 站 Referer 的问题；
   - 根据频道实际所属平台动态注入对应的官方 `Referer`（虎牙/斗鱼/B站），消除上游 CDN 防盗链检测导致的限速、连接重置与 403 异常。

3. **💾 Docker `tmpfs` 内存盘容量扩容至 512MB**
   - 将容器内 HLS 切片内存盘由 128MB 扩容至 **512MB**；
   - 完美支撑多路 1080P60 / 4K / 原画直播流并发切片，杜绝内存盘满溢导致的磁盘 I/O 写入失败。

4. **⏱️ HLS 滑动窗口与容错缓冲扩展**
   - 将切片参数调优为 `-hls_time 3 -hls_list_size 15 -hls_delete_threshold 10`；
   - 播放列表覆盖窗口从 ~30 秒扩展至 **~75 秒以上**，大幅增强播放器面对网络偶发抖动时的容错抗抖能力。

5. **⚡ 虎牙代理 Master Playlist 跳过与子列表复用**
   - 重构虎牙 HLS 代理签名重写管道，直接基于缓存的子播放列表路径与最新签名组装目标 URL；
   - 消除每次 m3u8 刷新时向虎牙 CDN 请求两次的公网双重 RTT 开销。

6. **🛡️ 频道重启平滑过渡保护**
   - 频道重启或重连时不再粗暴清空已生成的全部 `.ts` 切片，仅重置 m3u8 列表并交由 FFmpeg 滑动窗口自动淘汰旧分片，防止正在播放的客户端遭遇 404 错误。

7. **🚀 日志 I/O 异步批量写入引擎**
   - 使用常驻 `StreamWriter` + 异步信号量批量刷新日志，彻底替代逐行同步 `File.AppendAllText`，消除 FFmpeg 高频日志导致的磁盘锁竞争与 CPU 上下文开销。

8. **🔄 僵尸流自愈检测周期提速**
   - 健康巡检周期由 25s 缩短至 **10s**，陈旧流判定阈值由 90s 缩短至 **30s**，僵死进程自愈提速近 3 倍。
