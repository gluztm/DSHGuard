/**
 * Client half of @gluztm/dsh-guard — Cross-platform read-only health & snapshot sidebar for DeepSeek Harness.
 *
 * Strictly zero-secret-leak: only renders structural bundle drift, cordis.patch.yml status,
 * masked .credentials.yaml key readiness, pre-change snapshot metadata, and the local Windows DSHGuard bridge.
 */
window.__ModuleLoader__.load({
  id: '@gluztm/dsh-guard',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const SIDE_ID = 'dsh-guard'
    const API_BASE = 'api/dsh-guard'
    const STYLE_ID = 'dsh-guard-panel-styles'
    const RELEASE_FALLBACK = 'https://github.com/gluztm/DSHGuard/releases/latest'

    const COPY = {
      zh: {
        brand: 'DSH 守护壳',
        subtitle: '双轨体检 · 脱敏对账 · 快照回望',
        refresh: '重新体检',
        refreshing: '体检中…',
        langSwitch: 'EN',
        healthy: '双轨配置健康',
        unhealthy: (n) => `发现 ${n} 项异常`,
        loading: '正在读取双轨配置文件与快照状态…',
        retry: '重试',
        profilesTitle: 'Web / 桌面双轨对账',
        webTrack: 'Web 版',
        desktopTrack: '桌面版',
        missingDir: '未检测到该端配置目录',
        bundlesLabel: '启用插件',
        depsLabel: '已装依赖',
        patchOk: (n) => (n > 0 ? `Patch 正常 · 禁用 ${n} 项` : 'Patch 结构正常'),
        patchMissing: '未建 cordis.patch.yml',
        patchErr: (e) => `Patch 异常：${e}`,
        pkgErr: 'package.json 解析异常',
        missingInDeps: '清单缺依赖',
        unregisteredDeps: '未登记插件',
        credsTitle: '凭据脱敏就绪度（零秘密读取）',
        credReadyBadge: (nonEmpty, total) => `.credentials.yaml ${nonEmpty}/${total} 非空键`,
        credMissingFile: '未创建 .credentials.yaml',
        credNoRefs: 'cordis.patch.yml 暂未引用任何 apiKeyEnv 环境变量',
        snapshotsTitle: '最近变更前快照',
        snapshotsTotal: (t) => `共 ${t} 份`,
        snapshotsEmpty: '暂无 DSHGuard 存档快照（仅展示桌面端变更前自动/手动快照）',
        snapItems: (n) => `${n} 项`,
        snapNoReason: '日常快照备份',
        guardTitle: 'Windows 进程外守护壳联动',
        openGuard: '唤起 DSHGuard 守护壳',
        openingGuard: '正在唤起…',
        guardReady: (p) => `本机已安装 DSHGuard：${p}`,
        guardNonWin: (plat) => `当前运行环境为 ${plat}，已开启跨平台只读体检；完整崩溃恢复与 CC Switch 导入工具运行于 Windows。`,
        guardNotInstalled: '当前仅启用 DSH 内只读体检；需要进程外崩溃救援、快照回滚或 CC Switch 导入时，可安装 Windows 完整版。',
        downloadRelease: '前往 GitHub Releases 下载 Windows 完整版',
        issuesTitle: '诊断建议',
        checkedAt: (t) => `最近体检：${t}`,
        errorRecover: '守护壳面板渲染已自动保护恢复',
        guideDesc: '双端配置体检 · 凭据键就绪状态 · 快照历史 · 唤起 DSHGuard',
      },
      en: {
        brand: 'DSHGuard',
        subtitle: 'Dual-Track Health · Masked Keys · Snapshots',
        refresh: 'Re-check',
        refreshing: 'Checking…',
        langSwitch: '中',
        healthy: 'Dual-Track Healthy',
        unhealthy: (n) => `${n} Issue${n === 1 ? '' : 's'} Found`,
        loading: 'Inspecting DSH profiles and snapshot history…',
        retry: 'Retry',
        profilesTitle: 'Web / Desktop Profile Alignment',
        webTrack: 'Web Track',
        desktopTrack: 'Desktop Track',
        missingDir: 'Profile directory not found',
        bundlesLabel: 'Bundles',
        depsLabel: 'Dependencies',
        patchOk: (n) => (n > 0 ? `Patch OK · ${n} disabled` : 'Patch OK'),
        patchMissing: 'No cordis.patch.yml',
        patchErr: (e) => `Patch error: ${e}`,
        pkgErr: 'Invalid package.json',
        missingInDeps: 'Missing in deps',
        unregisteredDeps: 'Unregistered deps',
        credsTitle: 'Masked Credential Readiness (Zero Secret Read)',
        credReadyBadge: (nonEmpty, total) => `.credentials.yaml ${nonEmpty}/${total} non-empty keys`,
        credMissingFile: '.credentials.yaml not found',
        credNoRefs: 'No apiKeyEnv references found in cordis.patch.yml',
        snapshotsTitle: 'Recent Pre-Change Snapshots',
        snapshotsTotal: (t) => `${t} total`,
        snapshotsEmpty: 'No DSHGuard snapshots found yet',
        snapItems: (n) => `${n} items`,
        snapNoReason: 'Checkpoint',
        guardTitle: 'Windows Out-of-Band Supervisor',
        openGuard: 'Launch DSHGuard App',
        openingGuard: 'Launching…',
        guardReady: (p) => `Installed locally: ${p}`,
        guardNonWin: (plat) => `Running on ${plat} (cross-platform read-only health panel active). The full out-of-band crash supervisor & CC Switch importer runs on Windows.`,
        guardNotInstalled: 'In-app read-only audit active. Install DSHGuard on Windows for out-of-band snapshot rollback & CC Switch import.',
        downloadRelease: 'Download Windows Supervisor on GitHub Releases',
        issuesTitle: 'Diagnostics',
        checkedAt: (t) => `Last check: ${t}`,
        errorRecover: 'Panel recovered by ErrorBoundary',
        guideDesc: 'Web/Desktop drift check · Masked credential readiness · Snapshots · DSHGuard launcher',
      },
    }

    const CSS = `
      .dshg-root {
        --dshg-card: var(--dsw-alias-card-fill, var(--dsw-alias-bg-layer-1, rgba(128, 128, 136, 0.08)));
        --dshg-card-hover: var(--dsw-alias-card-fill-hover, var(--dsw-alias-bg-layer-2, rgba(128, 128, 136, 0.14)));
        --dshg-fill: var(--dsw-alias-fill-l1, var(--dsw-alias-bg-layer-2, rgba(120, 120, 128, 0.15)));
        --dshg-border: var(--dsw-alias-border-l1, rgba(128, 128, 136, 0.17));
        --dshg-border-strong: var(--dsw-alias-border-l2, rgba(128, 128, 136, 0.28));
        --dshg-text: var(--dsw-alias-label-primary, #e4e4e7);
        --dshg-text-2: var(--dsw-alias-label-secondary, rgba(142, 142, 147, 0.92));
        --dshg-text-3: var(--dsw-alias-label-tertiary, rgba(142, 142, 147, 0.65));
        --dshg-accent: var(--dsw-alias-brand-primary, #2563eb);
        color: var(--dshg-text);
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
        display: flex;
        flex-direction: column;
        gap: 10px;
        padding: 12px;
        height: 100%;
        box-sizing: border-box;
        overflow-y: auto;
      }
      .dshg-root[data-variant="main"] {
        max-width: 780px;
        margin: 0 auto;
        padding: 20px 18px;
      }
      .dshg-card {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        border-radius: 12px;
        padding: 11px 12px;
        display: flex;
        flex-direction: column;
        gap: 8px;
      }
      .dshg-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        flex-wrap: wrap;
      }
      .dshg-brand {
        display: flex;
        align-items: center;
        gap: 9px;
        min-width: 0;
      }
      .dshg-logo-wrap {
        width: 32px;
        height: 32px;
        border-radius: 9px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: linear-gradient(135deg, rgba(37, 99, 235, 0.18), rgba(14, 165, 233, 0.14));
        border: 1px solid rgba(59, 130, 246, 0.34);
        color: #3b82f6;
        flex-shrink: 0;
      }
      .dshg-title {
        font-size: 13.5px;
        font-weight: 650;
        line-height: 1.2;
        letter-spacing: -0.01em;
      }
      .dshg-sub {
        font-size: 11px;
        color: var(--dshg-text-2);
        margin-top: 2px;
      }
      .dshg-actions {
        display: flex;
        align-items: center;
        gap: 6px;
        flex-shrink: 0;
      }
      .dshg-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 5px;
        height: 26px;
        padding: 0 9px;
        border-radius: 8px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        color: var(--dshg-text);
        font-size: 11.5px;
        font-weight: 500;
        cursor: pointer;
        transition: background 0.15s ease, border-color 0.15s ease;
        text-decoration: none;
        box-sizing: border-box;
      }
      .dshg-btn:hover:not(:disabled) {
        background: var(--dshg-card-hover);
        border-color: var(--dshg-border-strong);
      }
      .dshg-btn:disabled {
        opacity: 0.6;
        cursor: default;
      }
      .dshg-btn-primary {
        background: var(--dshg-accent);
        border-color: transparent;
        color: #ffffff;
        height: 31px;
        padding: 0 12px;
        font-weight: 600;
        width: 100%;
      }
      .dshg-btn-primary:hover:not(:disabled) {
        background: var(--dshg-accent);
        filter: brightness(1.08);
      }
      .dshg-badge {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 3px 8px;
        border-radius: 99px;
        font-size: 11px;
        font-weight: 600;
        border: 1px solid transparent;
        width: fit-content;
      }
      .dshg-badge[data-tone="ok"] {
        background: rgba(34, 197, 94, 0.14);
        border-color: rgba(34, 197, 94, 0.36);
        color: #22c55e;
      }
      .dshg-badge[data-tone="warn"] {
        background: rgba(245, 158, 11, 0.14);
        border-color: rgba(245, 158, 11, 0.38);
        color: #f59e0b;
      }
      .dshg-badge[data-tone="blue"] {
        background: rgba(59, 130, 246, 0.14);
        border-color: rgba(59, 130, 246, 0.36);
        color: #3b82f6;
      }
      .dshg-badge[data-tone="muted"] {
        background: var(--dshg-fill);
        border-color: var(--dshg-border);
        color: var(--dshg-text-2);
      }
      .dshg-sec-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        font-size: 11.5px;
        font-weight: 600;
        color: var(--dshg-text-2);
      }
      .dshg-profile-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
        gap: 8px;
      }
      .dshg-subcard {
        border: 1px solid var(--dshg-border);
        border-radius: 9px;
        padding: 9px 10px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        background: rgba(128, 128, 136, 0.04);
      }
      .dshg-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 6px;
        font-size: 11.5px;
      }
      .dshg-pills {
        display: flex;
        flex-wrap: wrap;
        gap: 5px;
      }
      .dshg-pill {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 10.5px;
        padding: 2px 7px;
        border-radius: 6px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill);
        color: var(--dshg-text);
        word-break: break-all;
      }
      .dshg-pill[data-tone="ok"] {
        background: rgba(34, 197, 94, 0.12);
        border-color: rgba(34, 197, 94, 0.32);
        color: #22c55e;
      }
      .dshg-pill[data-tone="warn"] {
        background: rgba(245, 158, 11, 0.14);
        border-color: rgba(245, 158, 11, 0.35);
        color: #f59e0b;
      }
      .dshg-snap-list {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .dshg-snap-item {
        border: 1px solid var(--dshg-border);
        border-radius: 8px;
        padding: 7px 9px;
        display: flex;
        flex-direction: column;
        gap: 3px;
        background: rgba(128, 128, 136, 0.04);
      }
      .dshg-mono {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11px;
        word-break: break-all;
      }
      .dshg-notice {
        padding: 8px 10px;
        border-radius: 9px;
        font-size: 11.5px;
        border: 1px solid rgba(59, 130, 246, 0.35);
        background: rgba(59, 130, 246, 0.12);
        color: var(--dshg-text);
      }
      .dshg-error {
        padding: 8px 10px;
        border-radius: 9px;
        font-size: 11.5px;
        border: 1px solid rgba(239, 68, 68, 0.4);
        background: rgba(239, 68, 68, 0.12);
        color: #f87171;
      }
    `

    function ensureStyles() {
      if (typeof document === 'undefined' || !document.head) return
      if (document.getElementById(STYLE_ID)) return
      const el = document.createElement('style')
      el.id = STYLE_ID
      el.textContent = CSS
      document.head.appendChild(el)
    }

    const store = {
      lang: 'zh',
      loading: false,
      launching: false,
      error: '',
      notice: '',
      data: null,
      checkedAt: 0,
    }

    const listeners = new Set()

    function notify() {
      for (const listener of listeners) {
        try {
          listener()
        } catch {
          // Ignore UI subscriber errors safely.
        }
      }
    }

    function patch(next) {
      Object.assign(store, next)
      notify()
    }

    function useStore() {
      const [, bump] = React.useState(0)
      React.useEffect(() => {
        const fn = () => bump((n) => n + 1)
        listeners.add(fn)
        return () => listeners.delete(fn)
      }, [])
      return store
    }

    async function fetchOverview() {
      if (store.loading || typeof fetch !== 'function') return
      patch({ loading: true, error: '' })
      try {
        const res = await fetch(`${API_BASE}/overview`, {
          cache: 'no-store',
          credentials: 'same-origin',
          headers: { accept: 'application/json' },
        })
        const json = await res.json()
        if (!res.ok || !json || json.ok === false) {
          throw new Error((json && json.error) || `HTTP ${res.status}`)
        }
        patch({
          data: json,
          checkedAt: json.checkedAt || Date.now(),
          error: '',
        })
      } catch (err) {
        patch({ error: err && err.message ? String(err.message) : 'Failed to inspect DSHGuard status' })
      } finally {
        patch({ loading: false })
      }
    }

    async function launchDesktopGuard() {
      if (store.launching || typeof fetch !== 'function') return
      patch({ launching: true, notice: '', error: '' })
      try {
        const res = await fetch(`${API_BASE}/open`, {
          method: 'POST',
          cache: 'no-store',
          credentials: 'same-origin',
          headers: { accept: 'application/json' },
        })
        const json = await res.json()
        if (!res.ok || !json || json.ok === false) {
          throw new Error((json && json.error) || `HTTP ${res.status}`)
        }
        patch({ notice: json.message || 'DSHGuard action completed' })
      } catch (err) {
        patch({ error: err && err.message ? String(err.message) : 'Failed to launch DSHGuard' })
      } finally {
        patch({ launching: false })
      }
    }

    function formatShortTime(value) {
      if (!value) return ''
      const d = new Date(value)
      if (Number.isNaN(d.getTime())) return String(value)
      const p = (n) => String(n).padStart(2, '0')
      return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
    }

    /** Crisp Whale-Shield glyph rendered in currentColor for tabs & rail icons. */
    function GuardIcon(props) {
      const size = props && typeof props.size === 'number' && props.size > 0 ? props.size : 17
      const active = Boolean(props && props.active)
      return h(
        'svg',
        {
          width: size,
          height: size,
          viewBox: '0 0 24 24',
          fill: 'none',
          stroke: 'currentColor',
          strokeWidth: active ? 2.1 : 1.75,
          strokeLinecap: 'round',
          strokeLinejoin: 'round',
          'aria-hidden': true,
          focusable: false,
          style: { display: 'block' },
        },
        h('path', { d: 'M12 2.8 4.8 5.8v5.7c0 4.6 3.1 8.3 7.2 9.7 4.1-1.4 7.2-5.1 7.2-9.7V5.8L12 2.8Z' }),
        h('path', { d: 'M8.5 13c.8-2 2.5-3 4.3-2.6 1.5.3 2.5 1.3 3.2 2.2.8-.6 1.4-1.3 1.7-2.2' }),
        h('circle', { cx: '11', cy: '12.6', r: '0.65', fill: 'currentColor' }),
      )
    }

    class GuardErrorBoundary extends React.Component {
      constructor(props) {
        super(props)
        this.state = { error: null }
      }
      static getDerivedStateFromError(error) {
        return { error }
      }
      render() {
        if (this.state.error) {
          const t = COPY[store.lang] || COPY.zh
          return h(
            'div',
            { className: 'dshg-root' },
            h(
              'div',
              { className: 'dshg-card' },
              h('div', { style: { fontWeight: 600, fontSize: 13 } }, t.errorRecover),
              h(
                'div',
                { style: { fontSize: 11.5, color: 'var(--dshg-text-2)' } },
                String((this.state.error && this.state.error.message) || this.state.error),
              ),
              h(
                'button',
                {
                  className: 'dshg-btn',
                  onClick: () => {
                    this.setState({ error: null })
                    void fetchOverview()
                  },
                },
                t.retry,
              ),
            ),
          )
        }
        return this.props.children
      }
    }

    function ProfileSubcard({ profile, t }) {
      const isWeb = profile.target === 'web'
      const trackName = isWeb ? t.webTrack : t.desktopTrack
      const trackTone = isWeb ? 'blue' : 'ok'

      if (!profile.exists) {
        return h(
          'div',
          { className: 'dshg-subcard' },
          h(
            'div',
            { className: 'dshg-row' },
            h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, trackName),
          ),
          h('div', { style: { fontSize: 11, color: 'var(--dshg-text-2)' } }, t.missingDir),
        )
      }

      const patch = profile.patch || {}
      const patchText = !patch.exists
        ? t.patchMissing
        : patch.syntaxOk
          ? t.patchOk(patch.disabledCount || 0)
          : t.patchErr(patch.error || 'YAML')
      const patchTone = patch.exists && !patch.syntaxOk ? 'warn' : 'ok'

      return h(
        'div',
        { className: 'dshg-subcard' },
        h(
          'div',
          { className: 'dshg-row' },
          h('span', { className: 'dshg-badge', 'data-tone': trackTone }, trackName),
          h(
            'span',
            { style: { fontSize: 11, color: 'var(--dshg-text-2)' } },
            `${t.bundlesLabel} ${profile.bundlesCount} / ${t.depsLabel} ${profile.dependenciesCount}`,
          ),
        ),
        h('div', { className: 'dshg-badge', 'data-tone': patchTone }, patchText),
        !profile.packageJsonOk
          ? h('div', { style: { fontSize: 11, color: '#f59e0b' } }, t.pkgErr)
          : null,
        Array.isArray(profile.missingInDeps) && profile.missingInDeps.length > 0
          ? h(
              'div',
              { className: 'dshg-pills' },
              h('span', { style: { fontSize: 10.5, color: '#f59e0b' } }, `${t.missingInDeps}:`),
              ...profile.missingInDeps.map((pkg) => h('span', { key: pkg, className: 'dshg-pill', 'data-tone': 'warn' }, pkg)),
            )
          : null,
        Array.isArray(profile.unregisteredDeps) && profile.unregisteredDeps.length > 0
          ? h(
              'div',
              { className: 'dshg-pills' },
              h('span', { style: { fontSize: 10.5, color: '#f59e0b' } }, `${t.unregisteredDeps}:`),
              ...profile.unregisteredDeps.map((pkg) => h('span', { key: pkg, className: 'dshg-pill', 'data-tone': 'warn' }, pkg)),
            )
          : null,
      )
    }

    function GuardPanelInner({ variant }) {
      const snap = useStore()
      const t = COPY[snap.lang] || COPY.zh

      React.useEffect(() => {
        ensureStyles()
        if (!store.data && !store.loading) {
          void fetchOverview()
        }
      }, [])

      const data = snap.data
      const health = data && data.health
      const snapshots = data && data.snapshots
      const platform = (data && data.platform) || (health && health.platform) || ''
      const guard = (health && health.guard) || {}
      const releaseUrl = guard.releaseUrl || (snapshots && snapshots.releaseUrl) || RELEASE_FALLBACK
      const creds = (health && health.credentials) || {}
      const refKeys = Array.isArray(creds.referencedKeys) ? creds.referencedKeys : []
      const snapList = snapshots && Array.isArray(snapshots.snapshots) ? snapshots.snapshots : []

      return h(
        'div',
        { className: 'dshg-root', 'data-variant': variant || 'dock' },
        // Top header card
        h(
          'div',
          { className: 'dshg-card' },
          h(
            'div',
            { className: 'dshg-header' },
            h(
              'div',
              { className: 'dshg-brand' },
              h('div', { className: 'dshg-logo-wrap' }, h(GuardIcon, { size: 18, active: true })),
              h(
                'div',
                null,
                h('div', { className: 'dshg-title' }, t.brand),
                h('div', { className: 'dshg-sub' }, t.subtitle),
              ),
            ),
            h(
              'div',
              { className: 'dshg-actions' },
              h(
                'button',
                {
                  className: 'dshg-btn',
                  disabled: snap.loading,
                  onClick: () => void fetchOverview(),
                },
                snap.loading ? t.refreshing : `↻ ${t.refresh}`,
              ),
              h(
                'button',
                {
                  className: 'dshg-btn',
                  onClick: () => patch({ lang: snap.lang === 'zh' ? 'en' : 'zh' }),
                },
                t.langSwitch,
              ),
            ),
          ),
          health
            ? h(
                'div',
                { className: 'dshg-row' },
                h(
                  'span',
                  { className: 'dshg-badge', 'data-tone': health.healthy ? 'ok' : 'warn' },
                  health.healthy ? `✓ ${t.healthy}` : `⚠ ${t.unhealthy(health.issuesCount || 0)}`,
                ),
                snap.checkedAt
                  ? h('span', { style: { fontSize: 10.5, color: 'var(--dshg-text-3)' } }, t.checkedAt(formatShortTime(snap.checkedAt)))
                  : null,
              )
            : null,
        ),

        snap.error ? h('div', { className: 'dshg-error' }, snap.error) : null,
        snap.notice ? h('div', { className: 'dshg-notice' }, snap.notice) : null,

        !data && snap.loading
          ? h('div', { className: 'dshg-card', style: { fontSize: 12, color: 'var(--dshg-text-2)' } }, t.loading)
          : null,

        // Profiles comparison
        health && Array.isArray(health.profiles)
          ? h(
              'div',
              { className: 'dshg-card' },
              h('div', { className: 'dshg-sec-head' }, h('span', null, t.profilesTitle)),
              h(
                'div',
                { className: 'dshg-profile-grid' },
                ...health.profiles.map((p) => h(ProfileSubcard, { key: p.target, profile: p, t })),
              ),
            )
          : null,

        // Issues breakdown if unhealthy
        health && Array.isArray(health.issues) && health.issues.length > 0
          ? h(
              'div',
              { className: 'dshg-card' },
              h('div', { className: 'dshg-sec-head' }, h('span', null, t.issuesTitle)),
              ...health.issues.map((issue, idx) =>
                h('div', { key: idx, style: { fontSize: 11.5, color: '#f59e0b', wordBreak: 'break-word' } }, `• ${issue}`),
              ),
            )
          : null,

        // Masked credentials readiness
        health
          ? h(
              'div',
              { className: 'dshg-card' },
              h(
                'div',
                { className: 'dshg-sec-head' },
                h('span', null, t.credsTitle),
                h(
                  'span',
                  { className: 'dshg-badge', 'data-tone': creds.fileExists ? 'ok' : 'muted' },
                  creds.fileExists
                    ? t.credReadyBadge(creds.nonEmptyKeysCount || 0, creds.totalKeysCount || 0)
                    : t.credMissingFile,
                ),
              ),
              refKeys.length > 0
                ? h(
                    'div',
                    { className: 'dshg-pills' },
                    ...refKeys.map((item) =>
                      h(
                        'span',
                        { key: item.key, className: 'dshg-pill', 'data-tone': item.ready ? 'ok' : 'warn' },
                        `${item.ready ? '✓' : '✗'} ${item.key}`,
                      ),
                    ),
                  )
                : h('div', { style: { fontSize: 11, color: 'var(--dshg-text-2)' } }, t.credNoRefs),
            )
          : null,

        // Pre-change snapshots
        snapshots
          ? h(
              'div',
              { className: 'dshg-card' },
              h(
                'div',
                { className: 'dshg-sec-head' },
                h('span', null, t.snapshotsTitle),
                h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.snapshotsTotal(snapshots.total || 0)),
              ),
              snapList.length > 0
                ? h(
                    'div',
                    { className: 'dshg-snap-list' },
                    ...snapList.slice(0, 8).map((s) =>
                      h(
                        'div',
                        { key: s.id, className: 'dshg-snap-item' },
                        h(
                          'div',
                          { className: 'dshg-row' },
                          h(
                            'span',
                            { className: 'dshg-badge', 'data-tone': s.target === 'desktop' ? 'ok' : 'blue' },
                            s.target === 'desktop' ? t.desktopTrack : t.webTrack,
                          ),
                          h('span', { style: { fontSize: 10.5, color: 'var(--dshg-text-3)' } }, `${t.snapItems(s.itemCount || 0)} · ${formatShortTime(s.createdAt)}`),
                        ),
                        h('div', { className: 'dshg-mono' }, s.id),
                        h('div', { style: { fontSize: 11, color: 'var(--dshg-text-2)' } }, s.reason || t.snapNoReason),
                      ),
                    ),
                  )
                : h('div', { style: { fontSize: 11, color: 'var(--dshg-text-2)' } }, t.snapshotsEmpty),
            )
          : null,

        // Cross-platform DSHGuard supervisor bridge
        data
          ? h(
              'div',
              { className: 'dshg-card' },
              h('div', { className: 'dshg-sec-head' }, h('span', null, t.guardTitle)),
              platform === 'win32' && guard.installed
                ? [
                    h('div', { key: 'path', className: 'dshg-mono', style: { color: 'var(--dshg-text-2)' } }, t.guardReady(guard.exePath || 'DSHGuard.exe')),
                    h(
                      'button',
                      {
                        key: 'launch',
                        className: 'dshg-btn dshg-btn-primary',
                        disabled: snap.launching,
                        onClick: () => void launchDesktopGuard(),
                      },
                      snap.launching ? t.openingGuard : `🐋 ${t.openGuard}`,
                    ),
                  ]
                : [
                    h(
                      'div',
                      { key: 'note', style: { fontSize: 11.5, color: 'var(--dshg-text-2)', lineHeight: 1.5 } },
                      platform && platform !== 'win32' ? t.guardNonWin(platform) : t.guardNotInstalled,
                    ),
                    h(
                      'a',
                      {
                        key: 'dl',
                        className: 'dshg-btn',
                        href: releaseUrl,
                        target: '_blank',
                        rel: 'noopener noreferrer',
                      },
                      `↗ ${t.downloadRelease}`,
                    ),
                  ],
            )
          : null,
      )
    }

    function GuardPanel(props) {
      return h(GuardErrorBoundary, null, h(GuardPanelInner, props || {}))
    }

    function SidebarGuardTab() {
      return h(GuardPanel, { variant: 'dock' })
    }

    function MainGuardPage() {
      return h(GuardPanel, { variant: 'main' })
    }

    return {
      inject: ['slots', 'sidebarRightTabs'],
      apply(ctx) {
        if (ctx && typeof ctx.effect === 'function') {
          ctx.effect(() => {
            const tabs = typeof ctx.get === 'function' ? ctx.get('sidebarRightTabs') : ctx.sidebarRightTabs
            if (!tabs || typeof tabs.register !== 'function') return undefined
            return tabs.register({
              id: SIDE_ID,
              kind: SIDE_ID,
              priority: 'extension',
              title: () => (COPY[store.lang] || COPY.zh).brand,
              guide: [
                {
                  id: SIDE_ID,
                  order: 60,
                  title: () => (COPY[store.lang] || COPY.zh).brand,
                  description: () => (COPY[store.lang] || COPY.zh).guideDesc,
                  icon: GuardIcon,
                },
              ],
            })
          }, 'dsh-guard: right-sidebar tab')

          if (ctx.slots && typeof ctx.slots.inject === 'function' && typeof ctx.slots.register === 'function') {
            ctx.slots.inject('sidebar.right.pane.tab', () =>
              ctx.slots.register({ name: 'sidebar.right.pane.tab', key: SIDE_ID, order: 120 }, SidebarGuardTab),
            )
            ctx.slots.inject('sidebar.panellist', () =>
              ctx.slots.register(
                { name: 'sidebar.panellist', id: SIDE_ID, order: 55, label: () => (COPY[store.lang] || COPY.zh).brand },
                GuardIcon,
              ),
            )
            ctx.slots.inject('main', () =>
              ctx.slots.register({ name: 'main', key: SIDE_ID, locale: 'zh-CN' }, MainGuardPage),
            )
          }
        }
      },
    }
  },
})
