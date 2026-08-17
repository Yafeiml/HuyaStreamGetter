// ==========================================================
// HuyaStreamGateway - Web Management Controller
// ==========================================================

let appState = {
    status: null,
    config: null,
    editingChannelId: null,
    hlsPlayer: null,
    pollTimer: null
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
    
    // Manual Refresh Button
    document.getElementById('btn-manual-refresh')?.addEventListener('click', () => {
        loadStatus(true);
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
            updateCookieSelectOptions();
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

    const cookieCount = appState.config?.cookieProfiles ? Object.keys(appState.config.cookieProfiles).length : 0;
    document.getElementById('stat-cookie-count').innerText = `${cookieCount} 个配置`;

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

    if (!ch.enable) {
        statusBadgeClass = 'disabled';
        statusText = '已禁用';
    } else if (ch.isLive) {
        statusBadgeClass = 'live';
        statusText = '🟢 推流中';
    } else if (ch.statusMessage?.includes('失败') || ch.statusMessage?.includes('错误')) {
        statusBadgeClass = 'error';
        statusText = '🔴 ' + ch.statusMessage;
    }

    const boundCookieText = ch.cookieProfileKey 
        ? `<span title="${ch.cookieProfileKey}">${ch.cookieProfileKey}</span>` 
        : '<span style="color: var(--text-muted);">免登录</span>';

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
                        <span class="info-label">绑定 Cookie:</span>
                        <span class="info-value">${boundCookieText}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">源链接:</span>
                        <span class="info-value" title="${escapeHtml(ch.url)}">${escapeHtml(ch.url)}</span>
                    </div>
                </div>

                <div class="status-msg-box" title="${escapeHtml(ch.statusMessage)}">
                    <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2">
                        <circle cx="12" cy="12" r="10"></circle>
                        <line x1="12" y1="16" x2="12" y2="12"></line>
                        <line x1="12" y1="8" x2="12.01" y2="8"></line>
                    </svg>
                    <span style="overflow: hidden; text-overflow: ellipsis; white-space: nowrap;">${escapeHtml(ch.statusMessage)}</span>
                    ${ch.retryCount > 0 ? `<span style="color: var(--danger); margin-left: auto;">(重试 ${ch.retryCount})</span>` : ''}
                </div>
            </div>

            <div class="channel-actions">
                <label class="switch-label" title="切换频道开启/关闭">
                    <input type="checkbox" ${ch.enable ? 'checked' : ''} onchange="toggleChannel('${ch.id}')">
                    <span class="switch-slider"></span>
                </label>

                <div class="action-btns">
                    <button class="btn btn-sm btn-secondary" onclick="openPreviewModal('${ch.id}', '${escapeHtml(ch.name)}', '${ch.hlsUrl}')" title="在网页中实时试播">
                        <svg viewBox="0 0 24 24" width="13" height="13" fill="currentColor">
                            <polygon points="5 3 19 12 5 21 5 3"></polygon>
                        </svg>
                        <span>试播</span>
                    </button>
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
// Channel Add / Edit / Delete / Toggle
// -------------------------------------------------------------

function openAddChannelModal() {
    appState.editingChannelId = null;
    document.getElementById('modal-channel-title').innerText = '添加直播频道';
    document.getElementById('channel-id').value = '';
    document.getElementById('channel-id').disabled = false;
    document.getElementById('channel-name').value = '';
    document.getElementById('channel-platform').value = 'huya';
    document.getElementById('channel-url').value = '';
    document.getElementById('channel-quality').value = 'OD';
    document.getElementById('channel-cookie-key').value = '';
    document.getElementById('channel-enable').checked = true;

    updateCookieSelectOptions();
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
    document.getElementById('channel-platform').value = channel.platform?.toLowerCase() || 'huya';
    document.getElementById('channel-url').value = channel.url;
    document.getElementById('channel-quality').value = channel.quality || 'OD';
    document.getElementById('channel-enable').checked = channel.enable !== false;

    updateCookieSelectOptions(channel.cookieProfileKey);
    openModal('modal-channel');
}

function onPlatformChange() {
    const platform = document.getElementById('channel-platform').value;
    const urlInput = document.getElementById('channel-url');
    if (platform === 'huya') urlInput.placeholder = 'https://www.huya.com/eslcs 或房间号';
    else if (platform === 'douyu') urlInput.placeholder = 'https://www.douyu.com/9999 或房间号';
    else if (platform === 'bilibili') urlInput.placeholder = 'https://live.bilibili.com/6 或房间号';
}

async function saveChannel(event) {
    event.preventDefault();

    const id = document.getElementById('channel-id').value.trim();
    const name = document.getElementById('channel-name').value.trim();
    const platform = document.getElementById('channel-platform').value;
    const url = document.getElementById('channel-url').value.trim();
    const quality = document.getElementById('channel-quality').value;
    const cookieProfileKey = document.getElementById('channel-cookie-key').value;
    const enable = document.getElementById('channel-enable').checked;

    const payload = {
        id: appState.editingChannelId || id,
        name,
        platform,
        url,
        quality,
        cookieProfileKey: cookieProfileKey || null,
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
// Cookie Profiles Management
// -------------------------------------------------------------

function openCookieModal() {
    renderCookieList();
    document.getElementById('cookie-key').value = '';
    document.getElementById('cookie-value').value = '';
    openModal('modal-cookies');
}

function renderCookieList() {
    const listContainer = document.getElementById('cookie-list-container');
    const profiles = appState.config?.cookieProfiles || {};
    const keys = Object.keys(profiles);

    if (keys.length === 0) {
        listContainer.innerHTML = '<div style="color: var(--text-muted); font-size: 13px; text-align: center; padding: 12px;">暂无配置 Cookie 凭据</div>';
        return;
    }

    listContainer.innerHTML = keys.map(key => {
        const val = profiles[key] || '';
        const len = val.length;
        return `
            <div class="cookie-item">
                <div>
                    <div class="cookie-item-key">🔑 ${escapeHtml(key)}</div>
                    <div class="cookie-item-len">长度: ${len} 字符 (${len > 20 ? '已配置' : '未填充'})</div>
                </div>
                <div class="cookie-item-actions">
                    <button class="btn btn-sm btn-secondary" onclick="editCookie('${escapeHtml(key)}')">编辑</button>
                    <button class="btn btn-sm btn-danger-outline" onclick="deleteCookie('${escapeHtml(key)}')">删除</button>
                </div>
            </div>
        `;
    }).join('');
}

function editCookie(key) {
    const val = appState.config?.cookieProfiles?.[key] || '';
    document.getElementById('cookie-key').value = key;
    document.getElementById('cookie-value').value = val;
    document.getElementById('cookie-value').focus();
}

async function saveCookie(event) {
    event.preventDefault();
    const key = document.getElementById('cookie-key').value.trim();
    const cookie = document.getElementById('cookie-value').value.trim();

    if (!key) {
        showToast('Cookie 标识 Key 不能为空', 'error');
        return;
    }

    try {
        const res = await fetch('/api/cookies', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ key, cookie })
        });

        if (res.ok) {
            showToast(`Cookie Profile "${key}" 已保存`, 'success');
            await loadConfig();
            renderCookieList();
            document.getElementById('cookie-key').value = '';
            document.getElementById('cookie-value').value = '';
        } else {
            showToast('保存 Cookie 失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function deleteCookie(key) {
    if (!confirm(`确定要删除 Cookie 配置 "${key}" 吗？关联此配置的频道将变为免登录模式。`)) return;

    try {
        const res = await fetch(`/api/cookies/${encodeURIComponent(key)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            showToast(`Cookie Profile "${key}" 已删除`, 'success');
            await loadConfig();
            renderCookieList();
        }
    } catch (err) {
        showToast('删除失败: ' + err.message, 'error');
    }
}

function updateCookieSelectOptions(selectedKey = null) {
    const select = document.getElementById('channel-cookie-key');
    if (!select) return;

    const profiles = appState.config?.cookieProfiles || {};
    const keys = Object.keys(profiles);

    select.innerHTML = '<option value="">-- 免登录模式 --</option>' + 
        keys.map(k => `<option value="${escapeHtml(k)}" ${k === selectedKey ? 'selected' : ''}>${escapeHtml(k)}</option>`).join('');
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
