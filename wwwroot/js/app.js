// ==========================================================
// HuyaStreamGateway - Web Management Controller
// ==========================================================

let appState = {
    status: null,
    config: null,
    editingChannelId: null,
    hlsPlayer: null,
    pollTimer: null,
    urlDebounceTimer: null
};

document.addEventListener('DOMContentLoaded', () => {
    initApp();
});

async function initApp() {
    setupEventListeners();
    await loadConfig();
    await loadStatus();
    
    // Auto refresh status every 3 seconds
    appState.pollTimer = setInterval(loadStatus, 3000);
}

function setupEventListeners() {
    // Copy M3U Button
    document.getElementById('btn-copy-m3u')?.addEventListener('click', copyM3uUrl);
    
    // Manual Refresh Button with Click Spin Animation
    document.getElementById('btn-manual-refresh')?.addEventListener('click', async () => {
        const btn = document.getElementById('btn-manual-refresh');
        btn?.classList.add('spinning');
        await loadStatus(true);
        setTimeout(() => {
            btn?.classList.remove('spinning');
        }, 600);
    });

    // Add Channel Button
    document.getElementById('btn-add-channel')?.addEventListener('click', () => {
        openAddChannelModal();
    });

    // Manage Cookies Button
    document.getElementById('btn-manage-cookies')?.addEventListener('click', () => {
        openCookieModal();
    });

    // Copy stream url button inside preview modal
    document.getElementById('btn-copy-stream-url')?.addEventListener('click', () => {
        const url = document.getElementById('preview-hls-url')?.innerText;
        if (url) {
            navigator.clipboard.writeText(url).then(() => {
                showToast('HLS 播放链接已复制到剪贴板', 'success');
            });
        }
    });
}

// -------------------------------------------------------------
// API Data Fetching
// -------------------------------------------------------------

async function loadConfig() {
    try {
        const res = await fetch('/api/config');
        if (res.ok) {
            appState.config = await res.json();
            updateCookieStatusSummary();
        }
    } catch (err) {
        console.error('加载配置失败:', err);
    }
}

async function loadStatus(showToastOnSuccess = false) {
    try {
        const res = await fetch('/api/status');
        if (res.ok) {
            appState.status = await res.json();
            renderDashboard();
            if (showToastOnSuccess) {
                showToast('状态已刷新', 'success');
            }
        }
    } catch (err) {
        console.error('获取系统状态失败:', err);
    }
}

function updateCookieStatusSummary() {
    const profiles = appState.config?.cookieProfiles || {};
    const configuredList = [];
    if (profiles.huya && profiles.huya.trim().length > 0) configuredList.push('虎牙');
    if (profiles.douyu && profiles.douyu.trim().length > 0) configuredList.push('斗鱼');
    if (profiles.bilibili && profiles.bilibili.trim().length > 0) configuredList.push('B站');

    const summaryEl = document.getElementById('stat-cookie-count');
    if (summaryEl) {
        if (configuredList.length === 3) {
            summaryEl.innerHTML = '<span style="color: var(--success);">全部已配置</span>';
        } else if (configuredList.length > 0) {
            summaryEl.innerText = `${configuredList.join(' · ')} 已配置`;
        } else {
            summaryEl.innerHTML = '<span style="color: var(--text-muted);">全平台免登录</span>';
        }
    }
}

// -------------------------------------------------------------
// Dashboard Rendering
// -------------------------------------------------------------

function renderDashboard() {
    if (!appState.status) return;

    const s = appState.status;

    // Overview stats
    document.getElementById('stat-active-streams').innerText = `${s.activeStreams} / ${s.totalChannels}`;
    document.getElementById('stat-uptime').innerText = s.uptimeText || '刚刚启动';
    document.getElementById('stat-local-ip').innerText = `${s.localIp}:${s.httpPort}`;
    document.getElementById('badge-total-channels').innerText = `${s.totalChannels} 个频道`;

    // Render channels
    const container = document.getElementById('channels-container');
    if (!s.channels || s.channels.length === 0) {
        container.innerHTML = `
            <div class="loading-placeholder">
                <p>暂无配置任何直播频道</p>
                <button class="btn btn-primary" style="margin-top: 12px;" onclick="openAddChannelModal()">+ 立即添加第一个频道</button>
            </div>
        `;
        return;
    }

    container.innerHTML = s.channels.map(ch => createChannelCardHtml(ch)).join('');
}

function createChannelCardHtml(ch) {
    const platformClass = `platform-${ch.platform?.toLowerCase() || 'huya'}`;
    const platformName = getPlatformDisplayName(ch.platform);

    let statusBadgeClass = 'waiting';
    let statusText = ch.statusMessage || '准备中';
    let statusIconHtml = '';

    // 状态分类判断与图标
    if (!ch.enable) {
        statusBadgeClass = 'disabled';
        statusText = '已禁用';
        statusIconHtml = `
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#64748b" stroke-width="2">
                <circle cx="12" cy="12" r="10"></circle>
                <line x1="4.93" y1="4.93" x2="19.07" y2="19.07"></line>
            </svg>
        `;
    } else if (ch.isLive) {
        statusBadgeClass = 'live';
        statusText = '推流中';
        // 动态跳动绿色声波动画图标
        statusIconHtml = `
            <div class="live-wave-anim" title="正在实时推流">
                <span class="bar"></span>
                <span class="bar"></span>
                <span class="bar"></span>
            </div>
        `;
    } else if (ch.statusMessage?.includes('未开播')) {
        statusBadgeClass = 'waiting';
        statusText = '未开播';
        statusIconHtml = `
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#f59e0b" stroke-width="2">
                <circle cx="12" cy="12" r="10"></circle>
                <polyline points="12 6 12 12 16 14"></polyline>
            </svg>
        `;
    } else if (ch.statusMessage?.includes('Cookie') || ch.statusMessage?.includes('登录')) {
        statusBadgeClass = 'error';
        statusText = 'Cookie失效';
        statusIconHtml = `
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#ef4444" stroke-width="2">
                <circle cx="12" cy="12" r="10"></circle>
                <line x1="12" y1="8" x2="12" y2="12"></line>
                <line x1="12" y1="16" x2="12.01" y2="16"></line>
            </svg>
        `;
    } else {
        statusBadgeClass = 'error';
        statusText = ch.statusMessage || '异常';
        statusIconHtml = `
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#ef4444" stroke-width="2">
                <circle cx="12" cy="12" r="10"></circle>
                <line x1="12" y1="8" x2="12" y2="12"></line>
                <line x1="12" y1="16" x2="12.01" y2="16"></line>
            </svg>
        `;
    }

    // 平台 Cookie 状态展示
    const platformKey = (ch.platform || 'huya').toLowerCase();
    const hasPlatformCookie = appState.config?.cookieProfiles?.[platformKey]?.trim()?.length > 0;
    const cookieDisplayText = hasPlatformCookie 
        ? `<span class="cookie-tag-inline active">🔑 ${platformName} Cookie (已授权)</span>`
        : `<span class="cookie-tag-inline muted">免登录模式</span>`;

    // 试播按钮可用状态 (只有推流中才能试播)
    const canPreview = ch.isLive;
    const previewBtnHtml = canPreview
        ? `<button class="btn btn-sm btn-secondary" onclick="openPreviewModal('${ch.id}', '${escapeHtml(ch.name)}', '${ch.hlsUrl}')" title="在网页中实时试播">
                <svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor">
                    <polygon points="5 3 19 12 5 21 5 3"></polygon>
                </svg>
                <span>试播</span>
           </button>`
        : `<button class="btn btn-sm btn-secondary btn-disabled" disabled title="当前未处于推流中，无法试播">
                <svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor">
                    <polygon points="5 3 19 12 5 21 5 3"></polygon>
                </svg>
                <span>试播</span>
           </button>`;

    return `
        <div class="channel-card ${ch.enable ? '' : 'disabled'}" id="card-${ch.id}">
            <div>
                <div class="channel-header">
                    <div class="channel-title-wrap">
                        <span class="platform-pill ${platformClass}">${platformName}</span>
                        <h3 class="channel-name" title="${escapeHtml(ch.name)}">${escapeHtml(ch.name)}</h3>
                    </div>
                    <span class="status-pill ${statusBadgeClass}">${statusText}</span>
                </div>

                <div class="channel-info-list">
                    <div class="info-item">
                        <span class="info-label">频道 ID:</span>
                        <span class="info-value"><code>${escapeHtml(ch.id)}</code></span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">画质等级:</span>
                        <span class="info-value">${ch.quality || 'OD (原画)'}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">Cookie 授权:</span>
                        <span class="info-value">${cookieDisplayText}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">源链接:</span>
                        <span class="info-value" title="${escapeHtml(ch.url)}">${escapeHtml(ch.url)}</span>
                    </div>
                </div>

                <div class="status-msg-box ${ch.isLive ? 'live' : ''}" title="${escapeHtml(ch.statusMessage)}">
                    ${statusIconHtml}
                    <span style="overflow: hidden; text-overflow: ellipsis; white-space: nowrap; ${ch.isLive ? 'color: #34d399; font-weight: 500;' : ''}">${escapeHtml(ch.statusMessage)}</span>
                    ${ch.retryCount > 0 && !ch.statusMessage?.includes('未开播') ? `<span style="color: var(--danger); margin-left: auto; font-size: 11px;">(重试 ${ch.retryCount})</span>` : ''}
                </div>
            </div>

            <div class="channel-actions">
                <label class="switch-label" title="切换频道开启/关闭">
                    <input type="checkbox" ${ch.enable ? 'checked' : ''} onchange="toggleChannel('${ch.id}')">
                    <span class="switch-slider"></span>
                </label>

                <div class="action-btns">
                    ${previewBtnHtml}
                    <button class="btn-icon" onclick="restartChannel('${ch.id}')" title="重启推流进程">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="23 4 23 10 17 10"></polyline>
                            <polyline points="1 20 1 14 7 14"></polyline>
                            <path d="M3.51 9a9 9 0 0 1 14.85-3.36L23 10M1 14l4.64 4.36A9 9 0 0 0 20.49 15"></path>
                        </svg>
                    </button>
                    <button class="btn-icon" onclick="openEditChannelModal('${ch.id}')" title="编辑频道">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"></path>
                            <path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"></path>
                        </svg>
                    </button>
                    <button class="btn-icon btn-danger-outline" onclick="deleteChannel('${ch.id}', '${escapeHtml(ch.name)}')" title="删除频道">
                        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="3 6 5 6 21 6"></polyline>
                            <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                        </svg>
                    </button>
                </div>
            </div>
        </div>
    `;
}

function getPlatformDisplayName(platform) {
    switch (platform?.toLowerCase()) {
        case 'huya': return '虎牙';
        case 'douyu': return '斗鱼';
        case 'bilibili': return 'B站';
        default: return platform || '未知';
    }
}

// -------------------------------------------------------------
// URL Parsing & Smart Platform Detection
// -------------------------------------------------------------

function detectPlatformFromUrl(rawUrl) {
    if (!rawUrl) return 'huya';
    const str = rawUrl.toLowerCase();
    if (str.includes('bilibili.com') || str.includes('b23.tv')) return 'bilibili';
    if (str.includes('douyu.com')) return 'douyu';
    if (str.includes('huya.com')) return 'huya';
    return 'huya';
}

function sanitizeUrl(rawUrl, platform) {
    if (!rawUrl) return '';
    let str = rawUrl.trim();

    if (platform === 'bilibili' || str.includes('bilibili.com') || str.includes('b23.tv')) {
        const m = str.match(/(?:live\.bilibili\.com\/|b23\.tv\/)(\d+)/i);
        if (m) return `https://live.bilibili.com/${m[1]}`;
    } else if (platform === 'douyu' || str.includes('douyu.com')) {
        const mRid = str.match(/rid=(\d+)/i);
        const mNum = str.match(/douyu\.com\/(\d+)/i);
        if (mRid) return `https://www.douyu.com/${mRid[1]}`;
        if (mNum) return `https://www.douyu.com/${mNum[1]}`;
    } else if (platform === 'huya' || str.includes('huya.com')) {
        const m = str.match(/huya\.com\/([a-zA-Z0-9_-]+)/i);
        if (m) return `https://www.huya.com/${m[1]}`;
    }

    // 默认剥离 query 参数
    return str.split('?')[0];
}

function updatePlatformUi(platform) {
    document.getElementById('channel-platform').value = platform;
    const badge = document.getElementById('detected-platform-badge');
    const cookieText = document.getElementById('channel-cookie-status-text');
    const cookieBox = document.getElementById('channel-cookie-status-display');

    const hasCookie = appState.config?.cookieProfiles?.[platform]?.trim()?.length > 0;
    const pName = getPlatformDisplayName(platform);

    if (badge) {
        badge.style.display = 'inline-flex';
        badge.className = `detected-platform-tag tag-${platform}`;
        badge.innerHTML = `🏷️ 已识别平台：<strong>${pName}直播</strong> (链接已自动规范化)`;
    }

    if (cookieText && cookieBox) {
        if (hasCookie) {
            cookieBox.className = 'static-field-display active';
            cookieText.innerHTML = `🟢 自动匹配<strong>【${pName}】Cookie (已配置)</strong>`;
        } else {
            cookieBox.className = 'static-field-display';
            cookieText.innerHTML = `⚪ 免登录模式 (可在平台 Cookie 中配置)`;
        }
    }
}

function onUrlInput() {
    clearTimeout(appState.urlDebounceTimer);
    const urlInput = document.getElementById('channel-url');
    const rawVal = urlInput?.value?.trim() || '';

    if (!rawVal) {
        document.getElementById('detected-platform-badge').style.display = 'none';
        return;
    }

    const platform = detectPlatformFromUrl(rawVal);
    updatePlatformUi(platform);

    // 400ms 防抖自动清理 URL 并自动识别名称
    appState.urlDebounceTimer = setTimeout(() => {
        const clean = sanitizeUrl(rawVal, platform);
        if (clean && clean !== rawVal) {
            urlInput.value = clean;
        }
        autoFetchChannelInfo(true);
    }, 400);
}

function onUrlBlur() {
    const urlInput = document.getElementById('channel-url');
    const rawVal = urlInput?.value?.trim() || '';
    if (!rawVal) return;

    const platform = detectPlatformFromUrl(rawVal);
    const clean = sanitizeUrl(rawVal, platform);
    if (clean && clean !== rawVal) {
        urlInput.value = clean;
    }
    updatePlatformUi(platform);

    const nameInput = document.getElementById('channel-name');
    if (nameInput && !nameInput.value.trim()) {
        autoFetchChannelInfo(true);
    }
}

async function autoFetchChannelInfo(silent = false) {
    const urlInput = document.getElementById('channel-url');
    const rawUrl = urlInput?.value?.trim();
    const nameInput = document.getElementById('channel-name');
    const idInput = document.getElementById('channel-id');
    const btn = document.getElementById('btn-auto-fetch');

    if (!rawUrl) {
        if (!silent) showToast('请先输入直播间链接或房间号', 'error');
        return;
    }

    const platform = detectPlatformFromUrl(rawUrl);
    updatePlatformUi(platform);

    if (btn) {
        btn.innerText = '⏳ 识别中...';
        btn.disabled = true;
    }

    try {
        const res = await fetch(`/api/channels/fetch-info?platform=${encodeURIComponent(platform)}&url=${encodeURIComponent(rawUrl)}`);
        if (res.ok) {
            const data = await res.json();
            if (data.cleanUrl && urlInput) {
                urlInput.value = data.cleanUrl;
            }
            if (data.platform) {
                updatePlatformUi(data.platform);
            }
            if (data.name && nameInput) {
                nameInput.value = data.name;
                nameInput.classList.add('highlight-flash');
                setTimeout(() => nameInput.classList.remove('highlight-flash'), 1000);
            }
            if (data.suggestedId && idInput && !idInput.disabled && (!idInput.value || !appState.editingChannelId)) {
                idInput.value = data.suggestedId;
                idInput.classList.add('highlight-flash');
                setTimeout(() => idInput.classList.remove('highlight-flash'), 1000);
            }
            if (!silent && data.success) {
                showToast(`已识别：${data.name}`, 'success');
            }
        }
    } catch (err) {
        if (!silent) showToast('识别失败: ' + err.message, 'error');
    } finally {
        if (btn) {
            btn.innerText = '✨ 自动识别';
            btn.disabled = false;
        }
    }
}

// -------------------------------------------------------------
// Channel Add / Edit / Delete / Toggle
// -------------------------------------------------------------

function openAddChannelModal() {
    appState.editingChannelId = null;
    document.getElementById('modal-channel-title').innerText = '添加直播频道';
    document.getElementById('channel-id').value = '';
    document.getElementById('channel-id').disabled = false;
    document.getElementById('channel-name').value = '';
    document.getElementById('channel-url').value = '';
    document.getElementById('channel-quality').value = 'OD';
    document.getElementById('channel-enable').checked = true;
    document.getElementById('detected-platform-badge').style.display = 'none';

    updatePlatformUi('huya');
    openModal('modal-channel');
}

function openEditChannelModal(id) {
    const channel = appState.config?.channels?.find(c => c.id === id) || 
                    appState.status?.channels?.find(c => c.id === id);
    if (!channel) return;

    appState.editingChannelId = id;
    document.getElementById('modal-channel-title').innerText = '编辑直播频道';
    document.getElementById('channel-id').value = channel.id;
    document.getElementById('channel-id').disabled = true; // ID 不可修改
    document.getElementById('channel-name').value = channel.name;
    document.getElementById('channel-url').value = channel.url;
    document.getElementById('channel-quality').value = channel.quality || 'OD';
    document.getElementById('channel-enable').checked = channel.enable !== false;

    const platform = channel.platform?.toLowerCase() || detectPlatformFromUrl(channel.url);
    updatePlatformUi(platform);

    openModal('modal-channel');
}

async function saveChannel(event) {
    event.preventDefault();

    const id = document.getElementById('channel-id').value.trim();
    const name = document.getElementById('channel-name').value.trim();
    const platform = document.getElementById('channel-platform').value || 'huya';
    let url = document.getElementById('channel-url').value.trim();
    const quality = document.getElementById('channel-quality').value;
    const enable = document.getElementById('channel-enable').checked;

    url = sanitizeUrl(url, platform);

    const payload = {
        id: appState.editingChannelId || id,
        name,
        platform,
        url,
        quality,
        cookieProfileKey: platform,
        enable
    };

    try {
        const res = await fetch('/api/channels', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        });

        if (res.ok) {
            showToast(appState.editingChannelId ? '频道已更新' : '频道已添加并开始推流', 'success');
            closeModal('modal-channel');
            await loadConfig();
            await loadStatus();
        } else {
            const err = await res.json();
            showToast(err.error || '保存失败', 'error');
        }
    } catch (err) {
        showToast('网络请求失败: ' + err.message, 'error');
    }
}

async function deleteChannel(id, name) {
    if (!confirm(`确定要删除频道 "${name}" 吗？此操作不可恢复。`)) return;

    try {
        const res = await fetch(`/api/channels/${encodeURIComponent(id)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            showToast(`频道 "${name}" 已删除`, 'success');
            await loadConfig();
            await loadStatus();
        } else {
            showToast('删除失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function toggleChannel(id) {
    try {
        const res = await fetch(`/api/channels/${encodeURIComponent(id)}/toggle`, {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            showToast(data.enable ? '已启用频道推流' : '已停用频道推流', 'success');
            await loadConfig();
            await loadStatus();
        }
    } catch (err) {
        showToast('切换状态失败', 'error');
    }
}

async function restartChannel(id) {
    try {
        const res = await fetch(`/api/channels/${encodeURIComponent(id)}/restart`, {
            method: 'POST'
        });

        if (res.ok) {
            showToast('已触发推流进程重启', 'success');
            await loadStatus();
        }
    } catch (err) {
        showToast('重启失败', 'error');
    }
}

// -------------------------------------------------------------
// Platform Cookie Management (固定三大平台)
// -------------------------------------------------------------

function openCookieModal() {
    renderPlatformCookieCards();
    openModal('modal-cookies');
}

function renderPlatformCookieCards() {
    const profiles = appState.config?.cookieProfiles || {};
    const platforms = ['huya', 'douyu', 'bilibili'];

    platforms.forEach(p => {
        const val = profiles[p] || '';
        const textarea = document.getElementById(`cookie-${p}`);
        const tag = document.getElementById(`${p}-cookie-tag`);
        const card = document.getElementById(`card-cookie-${p}`);

        if (textarea) textarea.value = val;

        if (tag) {
            if (val && val.trim().length > 0) {
                tag.className = 'cookie-status-tag active';
                tag.innerText = `已配置 (${val.trim().length} 字符)`;
                card?.classList.add('configured');
            } else {
                tag.className = 'cookie-status-tag';
                tag.innerText = '未配置 (免登录)';
                card?.classList.remove('configured');
            }
        }
    });
}

async function savePlatformCookie(platform) {
    const textarea = document.getElementById(`cookie-${platform}`);
    const cookie = textarea?.value?.trim() || '';
    const pName = getPlatformDisplayName(platform);

    try {
        const res = await fetch('/api/cookies', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ key: platform, cookie })
        });

        if (res.ok) {
            showToast(`${pName} Cookie 已保存并生效`, 'success');
            await loadConfig();
            renderPlatformCookieCards();
        } else {
            showToast('保存 Cookie 失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function clearPlatformCookie(platform) {
    const pName = getPlatformDisplayName(platform);
    if (!confirm(`确定要清空 ${pName} 的 Cookie 吗？关联此平台的频道将自动降级为免登录模式。`)) return;

    try {
        const res = await fetch(`/api/cookies/${encodeURIComponent(platform)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            showToast(`已清空 ${pName} Cookie`, 'success');
            await loadConfig();
            renderPlatformCookieCards();
        } else {
            showToast('清空失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

// -------------------------------------------------------------
// Live Preview Player (Hls.js)
// -------------------------------------------------------------

function openPreviewModal(id, name, relativeHlsUrl) {
    const video = document.getElementById('preview-video');
    const title = document.getElementById('preview-title');
    const urlDisplay = document.getElementById('preview-hls-url');
    const errorMsg = document.getElementById('player-error-msg');

    title.innerText = `试播：${name}`;
    errorMsg.style.display = 'none';

    const fullUrl = window.location.origin + relativeHlsUrl;
    urlDisplay.innerText = fullUrl;

    openModal('modal-preview');

    if (Hls.isSupported()) {
        if (appState.hlsPlayer) {
            appState.hlsPlayer.destroy();
        }
        const hls = new Hls({
            enableWorker: true,
            lowLatencyMode: true,
            maxBufferLength: 10,
            liveSyncDurationCount: 2
        });
        appState.hlsPlayer = hls;

        hls.loadSource(fullUrl);
        hls.attachMedia(video);

        hls.on(Hls.Events.MANIFEST_PARSED, () => {
            video.play().catch(() => {});
        });

        hls.on(Hls.Events.ERROR, (event, data) => {
            if (data.fatal) {
                errorMsg.innerText = `播放出错: ${data.details} (流可能尚未生成完毕或正在启动)`;
                errorMsg.style.display = 'block';
            }
        });
    } else if (video.canPlayType('application/vnd.apple.mpegurl')) {
        // Native Safari / iOS HLS support
        video.src = fullUrl;
        video.addEventListener('loadedmetadata', () => {
            video.play().catch(() => {});
        });
    } else {
        errorMsg.innerText = '当前浏览器不支持 HLS 视频直接播放，请复制链接到外部播放器观看。';
        errorMsg.style.display = 'block';
    }
}

function closePreviewModal() {
    const video = document.getElementById('preview-video');
    if (video) {
        video.pause();
        video.src = '';
    }
    if (appState.hlsPlayer) {
        appState.hlsPlayer.destroy();
        appState.hlsPlayer = null;
    }
    closeModal('modal-preview');
}

// -------------------------------------------------------------
// Utilities & Modals
// -------------------------------------------------------------

function openModal(id) {
    document.getElementById(id)?.classList.add('active');
}

function closeModal(id) {
    document.getElementById(id)?.classList.remove('active');
}

function copyM3uUrl() {
    const url = appState.status?.m3uUrl || `${window.location.origin}/jellyfin.m3u`;
    navigator.clipboard.writeText(url).then(() => {
        showToast('M3U 订阅源地址已复制到剪贴板！', 'success');
    }).catch(() => {
        showToast('复制失败，请手动复制: ' + url, 'error');
    });
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.innerHTML = `
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2">
            ${type === 'success' ? '<polyline points="20 6 9 17 4 12"></polyline>' : '<circle cx="12" cy="12" r="10"></circle><line x1="12" y1="8" x2="12" y2="12"></line><line x1="12" y1="16" x2="12.01" y2="16"></line>'}
        </svg>
        <span>${escapeHtml(message)}</span>
    `;

    container.appendChild(toast);

    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(15px)';
        toast.style.transition = 'all 0.25s ease';
        setTimeout(() => toast.remove(), 250);
    }, 3000);
}

function escapeHtml(str) {
    if (!str) return '';
    return String(str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#039;');
}
