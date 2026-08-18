## 🚀 v1.3.0 正式版发布 (Major Rebranding: LiveStreamGateway)

> 全平台直播流中继与 IPTV 聚合网关正式更名为 **`LiveStreamGateway`**！从最初的单平台解析脚本，全面进化为现代化的全平台直播聚合中枢与媒体库（Jellyfin/Emby/IPTV）流转换服务。

---

### 🌟 核心变更与升级

1. **🏛️ 品牌与工程架构全面统一**
   - 项目正式更名为 **`LiveStreamGateway`**；
   - C# 工程（`.csproj` / `.sln`）与命名空间（`namespace LiveStreamGateway`）全面统一；
   - 官方多架构 Docker 镜像升级为 `ghcr.io/yafeiml/livestreamgateway` 与 `yafeiml/livestreamgateway`；
   - Windows 独立预编译发布包命名统一为 `LiveStreamGateway-$ver-win-x64.zip`。

2. **🎨 现代看板与动态声波均衡器**
   - 移除频道卡片底部的冗余状态栏，卡片视觉更规整；
   - 右上角推流胶囊内置 **3 段实时跳动的绿色声波/均衡器动态动画（Equalizer Wave Animation）**；
   - 频道参数列表采用左右两端对齐精致排版，字号精简至 12px。

3. **▶️ 统一纯图标试播组件**
   - 试播按钮重构为纯图标按钮 `[▶]`，与重启、编辑、删除操作按钮保持完全一致的规格与排布。

4. **🛡️ 统一风格的深色 UI 确认弹窗**
   - 全面移除浏览器原生生硬的 `confirm()` 弹窗，替换为全局暗色毛玻璃卡片风格的自定义模态确认框；
   - 删除频道、清空 Cookie 等高危操作均支持深色警告弹窗与 `Enter` / `Esc` 快捷键。

5. **⚡ 平台 Cookie 权威真伪鉴权 & 定时健康巡检**
   - 虎牙、斗鱼、B站 Cookie 直连官方接口鉴权，支持 30 分钟静默健康巡检与大盘红字预警联动。
