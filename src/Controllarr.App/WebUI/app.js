// ============================================================================
// Controllarr for Windows - browser WebUI (no-build, vanilla ES module)
//
// Functionally 1:1 with the macOS Controllarr React WebUI, retargeted to the
// EXACT JSON contract exposed by the Windows embedded server
// (Controllarr.Core/Server/QBittorrentApi.cs).
//
// Key contract facts honoured here:
//   * All requests are relative ('/api/...') with credentials:'include' so the
//     qBittorrent-style SID session cookie flows. 403 -> show sign-in card.
//   * Login is form-encoded POST /api/v2/auth/login (username/password).
//   * /api/controllarr/stats returns snake_case keys (download_rate, ...).
//   * /api/v2/torrents/info returns qBit-style keys (num_leech, not num_leechs;
//     no 'completed' field - we derive completed = progress * size).
//   * /api/controllarr/{categories,settings} serialize snake_case models.
//   * /api/controllarr/{recovery,seeding,postprocessor,vpn,diskspace,arr} and
//     the per-torrent files/trackers/peers serialize PascalCase POCOs with
//     enums as snake_case strings (Reason/Action) or integers (Source/Stage).
//   * Health endpoint builds an inline dict: info_hash, name, reason (PascalCase
//     enum name), first_seen/last_updated (ISO 8601 strings), last_progress.
//   * Log endpoint: id, timestamp (ISO 8601), level (lowercase), source, message.
//   * File priorities POST expects a JSON map {"<index>":<priority>}.
//   * There is no /api/controllarr/network endpoint on Windows.
// ============================================================================

'use strict'

/* ===========================================================================
 * Format helpers (mirror macOS WebUI src/format.ts)
 * ======================================================================== */

const dateFormatter = new Intl.DateTimeFormat(undefined, {
  month: 'short',
  day: 'numeric',
  hour: 'numeric',
  minute: '2-digit',
  second: '2-digit',
})

const relativeFormatter = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' })

function fmtBytes(n) {
  if (!Number.isFinite(n)) return '0 B'
  if (n < 1024) return `${n} B`
  const kb = n / 1024
  if (kb < 1024) return `${kb.toFixed(1)} KiB`
  const mb = kb / 1024
  if (mb < 1024) return `${mb.toFixed(1)} MiB`
  const gb = mb / 1024
  if (gb < 1024) return `${gb.toFixed(2)} GiB`
  return `${(gb / 1024).toFixed(2)} TiB`
}

function fmtRate(bps) {
  return `${fmtBytes(bps)}/s`
}

function fmtETA(s) {
  // qBit infinity sentinel from the Windows server is 8640000.
  if (s < 0 || !Number.isFinite(s) || s >= 8640000) return '∞'
  s = Math.round(s)
  if (s < 60) return `${s}s`
  if (s < 3600) return `${Math.floor(s / 60)}m ${s % 60}s`
  if (s < 86400) return `${Math.floor(s / 3600)}h ${Math.floor((s % 3600) / 60)}m`
  return `${Math.floor(s / 86400)}d ${Math.floor((s % 86400) / 3600)}h`
}

function fmtPercent(value) {
  return `${(value * 100).toFixed(1)}%`
}

// Epoch can be unix seconds (number) or an ISO 8601 string (DateTime.ToString("o")).
function toEpochSeconds(value) {
  if (value == null) return 0
  if (typeof value === 'number') return value
  const ms = Date.parse(value)
  return Number.isFinite(ms) ? ms / 1000 : 0
}

function fmtDateTime(epoch) {
  const seconds = toEpochSeconds(epoch)
  if (!seconds) return '—'
  return dateFormatter.format(new Date(seconds * 1000))
}

function fmtRelativeTime(epoch) {
  const epochSeconds = toEpochSeconds(epoch)
  if (!epochSeconds) return '—'
  const now = Date.now()
  const deltaSeconds = Math.round((epochSeconds * 1000 - now) / 1000)
  const units = [
    ['day', 86400],
    ['hour', 3600],
    ['minute', 60],
    ['second', 1],
  ]
  for (const [unit, size] of units) {
    if (Math.abs(deltaSeconds) >= size || unit === 'second') {
      return relativeFormatter.format(Math.round(deltaSeconds / size), unit)
    }
  }
  return 'now'
}

function fmtOptionalNumber(value, suffix = '') {
  if (value == null) return 'Inherit'
  return `${value}${suffix}`
}

function fmtUptime(seconds) {
  if (!Number.isFinite(seconds) || seconds <= 0) return '0s'
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  if (d > 0) return `${d}d ${h}h`
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m`
  return `${Math.floor(seconds)}s`
}

/* ===========================================================================
 * Tiny DOM helpers (no framework)
 * ======================================================================== */

function el(tag, props = {}, children = []) {
  const node = document.createElement(tag)
  for (const [key, value] of Object.entries(props)) {
    if (value == null) continue
    if (key === 'class') node.className = value
    else if (key === 'style' && typeof value === 'object') Object.assign(node.style, value)
    else if (key === 'dataset') Object.assign(node.dataset, value)
    else if (key.startsWith('on') && typeof value === 'function') {
      node.addEventListener(key.slice(2).toLowerCase(), value)
    } else if (key === 'html') node.innerHTML = value
    else if (key in node) {
      try { node[key] = value } catch { node.setAttribute(key, value) }
    } else node.setAttribute(key, value)
  }
  appendChildren(node, children)
  return node
}

function appendChildren(node, children) {
  const list = Array.isArray(children) ? children : [children]
  for (const child of list) {
    if (child == null || child === false) continue
    node.appendChild(typeof child === 'object' ? child : document.createTextNode(String(child)))
  }
}

function clear(node) {
  while (node.firstChild) node.removeChild(node.firstChild)
}

function pill(text, tone = 'neutral') {
  return el('span', { class: `pill ${tone}` }, [text])
}

function emptyState(title, body) {
  return el('div', { class: 'empty-state' }, [
    el('h3', {}, [title]),
    el('p', {}, [body]),
  ])
}

function field(labelText, control) {
  return el('label', { class: 'field' }, [el('span', {}, [labelText]), control])
}

function toggleField(labelText, checked, onChange) {
  const input = el('input', { type: 'checkbox', checked: !!checked, onChange: (e) => onChange(e.currentTarget.checked) })
  return el('label', { class: 'toggle-field' }, [el('span', {}, [labelText]), input])
}

/* ===========================================================================
 * API client - relative paths, credentials:'include'.
 * Throws { status, message } so callers can detect 403 -> sign-in.
 * ======================================================================== */

async function request(path, init) {
  let res
  try {
    res = await fetch(path, { credentials: 'include', ...init })
  } catch (networkError) {
    throw { status: 0, message: `Network error: ${networkError.message || networkError}` }
  }
  if (!res.ok) {
    let message = ''
    try { message = (await res.text()).trim() } catch { /* ignore */ }
    throw { status: res.status, message: message || `${init?.method ?? 'GET'} ${path} -> ${res.status}` }
  }
  return res
}

async function getJSON(path, init) {
  const res = await request(path, init)
  // Some endpoints (Ok / Ok(obj)) may return empty body or non-JSON; guard.
  const text = await res.text()
  if (!text) return null
  try { return JSON.parse(text) } catch { return null }
}

async function postForm(path, fields) {
  const body = new URLSearchParams()
  for (const [k, v] of Object.entries(fields)) {
    if (v != null) body.append(k, String(v))
  }
  return request(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: body.toString(),
  })
}

async function postJSON(path, payload, method = 'POST') {
  return request(path, {
    method,
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
  })
}

/* ---- Normalizers: map the Windows server JSON into stable internal shapes -- */

function normTorrent(t) {
  const size = num(t.size ?? t.total_size)
  const progress = num(t.progress)
  return {
    hash: t.hash ?? '',
    name: t.name ?? '',
    size,
    progress,
    completed: Math.round(size * progress), // server has no 'completed' field
    dlspeed: num(t.dlspeed),
    upspeed: num(t.upspeed),
    state: t.state ?? 'unknown',
    savePath: t.save_path ?? '',
    category: t.category ?? '',
    addedOn: num(t.added_on),
    ratio: num(t.ratio),
    numSeeds: num(t.num_seeds),
    numLeechs: num(t.num_leech ?? t.num_leechs), // Windows uses num_leech
    eta: num(t.eta),
    downloaded: num(t.downloaded),
    uploaded: num(t.uploaded),
  }
}

function normStats(s) {
  if (!s) return null
  return {
    downloadRate: num(s.download_rate),
    uploadRate: num(s.upload_rate),
    totalDownloaded: num(s.total_downloaded),
    totalUploaded: num(s.total_uploaded),
    dhtNodes: num(s.dht_nodes),
    listenPort: num(s.listen_port),
    torrentCount: num(s.torrent_count),
    downloadingCount: num(s.downloading_count),
    seedingCount: num(s.seeding_count),
    pausedCount: num(s.paused_count),
    uptimeSeconds: num(s.uptime_seconds),
  }
}

function normCategory(c) {
  return {
    name: c.name ?? '',
    savePath: c.save_path ?? '',
    completePath: c.complete_path ?? '',
    extractArchives: !!c.extract_archives,
    blockedExtensions: Array.isArray(c.blocked_extensions) ? c.blocked_extensions : [],
    maxRatio: c.max_ratio ?? null,
    maxSeedingTimeMinutes: c.max_seeding_time_minutes ?? null,
  }
}

function normHealth(h) {
  return {
    infoHash: h.info_hash ?? '',
    name: h.name ?? '',
    reason: friendlyHealthReason(h.reason), // reason arrives as PascalCase enum name
    rawReason: h.reason ?? '',
    firstSeen: h.first_seen,
    lastProgress: num(h.last_progress),
    lastUpdated: h.last_updated,
  }
}

function normRecovery(r) {
  // PascalCase POCO; Source serialized as integer (0=automatic,1=manual).
  return {
    infoHash: r.InfoHash ?? '',
    name: r.Name ?? '',
    reason: r.Reason ?? '',
    action: r.Action ?? '',
    source: typeof r.Source === 'number' ? (r.Source === 1 ? 'manual' : 'automatic') : String(r.Source ?? '').toLowerCase(),
    success: !!r.Success,
    message: r.Message ?? '',
    timestamp: r.Timestamp,
  }
}

const POST_STAGE_NAMES = ['Pending', 'Moving storage', 'Extracting', 'Done', 'Failed']
const POST_STAGE_KEYS = ['pending', 'moving', 'extracting', 'done', 'failed']

function normPostProcessor(p) {
  // Stage serialized as integer (PostStage enum). canRetry derived (Failed == 4).
  const stageIndex = typeof p.Stage === 'number' ? p.Stage : -1
  return {
    infoHash: p.InfoHash ?? '',
    name: p.Name ?? '',
    category: p.Category ?? '',
    stageIndex,
    stage: POST_STAGE_NAMES[stageIndex] ?? String(p.Stage ?? 'Unknown'),
    stageKey: POST_STAGE_KEYS[stageIndex] ?? 'unknown',
    canRetry: stageIndex === 4,
    message: p.Message ?? '',
    lastUpdated: p.LastUpdated,
  }
}

function normSeeding(s) {
  return {
    infoHash: s.InfoHash ?? '',
    name: s.Name ?? '',
    reason: s.Reason ?? '',
    action: s.Action ?? '', // snake_case enum string
    timestamp: s.Timestamp,
  }
}

function normVpn(v) {
  if (!v) return null
  const paused = Array.isArray(v.PausedHashes) ? v.PausedHashes : []
  return {
    enabled: !!v.Enabled,
    isConnected: !!v.IsConnected,
    interfaceName: v.InterfaceName ?? '',
    interfaceIP: v.InterfaceIP ?? '',
    killSwitchEngaged: !!v.KillSwitchEngaged,
    pausedHashes: paused,
    pausedCount: paused.length,
    boundToVPN: !!v.BoundToVPN,
  }
}

function normDisk(d) {
  if (!d) return null
  const paused = Array.isArray(d.PausedHashes) ? d.PausedHashes : []
  return {
    freeBytes: num(d.FreeBytes),
    thresholdBytes: num(d.ThresholdBytes),
    monitorPath: d.MonitorPath ?? '',
    shortfallBytes: num(d.ShortfallBytes),
    isPaused: !!d.IsPaused,
    pausedHashes: paused,
    pausedCount: paused.length,
  }
}

function normArr(a) {
  return {
    infoHash: a.InfoHash ?? '',
    name: a.Name ?? '',
    endpoint: a.Endpoint ?? '',
    success: !!a.Success,
    message: a.Message ?? '',
    timestamp: a.Timestamp,
  }
}

function normLog(e) {
  return {
    id: String(e.id ?? Math.random()),
    timestamp: e.timestamp,
    level: (e.level ?? 'info').toLowerCase(),
    source: e.source ?? '',
    message: e.message ?? '',
  }
}

function normFile(f, idx) {
  // PascalCase FileInfo POCO.
  return {
    index: typeof f.Index === 'number' ? f.Index : idx,
    name: f.Name ?? `File ${idx}`,
    size: num(f.Size),
    priority: num(f.Priority),
  }
}

function normTracker(t) {
  return {
    url: t.Url ?? '',
    tier: t.Tier,
    numPeers: t.NumPeers,
    numSeeds: t.NumSeeds,
    numLeechers: t.NumLeechers,
    numDownloaded: t.NumDownloaded,
    message: t.Message ?? '',
    status: t.Status,
  }
}

function normPeer(p) {
  return {
    ip: p.Ip ?? '',
    port: p.Port,
    client: p.Client ?? '',
    progress: num(p.Progress),
    downloadRate: num(p.DownloadRate),
    uploadRate: num(p.UploadRate),
    flags: p.Flags ?? '',
    country: p.Country ?? '',
  }
}

function num(v) {
  const n = Number(v)
  return Number.isFinite(n) ? n : 0
}

/* ---- Settings (snake_case <-> internal camelCase) ------------------------ */

function normSettings(s) {
  s = s || {}
  return {
    listenPortRangeStart: s.listen_port_range_start ?? 49152,
    listenPortRangeEnd: s.listen_port_range_end ?? 65000,
    preferredListenPort: s.preferred_listen_port ?? null,
    stallThresholdMinutes: s.stall_threshold_minutes ?? 10,
    defaultSavePath: s.default_save_path ?? '',
    webUIHost: s.web_ui_host ?? '127.0.0.1',
    webUIPort: s.web_ui_port ?? 8791,
    webUIUsername: s.web_ui_username ?? 'admin',
    webUIPassword: '', // form field stays blank; never echo the secret to the UI
    // The Windows server replaces settings wholesale on save (and Settings
    // defaults WebUIPassword to "adminadmin"), so omitting the password would
    // reset it. Keep the stored value here to resend when the field is blank.
    _storedWebUIPassword: s.web_ui_password ?? '',
    globalMaxRatio: s.global_max_ratio ?? null,
    globalMaxSeedingTimeMinutes: s.global_max_seeding_time_minutes ?? null,
    seedLimitAction: s.seed_limit_action ?? 'pause',
    minimumSeedTimeMinutes: s.minimum_seed_time_minutes ?? 60,
    healthStallMinutes: s.health_stall_minutes ?? 30,
    healthReannounceOnStall: s.health_reannounce_on_stall ?? true,
    recoveryRules: Array.isArray(s.recovery_rules) ? s.recovery_rules.map(normRecoveryRule) : [],
    bandwidthSchedule: Array.isArray(s.bandwidth_schedule) ? s.bandwidth_schedule.map(normBandwidthRule) : [],
    vpnEnabled: s.vpn_enabled ?? false,
    vpnKillSwitch: s.vpn_kill_switch ?? true,
    vpnBindInterface: s.vpn_bind_interface ?? true,
    vpnInterfacePrefix: s.vpn_interface_prefix ?? 'TAP',
    vpnMonitorIntervalSeconds: s.vpn_monitor_interval_seconds ?? 5,
    diskSpaceMinimumGB: s.disk_space_minimum_gb ?? null,
    diskSpaceMonitorPath: s.disk_space_monitor_path ?? '',
    arrReSearchAfterHours: s.arr_re_search_after_hours ?? 6,
    arrEndpoints: Array.isArray(s.arr_endpoints) ? s.arr_endpoints.map(normArrEndpoint) : [],
    // Keep the raw server object so a wholesale-replace save preserves any
    // settings the form doesn't render (peer discovery, connection limits,
    // queueing, security, UI prefs, etc.) instead of resetting them.
    _raw: s,
  }
}

function normRecoveryRule(r) {
  return {
    id: r.id ?? cryptoId(),
    enabled: r.enabled ?? true,
    trigger: r.trigger ?? 'metadata_timeout',
    action: r.action ?? 'reannounce',
    delayMinutes: r.delay_minutes ?? 10,
  }
}

function normBandwidthRule(b) {
  return {
    name: b.name ?? '',
    enabled: b.enabled ?? true,
    daysOfWeek: Array.isArray(b.days_of_week) ? b.days_of_week : [],
    startHour: b.start_hour ?? 0,
    startMinute: b.start_minute ?? 0,
    endHour: b.end_hour ?? 23,
    endMinute: b.end_minute ?? 59,
    maxDownloadKBps: b.max_download_kbps ?? 0,
    maxUploadKBps: b.max_upload_kbps ?? 0,
  }
}

function normArrEndpoint(a) {
  return {
    name: a.name ?? '',
    kind: a.kind ?? 'sonarr',
    baseURL: a.base_url ?? '',
    apiKey: '', // form field stays blank; never echo the secret to the UI
    _storedApiKey: a.api_key ?? '', // resent on wholesale-replace save when blank
  }
}

// Build the snake_case Settings payload the Windows server deserializes.
function settingsToPayload(s) {
  return {
    // Start from the raw server settings so fields the form doesn't render are
    // preserved on this wholesale-replace save; the keys below override them.
    ...(s._raw || {}),
    listen_port_range_start: clampPort(s.listenPortRangeStart),
    listen_port_range_end: clampPort(s.listenPortRangeEnd),
    preferred_listen_port: (s.preferredListenPort != null && s.preferredListenPort !== '')
      ? clampPort(s.preferredListenPort)
      : null,
    stall_threshold_minutes: int(s.stallThresholdMinutes),
    default_save_path: s.defaultSavePath ?? '',
    web_ui_host: s.webUIHost ?? '127.0.0.1',
    web_ui_port: int(s.webUIPort),
    web_ui_username: s.webUIUsername ?? 'admin',
    // The server replaces settings wholesale and Settings.WebUIPassword
    // defaults to "adminadmin", so we MUST always send a password. Use the
    // operator's freshly typed value when present, otherwise resend the stored
    // value captured on the last GET so the password is preserved unchanged.
    web_ui_password: (s.webUIPassword && s.webUIPassword.trim())
      ? s.webUIPassword
      : (s._storedWebUIPassword ?? ''),
    global_max_ratio: s.globalMaxRatio,
    global_max_seeding_time_minutes: s.globalMaxSeedingTimeMinutes,
    seed_limit_action: s.seedLimitAction,
    minimum_seed_time_minutes: int(s.minimumSeedTimeMinutes),
    health_stall_minutes: int(s.healthStallMinutes),
    health_reannounce_on_stall: !!s.healthReannounceOnStall,
    recovery_rules: s.recoveryRules.map((r) => ({
      id: r.id ?? cryptoId(),
      enabled: !!r.enabled,
      trigger: r.trigger,
      action: r.action,
      delay_minutes: int(r.delayMinutes),
    })),
    bandwidth_schedule: s.bandwidthSchedule.map((b) => ({
      name: b.name ?? '',
      enabled: !!b.enabled,
      days_of_week: b.daysOfWeek,
      start_hour: int(b.startHour),
      start_minute: int(b.startMinute),
      end_hour: int(b.endHour),
      end_minute: int(b.endMinute),
      max_download_kbps: int(b.maxDownloadKBps),
      max_upload_kbps: int(b.maxUploadKBps),
    })),
    vpn_enabled: !!s.vpnEnabled,
    vpn_kill_switch: !!s.vpnKillSwitch,
    vpn_bind_interface: !!s.vpnBindInterface,
    vpn_interface_prefix: s.vpnInterfacePrefix ?? 'TAP',
    vpn_monitor_interval_seconds: int(s.vpnMonitorIntervalSeconds),
    disk_space_minimum_gb: s.diskSpaceMinimumGB,
    disk_space_monitor_path: s.diskSpaceMonitorPath ?? '',
    arr_endpoints: s.arrEndpoints.map((a) => ({
      name: a.name ?? '',
      kind: a.kind,
      base_url: a.baseURL ?? '',
      // Same wholesale-replace caveat as the password: always send a key,
      // preferring a freshly typed one, else the stored value from the GET.
      api_key: (a.apiKey && a.apiKey.trim()) ? a.apiKey : (a._storedApiKey ?? ''),
    })),
    arr_re_search_after_hours: int(s.arrReSearchAfterHours),
  }
}

function categoryToPayload(c) {
  return {
    name: c.name,
    save_path: c.savePath,
    complete_path: c.completePath ?? '',
    extract_archives: !!c.extractArchives,
    blocked_extensions: c.blockedExtensions ?? [],
    max_ratio: c.maxRatio ?? null,
    max_seeding_time_minutes: c.maxSeedingTimeMinutes ?? null,
  }
}

function clampPort(v) {
  const n = int(v)
  return Math.max(1, Math.min(65535, n || 1))
}

function int(v) {
  const n = Math.round(Number(v))
  return Number.isFinite(n) ? n : 0
}

function cryptoId() {
  if (window.crypto?.randomUUID) return window.crypto.randomUUID()
  return 'r' + Math.random().toString(36).slice(2)
}

/* ---- API surface --------------------------------------------------------- */

const api = {
  async login(username, password) {
    await postForm('/api/v2/auth/login', { username, password })
  },
  async logout() {
    try { await request('/api/v2/auth/logout', { method: 'POST' }) } catch { /* ignore */ }
  },
  async torrents() {
    const data = (await getJSON('/api/v2/torrents/info')) || []
    return data.map(normTorrent)
  },
  async stats() {
    return normStats(await getJSON('/api/controllarr/stats'))
  },
  async addMagnet(uri, category) {
    const fields = { urls: uri }
    if (category) fields.category = category
    await postForm('/api/v2/torrents/add', fields)
  },
  async addTorrentFile(file, category) {
    const body = new FormData()
    body.append('torrents', file)
    if (category) body.append('category', category)
    await request('/api/v2/torrents/add', { method: 'POST', body })
  },
  async pause(hash) { await postForm('/api/v2/torrents/pause', { hashes: hash }) },
  async resume(hash) { await postForm('/api/v2/torrents/resume', { hashes: hash }) },
  async remove(hash, deleteFiles) {
    await postForm('/api/v2/torrents/delete', { hashes: hash, deleteFiles: deleteFiles ? 'true' : 'false' })
  },
  async setCategory(hash, category) {
    await postForm('/api/v2/torrents/setCategory', { hashes: hash, category: category ?? '' })
  },
  async cyclePort() { await request('/api/controllarr/port/cycle', { method: 'POST' }) },
  async categories() {
    const data = (await getJSON('/api/controllarr/categories')) || []
    return data.map(normCategory)
  },
  async saveCategory(category) {
    await postJSON('/api/controllarr/categories', categoryToPayload(category))
  },
  async deleteCategory(name) {
    await request(`/api/controllarr/categories/${encodeURIComponent(name)}`, { method: 'DELETE' })
  },
  async settings() {
    return normSettings(await getJSON('/api/controllarr/settings'))
  },
  async saveSettings(settings) {
    await postJSON('/api/controllarr/settings', settingsToPayload(settings))
  },
  async exportBackup() {
    const res = await request('/api/controllarr/backup')
    const disposition = res.headers.get('Content-Disposition') ?? ''
    const match = disposition.match(/filename="?([^"]+)"?/)
    const filename = (match && match[1]) || `controllarr-backup-${new Date().toISOString().replace(/[:.]/g, '-')}.json`
    return { blob: await res.blob(), filename }
  },
  async importBackup(text) {
    return getJSON('/api/controllarr/backup/import', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: text,
    })
  },
  async health() {
    const data = (await getJSON('/api/controllarr/health')) || []
    return data.map(normHealth)
  },
  async clearHealth(hash) { await postForm('/api/controllarr/health/clear', { hash }) },
  async recovery() {
    const data = (await getJSON('/api/controllarr/recovery')) || []
    return data.map(normRecovery)
  },
  async runRecovery(hash) {
    await postForm('/api/controllarr/recovery/run', { hash })
  },
  async postProcessor() {
    const data = (await getJSON('/api/controllarr/postprocessor')) || []
    return data.map(normPostProcessor)
  },
  async retryPostProcessor(hash) {
    await postForm('/api/controllarr/postprocessor/retry', { hash })
  },
  async seeding() {
    const data = (await getJSON('/api/controllarr/seeding')) || []
    return data.map(normSeeding)
  },
  async log(limit = 500) {
    const data = (await getJSON(`/api/controllarr/log?limit=${limit}`)) || []
    return data.map(normLog)
  },
  async diskSpace() { return normDisk(await getJSON('/api/controllarr/diskspace')) },
  async recheckDiskSpace() {
    return normDisk(await getJSON('/api/controllarr/diskspace/recheck', { method: 'POST' }))
  },
  async vpnStatus() { return normVpn(await getJSON('/api/controllarr/vpn')) },
  async arrNotifications() {
    const data = (await getJSON('/api/controllarr/arr')) || []
    return data.map(normArr)
  },
  async files(hash) {
    const data = (await getJSON(`/api/controllarr/torrents/${encodeURIComponent(hash)}/files`)) || []
    return data.map(normFile)
  },
  async setFilePriorities(hash, priorityMap) {
    // The Windows server deserializes a Dictionary<int,int> index->priority.
    await postJSON(`/api/controllarr/torrents/${encodeURIComponent(hash)}/files`, priorityMap)
  },
  async trackers(hash) {
    const data = (await getJSON(`/api/controllarr/torrents/${encodeURIComponent(hash)}/trackers`)) || []
    return data.map(normTracker)
  },
  async peers(hash) {
    const data = (await getJSON(`/api/controllarr/torrents/${encodeURIComponent(hash)}/peers`)) || []
    return data.map(normPeer)
  },
}

/* ===========================================================================
 * Friendly label maps + tone helpers
 * ======================================================================== */

const TABS = [
  { id: 'home', label: 'Home', title: 'Overview', description: 'Session metrics, status pills, quick actions, and the most active transfers.' },
  { id: 'torrents', label: 'Torrents', title: 'Torrents', description: 'Add magnets or .torrent files, watch live throughput, and manage the active queue.' },
  { id: 'categories', label: 'Categories', title: 'Categories', description: 'Map incoming downloads, completion paths, extraction rules, and file filters.' },
  { id: 'settings', label: 'Settings', title: 'Settings', description: 'Tune WebUI access, listen ports, seeding policy, VPN, disk, *arr, bandwidth, recovery, and backups.' },
  { id: 'health', label: 'Health', title: 'Health', description: 'Review stalled or unhealthy torrents and clear issues once resolved.' },
  { id: 'recovery', label: 'Recovery', title: 'Recovery', description: 'Track automatic playbooks, manual recoveries, and recovery rule outcomes.' },
  { id: 'postprocessor', label: 'Post-Processor', title: 'Post-Processor', description: 'See completed torrents moving, extracting, and landing in their final destination.' },
  { id: 'seeding', label: 'Seeding', title: 'Seeding', description: 'Audit seeding-limit enforcement and what action Controllarr applied.' },
  { id: 'log', label: 'Log', title: 'Log', description: 'Tail the in-app ring buffer with live level filtering and search.' },
]

const RECOVERY_TRIGGER_OPTIONS = [
  'metadata_timeout', 'no_peers', 'stalled_with_peers', 'awaiting_recheck',
  'post_process_move_failed', 'post_process_extraction_failed', 'disk_pressure',
]
const RECOVERY_ACTION_OPTIONS = [
  'reannounce', 'pause', 'remove_keep_files', 'remove_delete_files', 'retry_post_process',
]
const DAY_LABELS = { 1: 'Sun', 2: 'Mon', 3: 'Tue', 4: 'Wed', 5: 'Thu', 6: 'Fri', 7: 'Sat' }

// The health endpoint sends the C# HealthReason enum's .ToString() (PascalCase)
// while recovery rules use the snake_case converter. Handle both.
function friendlyHealthReason(reason) {
  switch (reason) {
    case 'MetadataTimeout':
    case 'metadata_timeout': return 'Metadata timeout'
    case 'NoPeers':
    case 'no_peers': return 'No peers'
    case 'StalledWithPeers':
    case 'stalled_with_peers': return 'Stalled (with peers)'
    case 'AwaitingRecheck':
    case 'awaiting_recheck': return 'Awaiting recheck'
    case 'PostProcessMoveFailed':
    case 'post_process_move_failed': return 'Post-process move failed'
    case 'PostProcessExtractionFailed':
    case 'post_process_extraction_failed': return 'Post-process extraction failed'
    case 'DiskPressure':
    case 'disk_pressure': return 'Disk pressure'
    default: return reason || '—'
  }
}

function friendlyRecoveryAction(action) {
  switch (action) {
    case 'reannounce': return 'Reannounce'
    case 'pause': return 'Pause'
    case 'remove_keep_files': return 'Remove (keep files)'
    case 'remove_delete_files': return 'Remove (delete files)'
    case 'retry_post_process': return 'Retry post-process'
    default: return action || '—'
  }
}

function friendlySeedAction(action) {
  switch (action) {
    case 'pause': return 'Pause'
    case 'remove_keep_files': return 'Remove (keep files)'
    case 'remove_delete_files': return 'Remove (delete files)'
    default: return action || '—'
  }
}

function stateTone(state) {
  if (!state) return 'neutral'
  if (state.startsWith('paused')) return 'neutral'
  if (state === 'uploading' || state === 'stalledUP') return 'green'
  if (state === 'error' || state === 'unknown') return 'red'
  if (state === 'stalledDL') return 'amber'
  return 'blue'
}

function stageTone(stageKey) {
  if (stageKey === 'failed') return 'red'
  if (stageKey === 'done') return 'green'
  if (stageKey === 'extracting' || stageKey === 'moving') return 'amber'
  return 'blue'
}

// Windows TrackerInfo.Status: 0=unknown, 1=ok(working), 2=connecting(updating),
// 3=invalid response(error), 4=offline.
function trackerStatusText(status) {
  switch (status) {
    case 0: return 'Not contacted'
    case 1: return 'Working'
    case 2: return 'Updating'
    case 3: return 'Error'
    case 4: return 'Offline'
    default: return 'Unknown'
  }
}

function trackerStatusTone(status) {
  switch (status) {
    case 1: return 'green'
    case 2: return 'blue'
    case 3:
    case 4: return 'red'
    default: return 'neutral'
  }
}

function isPaused(state) { return !!state && state.startsWith('paused') }
function isSeeding(state) { return state === 'uploading' || state === 'stalledUP' }

/* ===========================================================================
 * Application state + polling
 * ======================================================================== */

const state = {
  authed: false,
  activeTab: 'home',
  loading: false,
  error: null,
  flash: null, // { tone, message }
  lastUpdated: null,

  torrents: [],
  stats: null,
  categories: [],
  health: [],
  recovery: [],
  postProcessor: [],
  seeding: [],
  log: [],
  arr: [],
  vpn: null,
  disk: null,

  // Settings: serverSettings is the last fetched snapshot; draft is the form copy.
  serverSettings: null,
  draftSettings: null,
  settingsDirty: false,

  // Torrents tab UI
  torrentSearch: '',
  selectedHash: null,
  detailTab: 'files',
  detail: { files: [], trackers: [], peers: [], error: null },

  // Log tab UI
  logFilter: '',
  logLevel: 'all',

  // Add-torrent form
  addMagnet: '',
  addCategory: '',
  addFile: null,
  uploading: false,

  // Category modal
  categoryModal: null, // { mode:'new' } | { mode:'edit', category }
}

let pollTimers = []
let detailTimer = null

function clearPollTimers() {
  for (const t of pollTimers) clearInterval(t)
  pollTimers = []
}

function clearDetailTimer() {
  if (detailTimer) { clearInterval(detailTimer); detailTimer = null }
}

function setFlash(tone, message) {
  state.flash = { tone, message }
  render()
  window.clearTimeout(setFlash._t)
  setFlash._t = window.setTimeout(() => { state.flash = null; render() }, 3000)
}

function setError(message) {
  state.error = message
  render()
}

// Wrap a mutating action: run it, surface errors, then refresh live data.
async function runAction(action, { refreshAll = false } = {}) {
  try {
    await action()
    state.error = null
    if (refreshAll) await loadAll()
    else await loadLive()
  } catch (err) {
    handleApiError(err)
  }
}

function handleApiError(err) {
  if (err && err.status === 403 && state.authed) {
    // Session expired - drop to the sign-in card.
    state.authed = false
    clearPollTimers()
    clearDetailTimer()
    state.error = 'Session expired. Please sign in again.'
    render()
    return
  }
  state.error = (err && err.message) ? err.message : String(err)
  render()
}

/* ---- Data loading -------------------------------------------------------- */

async function loadAll() {
  state.loading = true
  render()
  try {
    const [torrents, stats, categories, settings] = await Promise.all([
      api.torrents(), api.stats(), api.categories(), api.settings(),
    ])
    state.torrents = sortTorrents(torrents)
    state.stats = stats
    state.categories = categories.slice().sort((a, b) => a.name.localeCompare(b.name))
    state.serverSettings = settings
    if (!state.settingsDirty || !state.draftSettings) {
      state.draftSettings = deepClone(settings)
    }
    await loadActiveTabData()
    state.lastUpdated = Date.now()
    state.error = null
  } catch (err) {
    handleApiError(err)
  } finally {
    state.loading = false
    render()
  }
}

// Lightweight refresh used by polling: always torrents + stats, plus the data
// for whichever tab is active.
async function loadLive() {
  try {
    const [torrents, stats] = await Promise.all([api.torrents(), api.stats()])
    state.torrents = sortTorrents(torrents)
    state.stats = stats
    await loadActiveTabData()
    state.lastUpdated = Date.now()
    state.error = null
  } catch (err) {
    handleApiError(err)
    return
  }
  // Don't yank focus out of a field the user is actively editing.
  if (!isEditingProtectedField()) render()
}

// True when the user is mid-edit in a field that a background re-render would
// disrupt (settings inputs that don't self-render, or the category modal).
function isEditingProtectedField() {
  const active = document.activeElement
  if (!active) return false
  const tag = active.tagName
  if (tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT') return false
  // Persistent search/magnet fields restore their own focus, so they're safe.
  if (active.dataset && active.dataset.focusKey) return false
  // Protect settings editing and the category modal.
  if (state.categoryModal) return true
  if (state.activeTab === 'settings') return true
  return false
}

async function loadActiveTabData() {
  switch (state.activeTab) {
    case 'home':
    case 'torrents':
    case 'categories':
      state.categories = (await api.categories()).slice().sort((a, b) => a.name.localeCompare(b.name))
      break
    case 'health':
      state.health = (await api.health()).sort((a, b) => toEpochSeconds(b.lastUpdated) - toEpochSeconds(a.lastUpdated))
      break
    case 'recovery':
      state.recovery = (await api.recovery()).sort((a, b) => toEpochSeconds(b.timestamp) - toEpochSeconds(a.timestamp))
      break
    case 'postprocessor':
      state.postProcessor = (await api.postProcessor()).sort((a, b) => toEpochSeconds(b.lastUpdated) - toEpochSeconds(a.lastUpdated))
      break
    case 'seeding':
      state.seeding = (await api.seeding()).sort((a, b) => toEpochSeconds(b.timestamp) - toEpochSeconds(a.timestamp))
      break
    case 'log':
      state.log = (await api.log(500)).sort((a, b) => toEpochSeconds(b.timestamp) - toEpochSeconds(a.timestamp))
      break
    case 'settings':
      // settings refresh handled in loadAll; nothing live to pull each tick
      break
  }
}

function sortTorrents(list) {
  return list.slice().sort((a, b) => b.addedOn - a.addedOn)
}

// Slow-cadence pollers that are resilient to errors (never throw).
function renderIfSafe() {
  if (!isEditingProtectedField()) render()
}
async function pollVpn() {
  if (!state.serverSettings?.vpnEnabled) { if (state.vpn) { state.vpn = null; renderIfSafe() } return }
  try { state.vpn = await api.vpnStatus(); renderIfSafe() } catch { /* ignore */ }
}
async function pollDisk() {
  try { state.disk = await api.diskSpace(); if (state.activeTab === 'settings' || state.activeTab === 'home') renderIfSafe() } catch { /* ignore */ }
}
async function pollArr() {
  try { state.arr = await api.arrNotifications(); if (state.activeTab === 'settings') renderIfSafe() } catch { /* ignore */ }
}

function startPolling() {
  clearPollTimers()
  // ~2s: torrents + stats + active tab data.
  pollTimers.push(window.setInterval(() => { void loadLive() }, 2000))
  // ~5s: VPN + disk.
  pollTimers.push(window.setInterval(() => { void pollVpn(); void pollDisk() }, 5000))
  // ~10s: *arr notifications.
  pollTimers.push(window.setInterval(() => { void pollArr() }, 10000))
}

/* ---- Per-torrent detail polling (~3s) ------------------------------------ */

async function loadDetail() {
  const hash = state.selectedHash
  if (!hash) return
  try {
    const [files, trackers, peers] = await Promise.all([
      api.files(hash), api.trackers(hash), api.peers(hash),
    ])
    if (state.selectedHash !== hash) return // selection changed mid-flight
    state.detail = { files, trackers, peers, error: null }
  } catch {
    state.detail = { ...state.detail, error: 'Failed to load details (will retry).' }
  }
  if (state.activeTab === 'torrents') render()
}

function startDetailPolling() {
  clearDetailTimer()
  void loadDetail()
  detailTimer = window.setInterval(() => { void loadDetail() }, 3000)
}

function selectTorrent(hash) {
  if (state.selectedHash === hash) {
    state.selectedHash = null
    state.detail = { files: [], trackers: [], peers: [], error: null }
    clearDetailTimer()
  } else {
    state.selectedHash = hash
    state.detailTab = 'files'
    state.detail = { files: [], trackers: [], peers: [], error: null }
    startDetailPolling()
  }
  render()
}

/* ---- Tab switching ------------------------------------------------------- */

function setTab(tabId) {
  if (state.activeTab === tabId) return
  state.activeTab = tabId
  state.error = null
  render()
  // Pull this tab's data immediately (resilient).
  void loadActiveTabData().then(render).catch(handleApiError)
}

/* ===========================================================================
 * Rendering
 * ======================================================================== */

const root = document.getElementById('root')

function render() {
  captureFocus()
  clear(root)
  root.appendChild(state.authed ? renderApp() : renderSignIn())
  restoreFocus()
}

/* ---- Sign-in ------------------------------------------------------------- */

function renderSignIn() {
  const username = el('input', { value: 'admin', autocomplete: 'username' })
  const password = el('input', { type: 'password', value: 'adminadmin', autocomplete: 'current-password' })
  const submit = el('button', { class: 'primary', type: 'submit' }, [state.loading ? 'Signing in…' : 'Sign in'])
  submit.disabled = state.loading

  const form = el('form', {
    class: 'sign-in-card',
    onSubmit: async (e) => {
      e.preventDefault()
      state.loading = true
      render()
      try {
        await api.login(username.value, password.value)
        state.authed = true
        state.error = null
        state.loading = false
        render()
        startPolling()
        await loadAll()
      } catch (err) {
        state.loading = false
        state.error = (err && err.status === 403)
          ? 'Sign-in failed. Check the WebUI username and password.'
          : ((err && err.message) || String(err))
        render()
      }
    },
  }, [
    el('div', { class: 'brand-kicker' }, ['Controllarr for Windows']),
    el('h1', {}, ['Browser control room']),
    el('p', {}, ['Sign in with the WebUI account to manage torrents, categories, seeding policy, health, and logs from any machine on the network.']),
    field('Username', username),
    field('Password', password),
    el('div', { class: 'button-row' }, [submit]),
    state.error ? el('div', { class: 'banner error' }, [state.error]) : null,
  ])

  return el('div', { class: 'sign-in-shell' }, [form])
}

/* ---- App shell ----------------------------------------------------------- */

function renderApp() {
  const current = TABS.find((t) => t.id === state.activeTab) || TABS[0]
  return el('div', { class: 'app-shell' }, [
    el('div', { class: 'shell' }, [
      renderSidebar(),
      el('main', { class: 'main-column' }, [
        renderHero(current),
        renderStatsGrid(),
        state.error ? banner('error', state.error) : null,
        state.flash ? banner(state.flash.tone, state.flash.message) : null,
        el('section', { class: 'panel panel-body' }, [renderTab(current)]),
      ]),
    ]),
    state.categoryModal ? renderCategoryModal() : null,
  ])
}

function banner(tone, message) {
  return el('div', { class: `banner ${tone}` }, [
    el('span', {}, [message]),
    el('button', { type: 'button', class: 'ghost', onClick: () => { state.error = tone === 'error' ? null : state.error; state.flash = tone === 'error' ? state.flash : null; render() } }, ['Dismiss']),
  ])
}

function countForTab(id) {
  switch (id) {
    case 'home': return state.stats ? state.stats.torrentCount : state.torrents.length
    case 'torrents': return state.torrents.length
    case 'categories': return state.categories.length
    case 'settings': return null
    case 'health': return state.health.length
    case 'recovery': return state.recovery.length
    case 'postprocessor': return state.postProcessor.length
    case 'seeding': return state.seeding.length
    case 'log': return state.log.length
    default: return null
  }
}

function renderSidebar() {
  const nav = el('nav', { class: 'tab-nav', 'aria-label': 'Sections' },
    TABS.map((tab) => {
      const count = countForTab(tab.id)
      return el('button', {
        type: 'button',
        class: tab.id === state.activeTab ? 'tab-button active' : 'tab-button',
        onClick: () => setTab(tab.id),
      }, [
        el('span', {}, [tab.label]),
        count == null ? null : el('span', { class: 'tab-count' }, [String(count)]),
      ])
    })
  )

  const s = state.stats
  const miniStats = [
    miniStat('Listen port', s ? String(s.listenPort) : '—'),
    miniStat('Torrents', s ? String(s.torrentCount) : '—'),
    miniStat('Download', s ? fmtRate(s.downloadRate) : '—'),
    miniStat('Upload', s ? fmtRate(s.uploadRate) : '—'),
  ]
  if (state.vpn) {
    miniStats.push(el('div', { class: 'mini-stat' }, [
      el('span', {}, ['VPN']),
      el('strong', { style: { color: state.vpn.isConnected ? 'var(--green)' : 'var(--red)' } },
        [state.vpn.isConnected ? (state.vpn.interfaceName || 'Connected') : 'Down']),
    ]))
  }
  if (state.disk && state.disk.thresholdBytes > 0) {
    miniStats.push(miniStat('Free disk', fmtBytes(state.disk.freeBytes)))
  }

  const refreshBtn = el('button', { type: 'button', onClick: () => { void loadAll() } },
    [state.loading ? 'Refreshing…' : 'Refresh now'])
  refreshBtn.disabled = state.loading

  const logoutBtn = el('button', { type: 'button', class: 'ghost', onClick: async () => {
    await api.logout()
    state.authed = false
    clearPollTimers()
    clearDetailTimer()
    render()
  } }, ['Sign out'])

  return el('aside', { class: 'sidebar panel' }, [
    el('div', { class: 'sidebar-header' }, [
      el('div', { class: 'brand-kicker' }, ['Controllarr']),
      el('div', { class: 'sidebar-title' }, ['Operations']),
      el('p', {}, ['qBittorrent-compatible control surface with Controllarr-native automation.']),
    ]),
    nav,
    el('div', { class: 'sidebar-footer' }, [...miniStats, refreshBtn, logoutBtn]),
  ])
}

function miniStat(label, value) {
  return el('div', { class: 'mini-stat' }, [el('span', {}, [label]), el('strong', {}, [value])])
}

function renderHero(current) {
  const s = state.stats
  let pillText = 'Watching listen state'
  let pillTone = 'neutral'
  if (s) {
    if (s.downloadingCount > 0) { pillText = `${s.downloadingCount} downloading`; pillTone = 'blue' }
    else if (s.seedingCount > 0) { pillText = `${s.seedingCount} seeding`; pillTone = 'green' }
    else if (s.torrentCount > 0) { pillText = 'Idle queue'; pillTone = 'amber' }
    else { pillText = 'No torrents'; pillTone = 'neutral' }
  }

  const refreshAll = el('button', { type: 'button', class: 'primary', onClick: () => { void loadAll() } }, ['Refresh all'])
  refreshAll.disabled = state.loading

  return el('section', { class: 'hero panel' }, [
    el('div', {}, [
      el('div', { class: 'brand-kicker' }, [current.label]),
      el('h1', {}, [current.title]),
      el('p', {}, [current.description]),
    ]),
    el('div', { class: 'hero-actions' }, [
      el('div', { class: `pill ${pillTone}` }, [pillText]),
      el('div', { class: 'mini-stat' }, [
        el('span', {}, ['Live sync']),
        el('strong', {}, [state.lastUpdated ? fmtRelativeTime(state.lastUpdated / 1000) : 'Waiting']),
      ]),
      refreshAll,
    ]),
  ])
}

function renderStatsGrid() {
  const s = state.stats
  const cards = [
    { label: 'Download', value: s ? fmtRate(s.downloadRate) : '—', tone: 'blue' },
    { label: 'Upload', value: s ? fmtRate(s.uploadRate) : '—', tone: 'green' },
    { label: 'Listen port', value: s ? String(s.listenPort) : '—', tone: 'neutral' },
    { label: 'Torrents', value: s ? String(s.torrentCount) : '—', tone: 'neutral' },
    { label: 'Downloaded', value: s ? fmtBytes(s.totalDownloaded) : '—', tone: 'neutral' },
    { label: 'Uploaded', value: s ? fmtBytes(s.totalUploaded) : '—', tone: 'neutral' },
  ]
  return el('section', { class: 'stats-grid' }, cards.map((c) =>
    el('article', { class: `metric-card ${c.tone}` }, [
      el('div', { class: 'metric-label' }, [c.label]),
      el('div', { class: 'metric-value' }, [c.value]),
    ])
  ))
}

function renderTab(current) {
  if (state.loading && !state.lastUpdated) {
    return el('div', { class: 'empty-state' }, [
      el('h2', {}, ['Loading Controllarr…']),
      el('p', {}, ['Pulling session stats, categories, health state, and the latest log entries.']),
    ])
  }
  switch (state.activeTab) {
    case 'home': return renderHome()
    case 'torrents': return renderTorrents()
    case 'categories': return renderCategories()
    case 'settings': return renderSettings()
    case 'health': return renderHealth()
    case 'recovery': return renderRecovery()
    case 'postprocessor': return renderPostProcessor()
    case 'seeding': return renderSeeding()
    case 'log': return renderLog()
    default: return emptyState('Unknown tab', 'Pick a section from the sidebar.')
  }
}

/* ---- Home ---------------------------------------------------------------- */

function renderHome() {
  const s = state.stats
  const pills = []
  if (s) {
    pills.push(pill(`Port ${s.listenPort}`, 'blue'))
    pills.push(pill(`${s.downloadingCount} downloading`, s.downloadingCount > 0 ? 'blue' : 'neutral'))
    pills.push(pill(`${s.seedingCount} seeding`, s.seedingCount > 0 ? 'green' : 'neutral'))
    pills.push(pill(`${s.pausedCount} paused`, s.pausedCount > 0 ? 'amber' : 'neutral'))
    pills.push(pill(`Uptime ${fmtUptime(s.uptimeSeconds)}`, 'neutral'))
    if (s.dhtNodes > 0) pills.push(pill(`${s.dhtNodes} DHT nodes`, 'neutral'))
  }
  if (state.vpn) {
    pills.push(pill(
      state.vpn.isConnected ? `VPN: ${state.vpn.interfaceName || 'connected'}` : (state.vpn.killSwitchEngaged ? 'VPN down - kill switch' : 'VPN down'),
      state.vpn.isConnected ? 'green' : 'red'))
  }
  if (state.disk && state.disk.thresholdBytes > 0) {
    pills.push(pill(state.disk.isPaused ? `Disk low (${state.disk.pausedCount} paused)` : `Disk OK (${fmtBytes(state.disk.freeBytes)} free)`,
      state.disk.isPaused ? 'red' : 'green'))
  }

  const active = state.torrents
    .filter((t) => t.dlspeed > 0 || t.upspeed > 0)
    .sort((a, b) => (b.dlspeed + b.upspeed) - (a.dlspeed + a.upspeed))
    .slice(0, 6)

  const quickActions = el('div', { class: 'button-row' }, [
    el('button', { type: 'button', class: 'primary', onClick: () => setTab('torrents') }, ['Add a torrent']),
    el('button', { type: 'button', onClick: () => { void runAction(() => api.cyclePort()).then(() => setFlash('success', 'Port cycle requested')) } }, ['Cycle listen port']),
    el('button', { type: 'button', onClick: () => setTab('health') }, [`Health (${state.health.length})`]),
    el('button', { type: 'button', onClick: () => setTab('log') }, ['Open log']),
    el('button', { type: 'button', onClick: () => { void loadAll() } }, ['Refresh all']),
  ])

  const activePanel = el('section', { class: 'form-panel' }, [
    el('div', { class: 'form-panel-header' }, [
      el('h3', {}, ['Most active transfers']),
      el('p', {}, ['The torrents currently moving the most data, refreshed live.']),
    ]),
    active.length === 0
      ? emptyState('Nothing transferring', 'No torrents are uploading or downloading right now.')
      : el('div', { class: 'section-stack' }, active.map(renderActiveTransfer)),
  ])

  return el('div', { class: 'section-stack dash-grid' }, [
    el('section', { class: 'form-panel' }, [
      el('div', { class: 'form-panel-header' }, [
        el('h3', {}, ['Session status']),
        el('p', {}, ['Live snapshot of the embedded engine and Controllarr automation.']),
      ]),
      el('div', { class: 'status-pill-row' }, pills.length ? pills : [pill('Waiting for stats…', 'neutral')]),
    ]),
    el('section', { class: 'form-panel' }, [
      el('div', { class: 'form-panel-header' }, [el('h3', {}, ['Quick actions'])]),
      quickActions,
    ]),
    activePanel,
  ])
}

function renderActiveTransfer(t) {
  const seeding = isSeeding(t.state)
  return el('div', { class: 'active-transfer' }, [
    el('div', { class: 'at-name', title: t.name }, [t.name || '(fetching metadata…)']),
    el('div', { class: 'at-meta' }, [`↓ ${fmtRate(t.dlspeed)}  ↑ ${fmtRate(t.upspeed)}`]),
    el('div', { class: 'progress-track' }, [
      el('div', {
        class: seeding ? 'progress-fill success' : 'progress-fill',
        style: { width: `${Math.min(100, t.progress * 100)}%` },
      }),
    ]),
    el('div', { class: 'at-meta', style: { gridColumn: '1 / -1', textAlign: 'left', display: 'flex', alignItems: 'center', gap: '0.5rem' } }, [
      `${fmtPercent(t.progress)} · ${fmtBytes(t.completed)} / ${fmtBytes(t.size)}`,
      pill(t.state, stateTone(t.state)),
    ]),
  ])
}

/* ---- Torrents ------------------------------------------------------------ */

function renderTorrents() {
  const filtered = filterTorrents(state.torrents, state.torrentSearch)

  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [
        el('h2', {}, ['Torrents']),
        el('p', {}, ['Live queue, qBit-style actions, magnet/.torrent intake, and per-torrent detail.']),
      ]),
      el('div', { class: 'section-meta' }, [`${filtered.length} / ${state.torrents.length}`]),
    ]),
    renderAddTorrentForm(),
    renderTorrentToolbar(),
    state.torrents.length === 0
      ? emptyState('No torrents yet', "Add a magnet above or point your *arr apps at Controllarr's qBittorrent-compatible API.")
      : (filtered.length === 0
        ? emptyState('No matches', 'No torrents match your search. Clear the box to see them all.')
        : renderTorrentTable(filtered)),
    state.selectedHash ? renderTorrentDetail() : null,
  ])
}

function filterTorrents(list, query) {
  const q = query.trim().toLowerCase()
  if (!q) return list
  return list.filter((t) =>
    t.name.toLowerCase().includes(q) ||
    (t.category || '').toLowerCase().includes(q) ||
    t.hash.toLowerCase().includes(q))
}

function renderTorrentToolbar() {
  const input = persistFocus(el('input', {
    value: state.torrentSearch,
    placeholder: 'Search name, category, or info-hash',
    oninput: (e) => { state.torrentSearch = e.currentTarget.value; render() },
  }), 'torrent-search')
  const clearBtn = el('button', { type: 'button', onClick: () => { state.torrentSearch = ''; render() } }, ['Clear'])
  clearBtn.disabled = !state.torrentSearch
  return el('div', { class: 'toolbar-row' }, [
    el('label', { class: 'field search-field' }, [el('span', {}, ['Search']), input]),
    clearBtn,
  ])
}

function renderAddTorrentForm() {
  const magnet = persistFocus(el('textarea', {
    rows: 3, placeholder: 'magnet:?xt=urn:btih:…', value: state.addMagnet,
    oninput: (e) => { state.addMagnet = e.currentTarget.value },
  }), 'add-magnet')
  const fileInput = el('input', {
    type: 'file', accept: '.torrent',
    onChange: (e) => { state.addFile = e.currentTarget.files?.[0] ?? null; render() },
  })
  const categorySelect = el('select', {
    value: state.addCategory,
    onChange: (e) => { state.addCategory = e.currentTarget.value },
  }, [
    el('option', { value: '' }, ['No category']),
    ...state.categories.map((c) => el('option', { value: c.name }, [c.name])),
  ])
  categorySelect.value = state.addCategory

  const submitLabel = state.uploading
    ? 'Adding…'
    : (state.addFile && state.addMagnet.trim()) ? 'Add magnet + file'
      : state.addFile ? 'Upload .torrent' : 'Add magnet'
  const submit = el('button', { class: 'primary', type: 'submit' }, [submitLabel])
  submit.disabled = state.uploading || (!state.addMagnet.trim() && !state.addFile)

  const grid = [
    el('label', { class: 'field wide' }, [el('span', {}, ['Magnet URI']), magnet]),
    el('label', { class: 'field wide' }, [el('span', {}, ['.torrent file']), fileInput]),
  ]
  if (state.addFile) {
    grid.push(el('div', { class: 'field wide', style: { display: 'flex', alignItems: 'center', gap: '0.5rem' } }, [
      pill(state.addFile.name, 'blue'),
      el('button', { type: 'button', onClick: () => { state.addFile = null; render() } }, ['Clear file']),
    ]))
  }
  grid.push(el('label', { class: 'field' }, [el('span', {}, ['Category']), categorySelect]))

  return el('form', {
    class: 'form-panel accent-panel',
    onSubmit: async (e) => {
      e.preventDefault()
      const uri = state.addMagnet.trim()
      const file = state.addFile
      if (!uri && !file) return
      state.uploading = true
      render()
      try {
        const category = state.addCategory.trim() || undefined
        const tasks = []
        if (uri) tasks.push(api.addMagnet(uri, category))
        if (file) tasks.push(api.addTorrentFile(file, category))
        await Promise.all(tasks)
        state.addMagnet = ''
        state.addFile = null
        state.error = null
        setFlash('success', 'Torrent added')
        await loadLive()
      } catch (err) {
        handleApiError(err)
      } finally {
        state.uploading = false
        render()
      }
    },
  }, [
    el('div', { class: 'form-panel-header' }, [
      el('h3', {}, ['Add torrent']),
      el('p', {}, ['Paste a magnet URI, choose a .torrent file, or both. Assign a category so post-processing and file filters apply immediately.']),
    ]),
    el('div', { class: 'form-grid compact' }, grid),
    el('div', { class: 'button-row' }, [submit]),
  ])
}

function renderTorrentTable(torrents) {
  const categoryOptions = state.categories
  const head = el('tr', {}, ['Name', 'Category', 'Progress', 'Transfer', 'Peers', 'Ratio', 'ETA', 'State', 'Actions']
    .map((h) => el('th', {}, [h])))

  const rows = torrents.map((t) => {
    const paused = isPaused(t.state)
    const seeding = isSeeding(t.state)
    const selected = t.hash === state.selectedHash

    // Inline category changer (cycle / set category).
    const catSelect = el('select', {
      value: t.category || '',
      onClick: (e) => e.stopPropagation(),
      onChange: (e) => {
        e.stopPropagation()
        const value = e.currentTarget.value
        void runAction(() => api.setCategory(t.hash, value))
      },
    }, [
      el('option', { value: '' }, ['Unassigned']),
      ...categoryOptions.map((c) => el('option', { value: c.name }, [c.name])),
    ])
    catSelect.value = t.category || ''

    const actions = el('div', { class: 'action-cluster', onClick: (e) => e.stopPropagation() }, [
      paused
        ? el('button', { type: 'button', onClick: () => runAction(() => api.resume(t.hash)) }, ['Resume'])
        : el('button', { type: 'button', onClick: () => runAction(() => api.pause(t.hash)) }, ['Pause']),
      el('button', { type: 'button', onClick: () => {
        if (window.confirm(`Remove "${t.name}"? Files are kept on disk.`)) runAction(() => api.remove(t.hash, false))
      } }, ['Remove']),
      el('button', { type: 'button', class: 'danger', onClick: () => {
        if (window.confirm(`Delete "${t.name}" AND its files? This cannot be undone.`)) runAction(() => api.remove(t.hash, true))
      } }, ['Delete files']),
    ])

    return el('tr', {
      class: `clickable${selected ? ' selected' : ''}`,
      onClick: () => selectTorrent(t.hash),
    }, [
      el('td', {}, [
        el('div', { class: 'primary-cell' }, [t.name || '(fetching metadata…)']),
        el('div', { class: 'subtle-cell' }, [t.savePath || 'Save path pending']),
      ]),
      el('td', {}, [catSelect]),
      el('td', { class: 'progress-cell' }, [
        el('div', { class: 'progress-track' }, [
          el('div', {
            class: seeding ? 'progress-fill success' : paused ? 'progress-fill muted' : 'progress-fill',
            style: { width: `${Math.min(100, t.progress * 100)}%` },
          }),
        ]),
        el('div', { class: 'subtle-cell' }, [`${fmtPercent(t.progress)} · ${fmtBytes(t.completed)} / ${fmtBytes(t.size)}`]),
      ]),
      el('td', { class: 'mono-cell' }, [
        `↓ ${fmtRate(t.dlspeed)}`, el('br'), `↑ ${fmtRate(t.upspeed)}`,
      ]),
      el('td', { class: 'mono-cell' }, [`${t.numSeeds}S / ${t.numLeechs}L`]),
      el('td', { class: 'mono-cell' }, [t.ratio.toFixed(2)]),
      el('td', {}, [fmtETA(t.eta)]),
      el('td', {}, [pill(t.state, stateTone(t.state))]),
      el('td', {}, [actions]),
    ])
  })

  return el('div', { class: 'table-shell' }, [
    el('table', { class: 'data-table' }, [el('thead', {}, [head]), el('tbody', {}, rows)]),
  ])
}

function renderTorrentDetail() {
  const t = state.torrents.find((x) => x.hash === state.selectedHash)
  const subtabs = ['files', 'trackers', 'peers']
  const tabRow = el('div', { class: 'subtab-row' }, subtabs.map((tab) =>
    el('button', {
      type: 'button',
      class: state.detailTab === tab ? 'primary' : undefined,
      onClick: () => { state.detailTab = tab; render() },
    }, [tab.charAt(0).toUpperCase() + tab.slice(1)])
  ))

  let body
  if (state.detailTab === 'files') body = renderFilesDetail()
  else if (state.detailTab === 'trackers') body = renderTrackersDetail()
  else body = renderPeersDetail()

  return el('div', { class: 'form-panel', style: { marginTop: '4px' } }, [
    el('div', { class: 'form-panel-header' }, [
      el('h3', {}, [(t && t.name) || '(fetching metadata…)']),
      el('p', {}, ['Inspect files, trackers, and peers. Refreshes every 3 seconds.']),
    ]),
    tabRow,
    state.detail.error ? el('div', { class: 'banner error detail-error' }, [state.detail.error]) : null,
    body,
  ])
}

function renderFilesDetail() {
  const files = state.detail.files
  if (files.length === 0) return emptyState('No file data', 'Waiting for metadata, or the torrent has no files listed yet.')

  const rows = files.map((f) => el('tr', {}, [
    el('td', { class: 'primary-cell' }, [f.name]),
    el('td', { class: 'mono-cell' }, [fmtBytes(f.size)]),
    el('td', {}, [pill(f.priority === 0 ? 'Skipped' : `Priority ${f.priority}`, f.priority === 0 ? 'neutral' : 'blue')]),
    el('td', {}, [
      el('button', { type: 'button', onClick: () => toggleFilePriority(f.index) },
        [f.priority === 0 ? 'Enable' : 'Skip']),
    ]),
  ]))

  return el('div', { class: 'table-shell' }, [
    el('table', { class: 'data-table', style: { minWidth: '600px' } }, [
      el('thead', {}, [el('tr', {}, ['Name', 'Size', 'Priority', 'Toggle'].map((h) => el('th', {}, [h])))]),
      el('tbody', {}, rows),
    ]),
  ])
}

async function toggleFilePriority(index) {
  const hash = state.selectedHash
  if (!hash) return
  // Optimistic toggle (0 <-> 4) then post the full index->priority map.
  const updated = state.detail.files.map((f) =>
    f.index === index ? { ...f, priority: f.priority === 0 ? 4 : 0 } : f)
  state.detail = { ...state.detail, files: updated }
  render()
  const map = {}
  for (const f of updated) map[f.index] = f.priority
  try {
    await api.setFilePriorities(hash, map)
  } catch (err) {
    // Revert on failure.
    try { state.detail = { ...state.detail, files: await api.files(hash) } } catch { /* ignore */ }
    handleApiError(err)
  }
}

function renderTrackersDetail() {
  const trackers = state.detail.trackers
  if (trackers.length === 0) return emptyState('No tracker data', 'Waiting for tracker information.')

  const rows = trackers.map((tr) => el('tr', {}, [
    el('td', { class: 'primary-cell', style: { wordBreak: 'break-all' } }, [tr.url || '—']),
    el('td', {}, [pill(trackerStatusText(tr.status), trackerStatusTone(tr.status))]),
    el('td', { class: 'mono-cell' }, [tr.tier ?? '—']),
    el('td', { class: 'mono-cell' }, [tr.numSeeds ?? '—']),
    el('td', { class: 'mono-cell' }, [tr.numPeers ?? '—']),
    el('td', { class: 'mono-cell' }, [tr.numLeechers ?? '—']),
    el('td', {}, [tr.message || '—']),
  ]))

  return el('div', { class: 'table-shell' }, [
    el('table', { class: 'data-table', style: { minWidth: '800px' } }, [
      el('thead', {}, [el('tr', {}, ['URL', 'Status', 'Tier', 'Seeds', 'Peers', 'Leechers', 'Message'].map((h) => el('th', {}, [h])))]),
      el('tbody', {}, rows),
    ]),
  ])
}

function renderPeersDetail() {
  const peers = state.detail.peers
  if (peers.length === 0) return emptyState('No peer data', 'No peers connected to this torrent right now.')

  const rows = peers.map((p) => el('tr', {}, [
    el('td', { class: 'mono-cell' }, [p.ip || '—']),
    el('td', {}, [p.client || '—']),
    el('td', { class: 'mono-cell' }, [fmtPercent(p.progress)]),
    el('td', { class: 'mono-cell' }, [fmtRate(p.downloadRate)]),
    el('td', { class: 'mono-cell' }, [fmtRate(p.uploadRate)]),
    el('td', { class: 'mono-cell' }, [p.flags || '—']),
  ]))

  return el('div', { class: 'table-shell' }, [
    el('table', { class: 'data-table', style: { minWidth: '700px' } }, [
      el('thead', {}, [el('tr', {}, ['IP', 'Client', 'Progress', 'Download', 'Upload', 'Flags'].map((h) => el('th', {}, [h])))]),
      el('tbody', {}, rows),
    ]),
  ])
}

/* ---- Categories ---------------------------------------------------------- */

function renderCategories() {
  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [
        el('h2', {}, ['Categories']),
        el('p', {}, ['Route downloads to scratch storage, final libraries, and per-category seeding limits.']),
      ]),
      el('div', { class: 'button-row' }, [
        el('button', { type: 'button', class: 'primary', onClick: () => { state.categoryModal = { mode: 'new' }; render() } }, ['New category']),
      ]),
    ]),
    state.categories.length === 0
      ? emptyState('No categories configured', 'Create one to define save paths, archive extraction, blocked extensions, and seeding overrides.')
      : renderCategoryTable(),
  ])
}

function renderCategoryTable() {
  const rows = state.categories.map((c) => el('tr', {}, [
    el('td', { class: 'primary-cell' }, [c.name]),
    el('td', { class: 'mono-cell' }, [c.savePath]),
    el('td', { class: 'mono-cell' }, [c.completePath || '—']),
    el('td', {}, [c.extractArchives ? pill('Yes', 'green') : pill('No', 'neutral')]),
    el('td', {}, [c.blockedExtensions.join(', ') || '—']),
    el('td', {}, [fmtOptionalNumber(c.maxRatio)]),
    el('td', {}, [fmtOptionalNumber(c.maxSeedingTimeMinutes, ' min')]),
    el('td', {}, [
      el('div', { class: 'action-cluster' }, [
        el('button', { type: 'button', onClick: () => { state.categoryModal = { mode: 'edit', category: c }; render() } }, ['Edit']),
        el('button', { type: 'button', class: 'danger', onClick: () => {
          if (window.confirm(`Delete category "${c.name}"?`)) runAction(() => api.deleteCategory(c.name))
        } }, ['Delete']),
      ]),
    ]),
  ]))

  return el('div', { class: 'table-shell' }, [
    el('table', { class: 'data-table' }, [
      el('thead', {}, [el('tr', {}, ['Name', 'Save path', 'Complete path', 'Extract', 'Blocked extensions', 'Max ratio', 'Max seed time', 'Actions'].map((h) => el('th', {}, [h])))]),
      el('tbody', {}, rows),
    ]),
  ])
}

function renderCategoryModal() {
  const modal = state.categoryModal
  const isNew = modal.mode === 'new'
  const initial = modal.mode === 'edit' ? modal.category : null

  const draft = {
    name: initial?.name ?? '',
    savePath: initial?.savePath ?? state.draftSettings?.defaultSavePath ?? (state.categories[0]?.savePath ?? ''),
    completePath: initial?.completePath ?? '',
    extractArchives: initial?.extractArchives ?? false,
    blockedExtensions: (initial?.blockedExtensions ?? []).join(', '),
    hasMaxRatio: initial?.maxRatio != null,
    maxRatio: initial?.maxRatio ?? 2,
    hasMaxSeedTime: initial?.maxSeedingTimeMinutes != null,
    maxSeedTime: initial?.maxSeedingTimeMinutes ?? 4320,
  }
  let modalError = null

  const nameInput = el('input', { value: draft.name, oninput: (e) => { draft.name = e.currentTarget.value } })
  if (!isNew) nameInput.disabled = true
  const savePathInput = el('input', { value: draft.savePath, oninput: (e) => { draft.savePath = e.currentTarget.value } })
  const completePathInput = el('input', { value: draft.completePath, oninput: (e) => { draft.completePath = e.currentTarget.value } })
  const blockedInput = el('input', { value: draft.blockedExtensions, placeholder: '.nfo, .txt, sample.mkv', oninput: (e) => { draft.blockedExtensions = e.currentTarget.value } })

  const ratioWrap = el('div', {})
  const seedWrap = el('div', {})

  function renderRatioField() {
    clear(ratioWrap)
    if (!draft.hasMaxRatio) return
    ratioWrap.appendChild(field('Max ratio', el('input', {
      type: 'number', step: '0.25', min: '0', value: draft.maxRatio,
      oninput: (e) => { draft.maxRatio = readNum(e.currentTarget.valueAsNumber, draft.maxRatio) },
    })))
  }
  function renderSeedField() {
    clear(seedWrap)
    if (!draft.hasMaxSeedTime) return
    seedWrap.appendChild(field('Max seed time (min)', el('input', {
      type: 'number', step: '60', min: '0', value: draft.maxSeedTime,
      oninput: (e) => { draft.maxSeedTime = readNum(e.currentTarget.valueAsNumber, draft.maxSeedTime) },
    })))
  }
  renderRatioField()
  renderSeedField()

  const errorSlot = el('div', {})

  const form = el('form', {
    class: 'modal-card',
    onClick: (e) => e.stopPropagation(),
    onSubmit: async (e) => {
      e.preventDefault()
      const name = draft.name.trim()
      const savePath = draft.savePath.trim()
      if (!name || !savePath) {
        clear(errorSlot)
        errorSlot.appendChild(el('div', { class: 'banner error' }, ['Name and save path are required.']))
        return
      }
      try {
        await api.saveCategory({
          name,
          savePath,
          completePath: draft.completePath.trim() || '',
          extractArchives: draft.extractArchives,
          blockedExtensions: draft.blockedExtensions.split(',').map((s) => s.trim()).filter(Boolean),
          maxRatio: draft.hasMaxRatio ? draft.maxRatio : null,
          maxSeedingTimeMinutes: draft.hasMaxSeedTime ? draft.maxSeedTime : null,
        })
        state.categoryModal = null
        setFlash('success', isNew ? 'Category created' : 'Category updated')
        await loadLive()
      } catch (err) {
        clear(errorSlot)
        errorSlot.appendChild(el('div', { class: 'banner error' }, [(err && err.message) || String(err)]))
      }
    },
  }, [
    el('div', { class: 'modal-header' }, [
      el('div', {}, [
        el('div', { class: 'brand-kicker' }, [isNew ? 'New category' : 'Edit category']),
        el('h2', {}, [isNew ? 'Create a routing profile' : initial.name]),
      ]),
      el('button', { type: 'button', onClick: () => { state.categoryModal = null; render() } }, ['Close']),
    ]),
    el('div', { class: 'form-grid' }, [
      field('Name', nameInput),
      field('Save path', savePathInput),
      el('label', { class: 'field wide' }, [el('span', {}, ['Complete path (optional)']), completePathInput]),
      toggleField('Extract archives (.rar/.zip/.7z)', draft.extractArchives, (v) => { draft.extractArchives = v }),
      el('label', { class: 'field wide' }, [el('span', {}, ['Blocked extensions (comma separated)']), blockedInput]),
      toggleField('Override max ratio', draft.hasMaxRatio, (v) => { draft.hasMaxRatio = v; if (v && draft.maxRatio <= 0) draft.maxRatio = 2; renderRatioField() }),
      ratioWrap,
      toggleField('Override max seed time', draft.hasMaxSeedTime, (v) => { draft.hasMaxSeedTime = v; if (v && draft.maxSeedTime < 0) draft.maxSeedTime = 4320; renderSeedField() }),
      seedWrap,
    ]),
    errorSlot,
    el('div', { class: 'button-row' }, [
      el('button', { type: 'button', onClick: () => { state.categoryModal = null; render() } }, ['Cancel']),
      el('button', { type: 'submit', class: 'primary' }, ['Save category']),
    ]),
  ])

  return el('div', { class: 'modal-backdrop', onClick: () => { state.categoryModal = null; render() } }, [form])
}

/* ---- Settings ------------------------------------------------------------ */

function patchSettings(patch) {
  state.draftSettings = { ...state.draftSettings, ...patch }
  state.settingsDirty = true
  render()
}

function renderSettings() {
  const s = state.draftSettings
  if (!s) return emptyState('Loading settings…', 'Fetching the current configuration from the server.')

  const portInput = (value, placeholder, onChange) => el('input', {
    type: 'text', inputmode: 'numeric', pattern: '[0-9]*', value: String(value), placeholder,
    oninput: (e) => {
      const digits = e.currentTarget.value.replace(/[^\d]/g, '')
      e.currentTarget.value = digits
      if (digits) onChange(clampPort(digits))
    },
  })

  const numberInput = (value, attrs, onChange) => el('input', {
    type: 'number', ...attrs, value,
    oninput: (e) => onChange(readNum(e.currentTarget.valueAsNumber, value)),
  })

  const webuiPanel = settingsPanel('WebUI', 'Binding for the browser admin interface. Use 0.0.0.0 for LAN access from Sonarr/Radarr/Overseerr on another machine, then restart Controllarr.', [
    el('div', { class: 'form-grid' }, [
      field('Bind host', el('input', { value: s.webUIHost, oninput: (e) => { s.webUIHost = e.currentTarget.value; state.settingsDirty = true } })),
      field('Port', portInput(s.webUIPort, '8791', (v) => { s.webUIPort = v; state.settingsDirty = true })),
      field('Username', el('input', { value: s.webUIUsername, oninput: (e) => { s.webUIUsername = e.currentTarget.value; state.settingsDirty = true } })),
      field('Password', el('input', { type: 'password', placeholder: 'Leave blank to keep current password', value: s.webUIPassword, oninput: (e) => { s.webUIPassword = e.currentTarget.value; state.settingsDirty = true } })),
    ]),
  ])

  const portPanel = settingsPanel('Listen port range', 'Set a VPN-forwarded preferred port first; PortWatcher falls back to this range if it goes stale.', [
    el('div', { class: 'form-grid' }, [
      optionalPortField('Preferred forwarded port', s.preferredListenPort,
        (enabled) => patchSettings({ preferredListenPort: enabled ? (s.preferredListenPort ?? s.listenPortRangeStart) : null }),
        (v) => patchSettings({ preferredListenPort: v })),
      field('Range start', portInput(s.listenPortRangeStart, '49152', (v) => { s.listenPortRangeStart = v; state.settingsDirty = true })),
      field('Range end', portInput(s.listenPortRangeEnd, '65000', (v) => { s.listenPortRangeEnd = v; state.settingsDirty = true })),
      field('Port stall threshold (min)', numberInput(s.stallThresholdMinutes, { min: 0 }, (v) => { s.stallThresholdMinutes = v; state.settingsDirty = true })),
    ]),
    el('div', { class: 'button-row' }, [
      el('button', { type: 'button', onClick: () => runAction(() => api.cyclePort()).then(() => setFlash('success', 'Port cycle requested')) }, ['Cycle port now']),
    ]),
  ])

  const storagePanel = settingsPanel('Storage defaults', 'Used when a torrent or category does not override its own save path.', [
    el('label', { class: 'field' }, [el('span', {}, ['Default save path']),
      el('input', { value: s.defaultSavePath, placeholder: 'C:\\Users\\you\\Downloads\\Controllarr', oninput: (e) => { s.defaultSavePath = e.currentTarget.value; state.settingsDirty = true } })]),
  ])

  const seedSelect = el('select', { value: s.seedLimitAction, onChange: (e) => patchSettings({ seedLimitAction: e.currentTarget.value }) }, [
    el('option', { value: 'pause' }, ['Pause']),
    el('option', { value: 'remove_keep_files' }, ['Remove (keep files)']),
    el('option', { value: 'remove_delete_files' }, ['Remove (delete files)']),
  ])
  seedSelect.value = s.seedLimitAction

  const seedingPanel = settingsPanel('Seeding policy', 'Global limits apply unless a category overrides them.', [
    el('div', { class: 'form-grid' }, [
      field('When limit reached', seedSelect),
      optionalNumberField('Global max ratio', s.globalMaxRatio, { step: 0.25, min: 0 },
        (enabled) => patchSettings({ globalMaxRatio: enabled ? (s.globalMaxRatio ?? 2) : null }),
        (v) => patchSettings({ globalMaxRatio: v })),
      optionalNumberField('Global max seed time (min)', s.globalMaxSeedingTimeMinutes, { step: 60, min: 0 },
        (enabled) => patchSettings({ globalMaxSeedingTimeMinutes: enabled ? (s.globalMaxSeedingTimeMinutes ?? 4320) : null }),
        (v) => patchSettings({ globalMaxSeedingTimeMinutes: v })),
      field('Minimum seed time (min)', numberInput(s.minimumSeedTimeMinutes, { min: 0 }, (v) => { s.minimumSeedTimeMinutes = v; state.settingsDirty = true })),
    ]),
  ])

  const healthPanel = settingsPanel('Health monitor', 'Flag stalled torrents and optionally reannounce when a stall begins.', [
    el('div', { class: 'form-grid' }, [
      field('Stall threshold (min)', numberInput(s.healthStallMinutes, { min: 0 }, (v) => { s.healthStallMinutes = v; state.settingsDirty = true })),
      toggleField('Reannounce automatically on stall', s.healthReannounceOnStall, (v) => patchSettings({ healthReannounceOnStall: v })),
    ]),
  ])

  const form = el('form', {
    class: 'section-stack',
    onSubmit: async (e) => {
      e.preventDefault()
      state.loading = true
      render()
      try {
        await api.saveSettings(state.draftSettings)
        state.settingsDirty = false
        setFlash('success', 'Settings saved')
        await loadAll()
      } catch (err) {
        handleApiError(err)
      } finally {
        state.loading = false
        render()
      }
    },
  }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [
        el('h2', {}, ['Settings']),
        el('p', {}, ['These fields mirror the native Settings view and post back to the full server schema.']),
      ]),
      el('div', { class: 'section-meta' }, [state.settingsDirty ? 'Unsaved changes' : 'In sync']),
    ]),
    el('div', { class: 'settings-grid' }, [webuiPanel, portPanel, storagePanel, seedingPanel, healthPanel]),
    renderRecoveryRulesSection(s),
    renderVpnSection(s),
    renderDiskSection(s),
    renderArrSection(s),
    renderBandwidthSection(s),
    renderBackupSection(),
    el('div', { class: 'button-row' }, [
      el('button', { type: 'submit', class: 'primary' }, ['Save settings']),
      el('button', { type: 'button', onClick: () => {
        if (state.serverSettings) { state.draftSettings = deepClone(state.serverSettings); state.settingsDirty = false; render() }
      } }, ['Revert']),
    ]),
  ])

  return form
}

function settingsPanel(title, desc, children) {
  return el('section', { class: 'form-panel' }, [
    el('div', { class: 'form-panel-header' }, [el('h3', {}, [title]), desc ? el('p', {}, [desc]) : null]),
    ...children,
  ])
}

function optionalPortField(label, value, onToggle, onValueChange) {
  const children = [toggleField(label, value !== null, onToggle)]
  if (value !== null) {
    children.push(field('Port', el('input', {
      type: 'text', inputmode: 'numeric', pattern: '[0-9]*', value: String(value), placeholder: '53127',
      oninput: (e) => { const d = e.currentTarget.value.replace(/[^\d]/g, ''); e.currentTarget.value = d; if (d) onValueChange(clampPort(d)) },
    })))
  }
  return el('div', { class: 'optional-field' }, children)
}

function optionalNumberField(label, value, attrs, onToggle, onValueChange) {
  const children = [toggleField(label, value !== null, onToggle)]
  if (value !== null) {
    children.push(field('Value', el('input', {
      type: 'number', step: attrs.step, min: attrs.min, value,
      oninput: (e) => onValueChange(readNum(e.currentTarget.valueAsNumber, value)),
    })))
  }
  return el('div', { class: 'optional-field' }, children)
}

function renderRecoveryRulesSection(s) {
  const rules = s.recoveryRules
  const body = rules.length === 0
    ? emptyState('No recovery rules configured', 'Add a rule to automatically reannounce, pause, or remove torrents that stay unhealthy.')
    : el('div', { class: 'section-stack' }, rules.map((rule, idx) => {
      const triggerSel = el('select', { value: rule.trigger, onChange: (e) => { rule.trigger = e.currentTarget.value; state.settingsDirty = true } },
        RECOVERY_TRIGGER_OPTIONS.map((tr) => el('option', { value: tr }, [friendlyHealthReason(tr)])))
      triggerSel.value = rule.trigger
      const actionSel = el('select', { value: rule.action, onChange: (e) => { rule.action = e.currentTarget.value; state.settingsDirty = true } },
        RECOVERY_ACTION_OPTIONS.map((ac) => el('option', { value: ac }, [friendlyRecoveryAction(ac)])))
      actionSel.value = rule.action
      return el('div', { class: 'form-panel nested' }, [
        el('div', { class: 'form-grid' }, [
          toggleField('Enabled', rule.enabled, (v) => { rule.enabled = v; state.settingsDirty = true }),
          field('Trigger', triggerSel),
          field('Action', actionSel),
          field('Delay (min)', el('input', { type: 'number', min: 0, step: 5, value: rule.delayMinutes, oninput: (e) => { rule.delayMinutes = Math.max(0, readNum(e.currentTarget.valueAsNumber, rule.delayMinutes)); state.settingsDirty = true } })),
        ]),
        el('div', { class: 'button-row' }, [
          el('button', { type: 'button', class: 'danger', onClick: () => patchSettings({ recoveryRules: rules.filter((_, i) => i !== idx) }) }, ['Remove rule']),
        ]),
      ])
    }))

  return settingsPanel('Recovery rules', 'Only the first enabled rule per health reason runs automatically; manual recovery is always available.', [
    body,
    el('div', { class: 'button-row' }, [
      el('button', { type: 'button', onClick: () => {
        const used = new Set(rules.map((r) => r.trigger))
        const next = RECOVERY_TRIGGER_OPTIONS.find((o) => !used.has(o)) ?? 'metadata_timeout'
        patchSettings({ recoveryRules: [...rules, { id: cryptoId(), enabled: false, trigger: next, action: 'reannounce', delayMinutes: 15 }] })
      } }, ['Add recovery rule']),
    ]),
  ])
}

function renderVpnSection(s) {
  const children = [toggleField('Enable VPN monitoring', s.vpnEnabled, (v) => patchSettings({ vpnEnabled: v }))]
  if (s.vpnEnabled) {
    children.push(el('div', { class: 'form-grid' }, [
      toggleField('Kill switch (pause all when VPN drops)', s.vpnKillSwitch, (v) => patchSettings({ vpnKillSwitch: v })),
      toggleField('Bind to VPN interface (prevent leaks)', s.vpnBindInterface, (v) => patchSettings({ vpnBindInterface: v })),
      field('Interface prefix', el('input', { value: s.vpnInterfacePrefix, placeholder: 'TAP', oninput: (e) => { s.vpnInterfacePrefix = e.currentTarget.value; state.settingsDirty = true } })),
      field('Check interval (seconds)', el('input', { type: 'number', min: 1, max: 60, value: s.vpnMonitorIntervalSeconds, oninput: (e) => { s.vpnMonitorIntervalSeconds = Math.max(1, readNum(e.currentTarget.valueAsNumber, 5)); state.settingsDirty = true } })),
    ]))
    if (state.vpn) {
      const v = state.vpn
      children.push(el('div', { class: 'status-pill-row' }, [
        pill(v.isConnected ? 'Connected' : 'Disconnected', v.isConnected ? 'green' : 'red'),
        v.isConnected ? pill(`${v.interfaceName} (${v.interfaceIP})${v.boundToVPN ? ' — bound' : ''}`, 'blue') : null,
        (!v.isConnected && v.killSwitchEngaged) ? pill(`Kill switch active (${v.pausedCount} paused)`, 'amber') : null,
      ].filter(Boolean)))
    }
  }
  return settingsPanel('VPN protection', 'Detect TAP-Windows / WireGuard / Wintun adapters, engage a kill switch, and bind the engine to the tunnel.', children)
}

function renderDiskSection(s) {
  const enabled = s.diskSpaceMinimumGB !== null
  const children = [toggleField('Enable disk space monitor', enabled, (v) => patchSettings({ diskSpaceMinimumGB: v ? 10 : null }))]
  if (enabled) {
    children.push(el('div', { class: 'form-grid' }, [
      field('Minimum free space (GB)', el('input', { type: 'number', min: 1, step: 1, value: s.diskSpaceMinimumGB ?? 10, oninput: (e) => { s.diskSpaceMinimumGB = readNum(e.currentTarget.valueAsNumber, s.diskSpaceMinimumGB ?? 10); state.settingsDirty = true } })),
      el('label', { class: 'field wide' }, [el('span', {}, ['Monitor path']), el('input', { value: s.diskSpaceMonitorPath, placeholder: 'D:\\Media or C:\\Users\\you\\Downloads', oninput: (e) => { s.diskSpaceMonitorPath = e.currentTarget.value; state.settingsDirty = true } })]),
    ]))
  }
  if (state.disk) {
    const d = state.disk
    children.push(el('div', { class: 'form-grid' }, [
      el('div', { class: 'field' }, [el('span', {}, ['Monitored path']), el('strong', { class: 'mono-cell' }, [d.monitorPath || '—'])]),
      el('div', { class: 'field' }, [el('span', {}, ['Current free space']), el('strong', {}, [fmtBytes(d.freeBytes)])]),
      el('div', { class: 'field' }, [el('span', {}, ['Threshold']), el('strong', {}, [fmtBytes(d.thresholdBytes)])]),
      el('div', { class: 'field' }, [el('span', {}, ['Status']), d.isPaused ? pill(`Paused (${d.pausedCount})`, 'amber') : pill('OK', 'green')]),
      el('div', { class: 'field' }, [el('span', {}, ['Operator action']),
        el('button', { type: 'button', onClick: async () => { try { state.disk = await api.recheckDiskSpace(); setFlash('success', 'Disk space rechecked'); render() } catch (err) { handleApiError(err) } } }, ['Recheck now'])]),
      d.shortfallBytes > 0 ? el('div', { class: 'field' }, [el('span', {}, ['Space still needed']), el('strong', {}, [fmtBytes(d.shortfallBytes)])]) : null,
    ].filter(Boolean)))
  }
  return settingsPanel('Disk space monitor', 'Pause new downloads when free space drops below a threshold on the monitored volume.', children)
}

function renderArrSection(s) {
  const endpoints = s.arrEndpoints
  const children = [
    el('div', { class: 'form-grid' }, [
      field('Re-search after (hours)', el('input', { type: 'number', min: 0, step: 1, value: s.arrReSearchAfterHours, oninput: (e) => { s.arrReSearchAfterHours = readNum(e.currentTarget.valueAsNumber, s.arrReSearchAfterHours); state.settingsDirty = true } })),
    ]),
  ]
  children.push(endpoints.length === 0
    ? emptyState('No *arr endpoints', 'Add a Sonarr or Radarr endpoint to enable automatic re-search on failed grabs.')
    : el('div', { class: 'section-stack' }, endpoints.map((ep, idx) => {
      const kindSel = el('select', { value: ep.kind, onChange: (e) => { ep.kind = e.currentTarget.value; state.settingsDirty = true } }, [
        el('option', { value: 'sonarr' }, ['Sonarr']),
        el('option', { value: 'radarr' }, ['Radarr']),
      ])
      kindSel.value = ep.kind
      return el('div', { class: 'form-panel nested' }, [
        el('div', { class: 'form-grid' }, [
          field('Name', el('input', { value: ep.name, placeholder: 'e.g. Sonarr', oninput: (e) => { ep.name = e.currentTarget.value; state.settingsDirty = true } })),
          field('Type', kindSel),
          el('label', { class: 'field wide' }, [el('span', {}, ['Base URL']), el('input', { value: ep.baseURL, placeholder: 'http://localhost:8989', oninput: (e) => { ep.baseURL = e.currentTarget.value; state.settingsDirty = true } })]),
          el('label', { class: 'field wide' }, [el('span', {}, ['API Key']), el('input', { type: 'password', value: ep.apiKey, placeholder: 'Leave blank to keep existing key', oninput: (e) => { ep.apiKey = e.currentTarget.value; state.settingsDirty = true } })]),
        ]),
        el('div', { class: 'button-row' }, [
          el('button', { type: 'button', class: 'danger', onClick: () => patchSettings({ arrEndpoints: endpoints.filter((_, i) => i !== idx) }) }, ['Remove endpoint']),
        ]),
      ])
    })))
  children.push(el('div', { class: 'button-row' }, [
    el('button', { type: 'button', onClick: () => patchSettings({ arrEndpoints: [...endpoints, { name: '', kind: 'sonarr', baseURL: '', apiKey: '' }] }) }, ['Add endpoint']),
  ]))

  // Recent re-search activity table.
  if (state.arr.length > 0) {
    const rows = state.arr.slice().sort((a, b) => toEpochSeconds(b.timestamp) - toEpochSeconds(a.timestamp)).map((n) => el('tr', {}, [
      el('td', { class: 'primary-cell' }, [n.name]),
      el('td', {}, [n.endpoint]),
      el('td', {}, [pill(n.success ? 'Success' : 'Failed', n.success ? 'green' : 'red')]),
      el('td', {}, [n.message || '—']),
      el('td', { title: fmtDateTime(n.timestamp) }, [fmtRelativeTime(n.timestamp)]),
    ]))
    children.push(el('div', { class: 'form-panel-header', style: { marginTop: '0.5rem' } }, [el('h3', {}, ['Recent re-search activity'])]))
    children.push(el('div', { class: 'table-shell' }, [
      el('table', { class: 'data-table' }, [
        el('thead', {}, [el('tr', {}, ['Torrent', 'Endpoint', 'Result', 'Message', 'When'].map((h) => el('th', {}, [h])))]),
        el('tbody', {}, rows),
      ]),
    ]))
  }

  return settingsPanel('*arr integration', 'Automatically re-search failed grabs in Sonarr/Radarr after a configurable delay.', children)
}

function renderBandwidthSection(s) {
  const rules = s.bandwidthSchedule
  const body = rules.length === 0
    ? emptyState('No bandwidth rules', 'Add a schedule rule to throttle speeds during specific windows.')
    : el('div', { class: 'section-stack' }, rules.map((rule, idx) => {
      const dayButtons = el('div', { class: 'button-row' }, [1, 2, 3, 4, 5, 6, 7].map((day) =>
        el('button', {
          type: 'button',
          class: rule.daysOfWeek.includes(day) ? 'primary' : undefined,
          style: { minWidth: '3.2rem', padding: '0.5rem 0.6rem' },
          onClick: () => {
            rule.daysOfWeek = rule.daysOfWeek.includes(day)
              ? rule.daysOfWeek.filter((d) => d !== day)
              : [...rule.daysOfWeek, day].sort((a, b) => a - b)
            state.settingsDirty = true
            render()
          },
        }, [DAY_LABELS[day]])))
      return el('div', { class: 'form-panel nested' }, [
        el('div', { class: 'form-grid' }, [
          field('Rule name', el('input', { value: rule.name, placeholder: 'e.g. Work hours', oninput: (e) => { rule.name = e.currentTarget.value; state.settingsDirty = true } })),
          toggleField('Enabled', rule.enabled, (v) => { rule.enabled = v; state.settingsDirty = true }),
          el('div', { class: 'field wide' }, [el('span', {}, ['Days of week']), dayButtons]),
          field('Start hour', el('input', { type: 'number', min: 0, max: 23, value: rule.startHour, oninput: (e) => { rule.startHour = readNum(e.currentTarget.valueAsNumber, rule.startHour); state.settingsDirty = true } })),
          field('Start minute', el('input', { type: 'number', min: 0, max: 59, value: rule.startMinute, oninput: (e) => { rule.startMinute = readNum(e.currentTarget.valueAsNumber, rule.startMinute); state.settingsDirty = true } })),
          field('End hour', el('input', { type: 'number', min: 0, max: 23, value: rule.endHour, oninput: (e) => { rule.endHour = readNum(e.currentTarget.valueAsNumber, rule.endHour); state.settingsDirty = true } })),
          field('End minute', el('input', { type: 'number', min: 0, max: 59, value: rule.endMinute, oninput: (e) => { rule.endMinute = readNum(e.currentTarget.valueAsNumber, rule.endMinute); state.settingsDirty = true } })),
          field('Max download (KB/s)', el('input', { type: 'number', min: 0, value: rule.maxDownloadKBps, oninput: (e) => { rule.maxDownloadKBps = readNum(e.currentTarget.valueAsNumber, rule.maxDownloadKBps); state.settingsDirty = true } })),
          field('Max upload (KB/s)', el('input', { type: 'number', min: 0, value: rule.maxUploadKBps, oninput: (e) => { rule.maxUploadKBps = readNum(e.currentTarget.valueAsNumber, rule.maxUploadKBps); state.settingsDirty = true } })),
        ]),
        el('div', { class: 'button-row' }, [
          el('button', { type: 'button', class: 'danger', onClick: () => patchSettings({ bandwidthSchedule: rules.filter((_, i) => i !== idx) }) }, ['Remove rule']),
        ]),
      ])
    }))

  return settingsPanel('Bandwidth schedule', 'Define time-based rules that limit download and upload speeds on specific days.', [
    body,
    el('div', { class: 'button-row' }, [
      el('button', { type: 'button', onClick: () => patchSettings({ bandwidthSchedule: [...rules, { name: '', enabled: true, daysOfWeek: [2, 3, 4, 5, 6], startHour: 9, startMinute: 0, endHour: 17, endMinute: 0, maxDownloadKBps: 0, maxUploadKBps: 0 }] }) }, ['Add schedule rule']),
    ]),
  ])
}

function renderBackupSection() {
  const fileInput = el('input', { type: 'file', accept: 'application/json,.json' })
  return settingsPanel('Backup & restore', 'Export the full Controllarr state or restore it on this machine.', [
    el('label', { class: 'field wide' }, [el('span', {}, ['Restore from backup JSON']), fileInput]),
    el('div', { class: 'button-row' }, [
      el('button', { type: 'button', onClick: async () => {
        try {
          const { blob, filename } = await api.exportBackup()
          const url = URL.createObjectURL(blob)
          const link = el('a', { href: url, download: filename })
          document.body.appendChild(link)
          link.click()
          link.remove()
          URL.revokeObjectURL(url)
          setFlash('success', 'Backup downloaded')
        } catch (err) { handleApiError(err) }
      } }, ['Export backup']),
      el('button', { type: 'button', class: 'primary', onClick: async () => {
        const file = fileInput.files?.[0]
        if (!file) { setFlash('error', 'Choose a backup file before importing.'); return }
        if (!window.confirm('Importing a backup replaces the current settings and categories. Continue?')) return
        try {
          const text = await file.text()
          await api.importBackup(text)
          state.settingsDirty = false
          setFlash('success', 'Backup imported. Restart Controllarr if host/port changed.')
          await loadAll()
        } catch (err) { handleApiError(err) }
      } }, ['Import backup']),
    ]),
  ])
}

/* ---- Health -------------------------------------------------------------- */

function renderHealth() {
  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [el('h2', {}, ['Health']), el('p', {}, ['HealthMonitor snapshots with reason labels and one-click clear/recover actions.'])]),
      el('div', { class: 'section-meta' }, [`${state.health.length} open`]),
    ]),
    state.health.length === 0
      ? emptyState('All torrents healthy', 'No stalled or flagged torrents right now.')
      : el('div', { class: 'table-shell' }, [
        el('table', { class: 'data-table' }, [
          el('thead', {}, [el('tr', {}, ['Torrent', 'Reason', 'Progress', 'First seen', 'Updated', 'Actions'].map((h) => el('th', {}, [h])))]),
          el('tbody', {}, state.health.map((issue) => el('tr', {}, [
            el('td', { class: 'primary-cell' }, [issue.name]),
            el('td', {}, [pill(issue.reason, 'amber')]),
            el('td', { class: 'mono-cell' }, [fmtPercent(issue.lastProgress)]),
            el('td', { title: fmtDateTime(issue.firstSeen) }, [fmtRelativeTime(issue.firstSeen)]),
            el('td', { title: fmtDateTime(issue.lastUpdated) }, [fmtRelativeTime(issue.lastUpdated)]),
            el('td', {}, [el('div', { class: 'action-cluster' }, [
              el('button', { type: 'button', onClick: () => runAction(() => api.runRecovery(issue.infoHash), { refreshAll: true }).then(() => setFlash('success', 'Recovery triggered')) }, ['Recover now']),
              el('button', { type: 'button', onClick: () => runAction(() => api.clearHealth(issue.infoHash)) }, ['Clear']),
            ])]),
          ]))),
        ]),
      ]),
  ])
}

/* ---- Recovery ------------------------------------------------------------ */

function renderRecovery() {
  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [el('h2', {}, ['Recovery']), el('p', {}, ['Automatic and manual recovery attempts driven by configured health rules.'])]),
      el('div', { class: 'section-meta' }, [`${state.recovery.length}`]),
    ]),
    state.recovery.length === 0
      ? emptyState('No recovery actions yet', 'Configured rules and manual recoveries appear here once Controllarr responds to unhealthy torrents.')
      : el('div', { class: 'table-shell' }, [
        el('table', { class: 'data-table' }, [
          el('thead', {}, [el('tr', {}, ['Torrent', 'Reason', 'Action', 'Source', 'Status', 'When', 'Message'].map((h) => el('th', {}, [h])))]),
          el('tbody', {}, state.recovery.map((r) => el('tr', {}, [
            el('td', { class: 'primary-cell' }, [r.name]),
            el('td', {}, [pill(friendlyHealthReason(r.reason), 'amber')]),
            el('td', {}, [friendlyRecoveryAction(r.action)]),
            el('td', {}, [r.source === 'automatic' ? 'Automatic' : 'Manual']),
            el('td', {}, [pill(r.success ? 'Applied' : 'Failed', r.success ? 'green' : 'red')]),
            el('td', { title: fmtDateTime(r.timestamp) }, [fmtRelativeTime(r.timestamp)]),
            el('td', {}, [r.message || '—']),
          ]))),
        ]),
      ]),
  ])
}

/* ---- Post-Processor ------------------------------------------------------ */

function renderPostProcessor() {
  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [el('h2', {}, ['Post-Processor']), el('p', {}, ['Move and extraction pipeline for completed torrents, with manual retries for failed records.'])]),
      el('div', { class: 'section-meta' }, [`${state.postProcessor.length}`]),
    ]),
    state.postProcessor.length === 0
      ? emptyState('No post-processing activity yet', 'Finished torrents appear here once they start moving storage or extracting archives.')
      : el('div', { class: 'table-shell' }, [
        el('table', { class: 'data-table' }, [
          el('thead', {}, [el('tr', {}, ['Torrent', 'Category', 'Stage', 'Updated', 'Message', 'Actions'].map((h) => el('th', {}, [h])))]),
          el('tbody', {}, state.postProcessor.map((r) => el('tr', {}, [
            el('td', { class: 'primary-cell' }, [r.name]),
            el('td', {}, [r.category || '—']),
            el('td', {}, [pill(r.stage, stageTone(r.stageKey))]),
            el('td', { title: fmtDateTime(r.lastUpdated) }, [fmtRelativeTime(r.lastUpdated)]),
            el('td', {}, [r.message || '—']),
            el('td', {}, [r.canRetry
              ? el('button', { type: 'button', onClick: () => runAction(() => api.retryPostProcessor(r.infoHash)).then(() => setFlash('success', 'Post-processing retry queued')) }, ['Retry'])
              : '—']),
          ]))),
        ]),
      ]),
  ])
}

/* ---- Seeding ------------------------------------------------------------- */

function renderSeeding() {
  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [el('h2', {}, ['Seeding']), el('p', {}, ['Read-only enforcement history for ratio and seed-time limits.'])]),
      el('div', { class: 'section-meta' }, [`${state.seeding.length}`]),
    ]),
    state.seeding.length === 0
      ? emptyState('No seeding-policy actions yet', 'Once a torrent crosses a configured ratio or time limit, the action appears here.')
      : el('div', { class: 'table-shell' }, [
        el('table', { class: 'data-table' }, [
          el('thead', {}, [el('tr', {}, ['Torrent', 'Action', 'Reason', 'When'].map((h) => el('th', {}, [h])))]),
          el('tbody', {}, state.seeding.map((r) => el('tr', {}, [
            el('td', { class: 'primary-cell' }, [r.name]),
            el('td', {}, [pill(friendlySeedAction(r.action), r.action === 'pause' ? 'blue' : 'amber')]),
            el('td', {}, [r.reason]),
            el('td', { title: fmtDateTime(r.timestamp) }, [fmtRelativeTime(r.timestamp)]),
          ]))),
        ]),
      ]),
  ])
}

/* ---- Log ----------------------------------------------------------------- */

function renderLog() {
  const normalizedFilter = state.logFilter.trim().toLowerCase()
  const visible = state.log.filter((e) => {
    if (state.logLevel !== 'all' && e.level !== state.logLevel) return false
    if (!normalizedFilter) return true
    return e.message.toLowerCase().includes(normalizedFilter) ||
      e.source.toLowerCase().includes(normalizedFilter) ||
      e.level.toLowerCase().includes(normalizedFilter)
  })

  const search = persistFocus(el('input', {
    value: state.logFilter, placeholder: 'message, source, or level',
    oninput: (e) => { state.logFilter = e.currentTarget.value; render() },
  }), 'log-search')

  const levelSel = el('select', { value: state.logLevel, onChange: (e) => { state.logLevel = e.currentTarget.value; render() } }, [
    el('option', { value: 'all' }, ['All levels']),
    el('option', { value: 'debug' }, ['Debug']),
    el('option', { value: 'info' }, ['Info']),
    el('option', { value: 'warn' }, ['Warn']),
    el('option', { value: 'error' }, ['Error']),
  ])
  levelSel.value = state.logLevel

  return el('div', { class: 'section-stack' }, [
    el('header', { class: 'section-header' }, [
      el('div', {}, [el('h2', {}, ['Log']), el('p', {}, ['Latest 500 entries from the in-app ring buffer, refreshed every two seconds.'])]),
      el('div', { class: 'section-meta' }, [`${visible.length} shown`]),
    ]),
    el('div', { class: 'toolbar-row' }, [
      el('label', { class: 'field search-field' }, [el('span', {}, ['Filter']), search]),
      el('label', { class: 'field' }, [el('span', {}, ['Level']), levelSel]),
    ]),
    visible.length === 0
      ? emptyState('No matching log entries', 'Try a broader filter or wait for the next refresh.')
      : el('div', { class: 'log-stream' }, visible.map((e) => el('div', { class: 'log-row' }, [
        el('span', { class: 'log-time' }, [fmtDateTime(e.timestamp)]),
        el('span', { class: `log-level ${e.level}` }, [e.level.toUpperCase()]),
        el('span', { class: 'log-source' }, [`[${e.source}]`]),
        el('span', { class: 'log-message' }, [e.message]),
      ]))),
  ])
}

/* ===========================================================================
 * Misc helpers
 * ======================================================================== */

function readNum(value, fallback) {
  return Number.isFinite(value) ? value : fallback
}

function deepClone(obj) {
  return JSON.parse(JSON.stringify(obj))
}

// Focus preservation across full re-renders. Because the whole tree is rebuilt
// on every poll (~2s), a search box that the user is typing into would lose
// focus. We tag persistent inputs with data-focus-key; just before re-rendering
// we capture which key was focused and its caret, then restore it afterward.
const focusState = { key: null, caret: 0 }

function captureFocus() {
  const active = document.activeElement
  if (active && active.dataset && active.dataset.focusKey) {
    focusState.key = active.dataset.focusKey
    focusState.caret = typeof active.selectionStart === 'number' ? active.selectionStart : 0
  } else {
    focusState.key = null
  }
}

function restoreFocus() {
  if (!focusState.key) return
  const target = root.querySelector(`[data-focus-key="${focusState.key}"]`)
  if (!target) return
  target.focus()
  try { target.setSelectionRange(focusState.caret, focusState.caret) } catch { /* non-text input */ }
}

// Mark an input as focus-persistent under the given key.
function persistFocus(input, key) {
  input.dataset.focusKey = key
  return input
}

/* ===========================================================================
 * Boot
 * ======================================================================== */

// Probe the session on load: if a valid SID cookie exists we skip the sign-in
// card and go straight to the dashboard.
async function boot() {
  render() // show sign-in immediately for fast first paint
  try {
    await api.stats() // any authenticated endpoint; 403 if not signed in
    state.authed = true
    render()
    startPolling()
    await loadAll()
  } catch (err) {
    if (err && (err.status === 403 || err.status === 0)) {
      state.authed = false
      render()
    } else {
      // Unexpected error - still present sign-in but note it.
      state.authed = false
      state.error = (err && err.message) || String(err)
      render()
    }
  }
}

// Keep polling cheap when the tab is hidden by pausing the fast loop.
document.addEventListener('visibilitychange', () => {
  if (!state.authed) return
  if (document.hidden) {
    clearPollTimers()
  } else {
    startPolling()
    void loadLive()
  }
})

boot()
