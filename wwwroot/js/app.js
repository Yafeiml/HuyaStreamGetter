// ==========================================================
// LiveStreamGateway - Web Management Controller
// ==========================================================

let appState = {
    status: null,
    config: null,
    cookieStatuses: {},
    cookieEditors: { huya: false, douyu: false, bilibili: false },
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
        const btn = document.getElementById('btn-copy-stream-url');
        if (url) {
            copyTextToClipboard(url, 'HLS 播放链接', btn);
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
            const data = await res.json();
            appState.config = data;
            if (data.cookieStatuses) {
                appState.cookieStatuses = data.cookieStatuses;
            }
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
            const data = await res.json();
            appState.status = data;
            if (data.cookieStatuses) {
                appState.cookieStatuses = data.cookieStatuses;
            }
            renderDashboard();
            updateCookieStatusSummary();
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
    const statuses = appState.cookieStatuses || {};
    const platforms = ['huya', 'douyu', 'bilibili'];

    let hasExpired = false;
    let configuredCount = 0;

    platforms.forEach(p => {
        const hasVal = profiles[p] && profiles[p].trim().length > 0;
        if (hasVal) {
            configuredCount++;
            if (statuses[p] && statuses[p].isValid === false) {
                hasExpired = true;
            }
        }
    });

    const summaryEl = document.getElementById('stat-cookie-count');
    if (summaryEl) {
        if (hasExpired) {
            summaryEl.innerHTML = '<span style="color: var(--danger); font-weight: 600;">⚠️ 存在失效 Cookie</span>';
        } else if (configuredCount === 3) {
            summaryEl.innerHTML = '<span style="color: var(--success);">全部已授权 (有效)</span>';
        } else if (configuredCount > 0) {
            summaryEl.innerHTML = `<span style="color: var(--success);">${configuredCount} 个平台已授权</span>`;
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
    
    // 局域网中继端点：优先显示配置的 customHost，其次显示后端识别的 displayHost，最后显示 window.location.host
    let activeHost = appState.config?.customHost || s.displayHost || window.location.host;
    if (activeHost && !activeHost.includes(':') && s.httpPort) {
        activeHost = `${activeHost}:${s.httpPort}`;
    }
    document.getElementById('stat-local-ip').innerText = activeHost || `${s.localIp}:${s.httpPort}`;
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
    let statusPillHtml = '';

    const statusTitle = ch.retryCount > 0 
        ? `${ch.statusMessage || ''} (重试 ${ch.retryCount} 次)`
        : (ch.statusMessage || '');

    // 状态分类判断与右上角小动画
    if (!ch.enable) {
        statusBadgeClass = 'disabled';
        statusPillHtml = `<span class="status-dot-pulse disabled"></span><span>已停用</span>`;
    } else if (ch.isLive) {
        statusBadgeClass = 'live';
        statusPillHtml = `
            <span class="live-wave-anim" title="正在实时推流">
                <span class="bar"></span>
                <span class="bar"></span>
                <span class="bar"></span>
            </span>
            <span>推流中</span>
        `;
    } else if (ch.statusMessage?.includes('未开播')) {
        statusBadgeClass = 'waiting';
        statusPillHtml = `<span class="status-dot-pulse waiting"></span><span>未开播</span>`;
    } else if (ch.statusMessage?.includes('Cookie') || ch.statusMessage?.includes('登录')) {
        statusBadgeClass = 'error';
        statusPillHtml = `<span class="status-dot-pulse error"></span><span>Cookie失效</span>`;
    } else {
        statusBadgeClass = 'error';
        const errShort = (ch.statusMessage && ch.statusMessage.length > 8) ? '异常' : (ch.statusMessage || '异常');
        statusPillHtml = `<span class="status-dot-pulse error"></span><span>${escapeHtml(errShort)}</span>`;
    }

    // 平台 Cookie 状态与真伪检测状态展示
    const platformKey = (ch.platform || 'huya').toLowerCase();
    const cookieVal = appState.config?.cookieProfiles?.[platformKey] || ch.cookies || '';
    const hasPlatformCookie = cookieVal.trim().length > 0;
    const cookieStatus = appState.cookieStatuses?.[platformKey] || {
        configured: ch.isCookieConfigured ?? hasPlatformCookie,
        isValid: ch.isCookieValid ?? true,
        isNetworkError: ch.isCookieNetworkError ?? false,
        username: ch.cookieUsername ?? '',
        message: ch.cookieStatusMessage ?? ''
    };

    let cookieDisplayText = `<span class="cookie-tag-inline muted">⚪ 免登录</span>`;
    if (hasPlatformCookie) {
        if (cookieStatus.isValid === false && !cookieStatus.isNetworkError) {
            // 明确确认过期，高亮红字
            cookieDisplayText = `<span class="cookie-tag-inline expired" title="${escapeHtml(cookieStatus.message || 'Cookie已失效')}">🔴 已过期</span>`;
        } else if (cookieStatus.isNetworkError) {
            // 网络检测异常，保持有效授权状态
            cookieDisplayText = `<span class="cookie-tag-inline active" title="${escapeHtml(cookieStatus.message || '网络检测波动，维持授权状态')}">🟢 已授权</span>`;
        } else {
            cookieDisplayText = `<span class="cookie-tag-inline active" title="${escapeHtml(cookieStatus.message || 'Cookie有效并已授权')}">🟢 已授权</span>`;
        }
    }

    // 试播按钮可用状态 (只有推流中才能试播)
    // 试播纯图标按钮 (与右侧重启/编辑/删除保持一致)
    const canPreview = ch.isLive;
    const previewBtnHtml = canPreview
        ? `<button class="btn-icon" onclick="openPreviewModal('${ch.id}', '${escapeHtml(ch.name)}', '${ch.hlsUrl}')" title="试播">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor">
                    <polygon points="6 3 20 12 6 21 6 3"></polygon>
                </svg>
           </button>`
        : `<button class="btn-icon btn-disabled" disabled title="试播 (当前未推流)">
                <svg viewBox="0 0 24 24" width="14" height="14" fill="currentColor">
                    <polygon points="6 3 20 12 6 21 6 3"></polygon>
                </svg>
           </button>`;

    return `
        <div class="channel-card ${ch.enable ? '' : 'disabled'}" id="card-${ch.id}">
            <div>
                <div class="channel-header">
                    <div class="channel-title-wrap">
                        <span class="platform-pill ${platformClass}">${platformName}</span>
                        <h3 class="channel-name" title="${escapeHtml(ch.name)}">${escapeHtml(ch.name)}</h3>
                    </div>
                    <span class="status-pill ${statusBadgeClass}" title="${escapeHtml(statusTitle)}">${statusPillHtml}</span>
                </div>

                <div class="channel-info-list">
                    <div class="info-item">
                        <span class="info-label">频道 ID:</span>
                        <span class="info-value">${escapeHtml(ch.id)}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">画质等级:</span>
                        <span class="info-value">${escapeHtml(ch.quality || 'OD')}</span>
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

function updatePlatformUi(platform, isUserUrlPresent = false) {
    if (platform) {
        document.getElementById('channel-platform').value = platform;
    }
    const badge = document.getElementById('detected-platform-badge');
    const cookieText = document.getElementById('channel-cookie-status-text');
    const cookieBox = document.getElementById('channel-cookie-status-display');

    if (!isUserUrlPresent || !platform) {
        // 未填入链接时的初始空状态
        if (badge) badge.style.display = 'none';
        if (cookieText && cookieBox) {
            cookieBox.className = 'static-field-display';
            cookieText.innerHTML = '等待输入链接...';
        }
        return;
    }

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
            cookieText.innerHTML = `<strong>已授权</strong> (已自动绑定)`;
        } else {
            cookieBox.className = 'static-field-display';
            cookieText.innerHTML = `免登录模式 (可在平台Cookie中配置)`;
        }
    }
}

function onUrlInput() {
    clearTimeout(appState.urlDebounceTimer);
    const urlInput = document.getElementById('channel-url');
    const rawVal = urlInput?.value?.trim() || '';

    if (!rawVal) {
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawVal);
    updatePlatformUi(platform, true);

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
    if (!rawVal) {
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawVal);
    const clean = sanitizeUrl(rawVal, platform);
    if (clean && clean !== rawVal) {
        urlInput.value = clean;
    }
    updatePlatformUi(platform, true);

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
        updatePlatformUi(null, false);
        return;
    }

    const platform = detectPlatformFromUrl(rawUrl);
    updatePlatformUi(platform, true);

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
                updatePlatformUi(data.platform, true);
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

    // 默认空状态，不显示任何平台识别或Cookie匹配标签
    updatePlatformUi(null, false);
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
    updatePlatformUi(platform, true);

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
    const confirmed = await showConfirm({
        title: '删除直播频道',
        message: `确定要删除频道 "${name}" 吗？此操作不可恢复。`,
        okText: '确定删除',
        cancelText: '取消',
        type: 'danger'
    });
    if (!confirmed) return;

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

function togglePlatformCookieEdit(platform, forceState = null) {
    const editor = document.getElementById(`editor-cookie-${platform}`);
    const btnEdit = document.getElementById(`btn-edit-${platform}`);
    if (!editor) return;

    const isCurrentlyOpen = editor.style.display !== 'none';
    const nextState = forceState !== null ? forceState : !isCurrentlyOpen;

    editor.style.display = nextState ? 'flex' : 'none';
    appState.cookieEditors[platform] = nextState;

    if (btnEdit) {
        btnEdit.innerText = nextState ? '🔼 收起' : '✏️ 编辑';
    }

    if (nextState) {
        const textarea = document.getElementById(`cookie-${platform}`);
        textarea?.focus();
    }
}

function renderPlatformCookieCards() {
    const profiles = appState.config?.cookieProfiles || {};
    const statuses = appState.cookieStatuses || {};
    const platforms = ['huya', 'douyu', 'bilibili'];

    platforms.forEach(p => {
        const val = profiles[p] || '';
        const textarea = document.getElementById(`cookie-${p}`);
        const tag = document.getElementById(`${p}-cookie-tag`);
        const card = document.getElementById(`card-cookie-${p}`);
        const btnClear = document.getElementById(`btn-clear-${p}`);
        const btnVerify = document.getElementById(`btn-verify-${p}`);
        const status = statuses[p];

        if (textarea && document.activeElement !== textarea) {
            textarea.value = val;
        }

        const isConfigured = val && val.trim().length > 0;
        if (card) {
            if (isConfigured) card.classList.add('configured');
            else card.classList.remove('configured');
        }

        if (btnClear) {
            btnClear.style.display = isConfigured ? 'inline-block' : 'none';
        }
        if (btnVerify) {
            btnVerify.style.display = isConfigured ? 'inline-block' : 'none';
        }

        if (tag) {
            if (!isConfigured) {
                tag.className = 'cookie-badge-status';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">未配置 (免登录模式)</span>`;
            } else if (status && status.isValid === true) {
                tag.className = 'cookie-badge-status valid';
                const userText = status.username ? `已授权: ${status.username}` : (status.message || '已授权有效');
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">${escapeHtml(userText)} (${val.trim().length} 字符)</span>`;
            } else if (status && status.isNetworkError) {
                tag.className = 'cookie-badge-status valid';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已配置 (网络波动，保持状态) (${val.trim().length} 字符)</span>`;
            } else if (status && status.isValid === false) {
                tag.className = 'cookie-badge-status expired';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已过期: ${escapeHtml(status.message || '账号未登录')} (${val.trim().length} 字符)</span>`;
            } else {
                tag.className = 'cookie-badge-status';
                tag.innerHTML = `<span class="status-dot"></span><span class="status-text">已配置 (${val.trim().length} 字符)</span>`;
            }
        }
    });
}

async function verifyPlatformCookie(platform) {
    const btn = document.getElementById(`btn-verify-${platform}`);
    const tag = document.getElementById(`${platform}-cookie-tag`);
    const pName = getPlatformDisplayName(platform);

    if (btn) {
        btn.disabled = true;
        btn.innerText = '⏳ 检测中...';
    }
    if (tag) {
        tag.className = 'cookie-badge-status checking';
        tag.innerHTML = `<span class="status-dot"></span><span class="status-text">正在鉴权检测...</span>`;
    }

    try {
        const res = await fetch(`/api/cookies/verify?platform=${encodeURIComponent(platform)}`, {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            renderPlatformCookieCards();
            renderDashboard();
            updateCookieStatusSummary();

            const st = data.status || appState.cookieStatuses[platform];
            if (st?.isValid && !st?.isNetworkError) {
                showToast(`${pName} Cookie 检测通过 (${st.username ? '用户: ' + st.username : st.message})`, 'success');
            } else if (st?.isNetworkError) {
                showToast(`${pName} Cookie 检测提示: ${st.message}，保持当前授权状态`, 'warning');
            } else {
                showToast(`${pName} Cookie 检测失败: ${st?.message || '已失效'}`, 'error');
            }
        } else {
            showToast(`${pName} 检测请求失败`, 'error');
            renderPlatformCookieCards();
        }
    } catch (err) {
        showToast('检测请求异常: ' + err.message, 'error');
        renderPlatformCookieCards();
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerText = '🔍 检测';
        }
    }
}

async function verifyAllPlatformCookies() {
    showToast('正在检测全部平台 Cookie 状态...', 'info');
    try {
        const res = await fetch('/api/cookies/verify?platform=all', {
            method: 'POST'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            renderPlatformCookieCards();
            renderDashboard();
            updateCookieStatusSummary();
            showToast('已完成全部平台有效性检测', 'success');
        } else {
            showToast('检测失败', 'error');
        }
    } catch (err) {
        showToast('检测请求失败: ' + err.message, 'error');
    }
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
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            togglePlatformCookieEdit(platform, false);
            await loadConfig();
            await loadStatus();
            renderPlatformCookieCards();

            const st = data.status || appState.cookieStatuses[platform];
            if (st?.isValid) {
                showToast(`${pName} Cookie 已保存并验证通过 (${st.username || st.message})`, 'success');
            } else if (cookie.length > 0) {
                showToast(`${pName} Cookie 已保存，但检测提示: ${st?.message || '可能已失效'}`, 'warning');
            } else {
                showToast(`${pName} Cookie 已清空 (免登录)`, 'success');
            }
        } else {
            showToast('保存 Cookie 失败', 'error');
        }
    } catch (err) {
        showToast('请求失败: ' + err.message, 'error');
    }
}

async function clearPlatformCookie(platform) {
    const pName = getPlatformDisplayName(platform);
    const confirmed = await showConfirm({
        title: `清空 ${pName} Cookie`,
        message: `确定要清空 ${pName} 的 Cookie 凭据吗？关联此平台的频道将自动降级为免登录模式。`,
        okText: '确定清空',
        cancelText: '取消',
        type: 'danger'
    });
    if (!confirmed) return;

    try {
        const res = await fetch(`/api/cookies/${encodeURIComponent(platform)}`, {
            method: 'DELETE'
        });

        if (res.ok) {
            const data = await res.json();
            if (data.statuses) {
                appState.cookieStatuses = data.statuses;
            }
            togglePlatformCookieEdit(platform, false);
            showToast(`已清空 ${pName} Cookie`, 'success');
            await loadConfig();
            await loadStatus();
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

async function copyTextToClipboard(text, label = '内容', buttonEl = null) {
    if (!text) return;
    let success = false;

    // 1. 尝试现代 Clipboard API (HTTPS 或 localhost 环境)
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            success = true;
        } catch (err) {
            console.warn('navigator.clipboard.writeText 失败，尝试降级兼容方案:', err);
        }
    }

    // 2. 兼容降级方案 (适用于纯 HTTP 局域网 IP 如 192.168.x.x 等环境)
    if (!success) {
        try {
            const textArea = document.createElement('textarea');
            textArea.value = text;
            textArea.style.position = 'fixed';
            textArea.style.left = '-9999px';
            textArea.style.top = '-9999px';
            textArea.setAttribute('readonly', '');
            document.body.appendChild(textArea);
            textArea.select();
            textArea.setSelectionRange(0, 99999);
            success = document.execCommand('copy');
            document.body.removeChild(textArea);
        } catch (err) {
            console.error('execCommand 复制失败:', err);
        }
    }

    if (success) {
        showToast(`${label}已复制到剪贴板！`, 'success');
        if (buttonEl) {
            const originalHtml = buttonEl.innerHTML;
            const originalClass = buttonEl.className;
            buttonEl.classList.add('btn-copied-success');
            buttonEl.innerHTML = `
                <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2.5">
                    <polyline points="20 6 9 17 4 12"></polyline>
                </svg>
                <span>已复制！</span>
            `;
            setTimeout(() => {
                buttonEl.innerHTML = originalHtml;
                buttonEl.className = originalClass;
            }, 2000);
        }
    } else {
        // 极端受限环境下的终极弹窗复制引导
        prompt(`无法直接写入剪贴板，请按 Ctrl+C / Cmd+C 复制 ${label}：`, text);
    }
}

async function detectBrowserCandidateIps() {
    return new Promise((resolve) => {
        const ips = new Set();
        try {
            const RTCPeer = window.RTCPeerConnection || window.webkitRTCPeerConnection || window.mozRTCPeerConnection;
            if (!RTCPeer) return resolve([]);
            const pc = new RTCPeer({ iceServers: [] });
            pc.createDataChannel('');
            pc.createOffer().then(offer => pc.setLocalDescription(offer)).catch(() => {});
            pc.onicecandidate = (ice) => {
                if (!ice || !ice.candidate || !ice.candidate.candidate) return;
                const match = /([0-9]{1,3}(\.[0-9]{1,3}){3})/.exec(ice.candidate.candidate);
                if (match && match[1]) {
                    const ip = match[1];
                    if (!ip.startsWith('127.') && !ip.startsWith('172.17.') && !ip.startsWith('172.18.') && !ip.startsWith('172.20.')) {
                        ips.add(ip);
                    }
                }
            };
            setTimeout(() => {
                try { pc.close(); } catch (_) {}
                resolve(Array.from(ips));
            }, 600);
        } catch (_) {
            resolve([]);
        }
    });
}

async function openHostModal(defaultFill = '') {
    const input = document.getElementById('input-custom-host');
    const hintBox = document.getElementById('host-candidate-box');
    
    if (input) {
        input.value = appState.config?.customHost || defaultFill || '';
    }

    openModal('modal-host');

    // 自动检测本机局域网候选 IP 并在下方显示一键填充标签
    if (hintBox) {
        hintBox.innerHTML = '<span>🔍 正在探测本机局域网 IP...</span>';
        const detected = await detectBrowserCandidateIps();
        if (detected.length > 0) {
            hintBox.innerHTML = `
                <div style="margin-top: 8px;">
                    <span>💡 检测到本机可能 IP：</span>
                    ${detected.map(ip => `
                        <button type="button" class="btn btn-sm btn-secondary" style="padding: 2px 8px; font-size: 11px; margin: 2px 4px 2px 0;" onclick="selectCandidateHost('${ip}')">
                            ${ip}
                        </button>
                    `).join('')}
                </div>
            `;
            if (input && !input.value) {
                input.value = detected[0];
            }
        } else {
            hintBox.innerHTML = `<span>💡 提示：输入当前电脑/NAS的局域网 IP (如 192.168.10.2)，局域网其它设备即可直连。</span>`;
        }
    }
}

function selectCandidateHost(ip) {
    const input = document.getElementById('input-custom-host');
    if (input) {
        input.value = ip;
        input.focus();
    }
}

async function saveCustomHost() {
    const input = document.getElementById('input-custom-host');
    const val = input ? input.value.trim() : '';
    const btn = document.getElementById('btn-save-host');

    if (btn) {
        btn.disabled = true;
        btn.innerText = '保存中...';
    }

    try {
        const res = await fetch('/api/config/host', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ customHost: val })
        });

        if (res.ok) {
            if (!appState.config) appState.config = {};
            appState.config.customHost = val;
            closeModal('modal-host');
            await loadStatus(false);
            showToast(val ? `已配置局域网主机: ${val}` : '已恢复自动识别主机', 'success');
        } else {
            showToast('保存失败，请稍后重试', 'error');
        }
    } catch (err) {
        showToast('保存异常: ' + err.message, 'error');
    } finally {
        if (btn) {
            btn.disabled = false;
            btn.innerText = '保存生效';
        }
    }
}

async function copyM3uUrl() {
    let url = appState.status?.m3uUrl || `${window.location.origin}/jellyfin.m3u`;
    
    // 如果当前通过 localhost / 127.0.0.1 访问，且配置中未设置 customHost，弹窗引导用户快速确认真实局域网 IP
    const isLocalhost = window.location.hostname === 'localhost' || window.location.hostname === '127.0.0.1';
    const hasCustomHost = appState.config?.customHost && appState.config.customHost.trim().length > 0;
    
    if (isLocalhost && !hasCustomHost && (url.includes('localhost') || url.includes('127.0.0.1'))) {
        const detected = await detectBrowserCandidateIps();
        const candidateIp = detected.length > 0 ? detected[0] : '';
        openHostModal(candidateIp);
        showToast('💡 请先确认/填写宿主机真实局域网 IP，以供其他设备访问', 'info');
        return;
    }

    const btn = document.getElementById('btn-copy-m3u');
    copyTextToClipboard(url, 'M3U 订阅源地址', btn);
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

// 统一风格自定义确认对话框 (替代原生 confirm)
function showConfirm({
    title = '确认操作',
    message = '确定要执行此操作吗？',
    okText = '确定',
    cancelText = '取消',
    type = 'danger' // 'danger' | 'warning' | 'primary'
} = {}) {
    return new Promise((resolve) => {
        const titleEl = document.getElementById('confirm-title');
        const msgEl = document.getElementById('confirm-message');
        const okBtn = document.getElementById('confirm-btn-ok');
        const cancelBtn = document.getElementById('confirm-btn-cancel');
        const iconBox = document.getElementById('confirm-icon-box');

        if (titleEl) titleEl.innerText = title;
        if (msgEl) msgEl.innerText = message;
        if (okBtn) okBtn.innerText = okText;
        if (cancelBtn) cancelBtn.innerText = cancelText;

        if (okBtn && iconBox) {
            if (type === 'danger') {
                okBtn.className = 'btn btn-danger';
                iconBox.className = 'confirm-icon-box danger';
            } else if (type === 'warning') {
                okBtn.className = 'btn btn-warning';
                iconBox.className = 'confirm-icon-box warning';
            } else {
                okBtn.className = 'btn btn-primary';
                iconBox.className = 'confirm-icon-box primary';
            }
        }

        const handleOk = () => {
            cleanup();
            resolve(true);
        };

        const handleCancel = () => {
            cleanup();
            resolve(false);
        };

        const handleKeydown = (e) => {
            if (e.key === 'Escape') handleCancel();
        };

        const cleanup = () => {
            okBtn?.removeEventListener('click', handleOk);
            cancelBtn?.removeEventListener('click', handleCancel);
            document.removeEventListener('keydown', handleKeydown);
            closeModal('modal-confirm');
        };

        okBtn?.addEventListener('click', handleOk, { once: true });
        cancelBtn?.addEventListener('click', handleCancel, { once: true });
        document.addEventListener('keydown', handleKeydown);

        openModal('modal-confirm');
        okBtn?.focus();
    });
}
