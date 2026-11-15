/**
 * Client half of @gluztm/dsh-guard — Cross-platform read-only health, masked key readiness,
 * snapshot timeline, and Windows DSHGuard Desktop Console bridge for DeepSeek Harness.
 *
 * Strictly zero-secret-leak: never reads, slices, stores, or transmits API keys or tokens.
 * Uses only React.createElement, useState, useEffect, and Component so lightweight ModuleLoader hosts stay compatible.
 */
window.__ModuleLoader__.load({
  id: '@gluztm/dsh-guard',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const SIDE_ID = 'dsh-guard'
    const API_BASE = 'api/dsh-guard'
    const STYLE_ID = 'dsh-guard-panel-styles-v241'
    const VERSION = '2.4.1'
    const REPO_FALLBACK = 'https://github.com/gluztm/DSHGuard'
    const RELEASE_FALLBACK = 'https://github.com/gluztm/DSHGuard/releases/latest'
    const INSTALLER_FALLBACK = 'https://github.com/gluztm/DSHGuard/releases/download/2.4.1/DSHGuard-Setup-2.4.1.exe'

    const COPY = {
      zh: {
        brand: 'DSH 守护壳',
        subtitle: '双轨体检 · 脱敏对账 · 快照回望',
        versionPill: `v${VERSION}`,
        refresh: '重新体检',
        refreshing: '体检中',
        langSwitch: 'EN',
        healthy: '双轨配置健康',
        unhealthy: (n) => `发现 ${n} 项异常`,
        loading: '正在读取双轨配置、凭据键与快照状态…',
        retry: '重试',
        checkedAt: (t) => `最近体检 ${t}`,
        tabOverview: '总览',
        tabCreds: '凭据',
        tabSnapshots: '快照',
        kpiProfilesLabel: '双轨插件',
        kpiProfilesSub: 'Web / 桌面启用',
        kpiCredsLabel: '凭据就绪',
        kpiCredsSub: '非空环境键',
        kpiSnapsLabel: '变更快照',
        kpiSnapsSub: '桌面守护存档',
        supervisorTitleWinMissing: '安装 DSHGuard 桌面完整版',
        supervisorBadgeWin: 'Windows · 免管理员',
        supervisorDescWinMissing: '伴生插件仅负责 DSH 内只读体检；安装 Windows 桌面控制台可解锁以下日常高频能力：',
        feat1Title: '四协议模型配置与测速',
        feat1Sub: '自动补全 /v1 与 /v1beta',
        feat2Title: 'CC Switch 多源导密钥',
        feat2Sub: '文件拖入与跨端只读导入',
        feat3Title: '插件批量查新防串轨',
        feat3Sub: '万款市场插件安全升级',
        feat4Title: '进程外救援与快照回滚',
        feat4Sub: '启动崩溃时桌面一键还原',
        btnDownloadExe: '直接下载 Windows 安装包 (.exe)',
        btnViewRepo: '主页与动图预览',
        btnRedetect: '已安装？重新检测',
        supervisorTitleWinReady: 'DSHGuard 桌面控制台',
        supervisorBadgeReady: '桌面控制台已就绪',
        supervisorDescWinReady: '已对接本机桌面守护壳，支持四协议模型配置与测速、CC Switch 导入及快照回滚。',
        btnOpenGuard: '打开 DSHGuard 桌面控制台',
        btnOpeningGuard: '正在唤起…',
        supervisorTitleOther: '跨平台只读体检模式',
        supervisorDescOther: (plat) => `当前运行环境为 ${plat}，伴生插件双轨体检、凭据脱敏对账与 /guard 命令已全部就绪；Windows 桌面完整版独立提供图形化模型配置、CC Switch 导入与进程外崩溃救援。`,
        btnViewReleases: '查看 Windows 版 Releases',
        profilesTitle: 'Web / 桌面双轨对账',
        webTrack: 'Web 引擎',
        desktopTrack: '桌面版',
        missingDir: '未检测到该端配置目录',
        bundlesLabel: '插件',
        depsLabel: '依赖',
        patchOk: (n) => (n > 0 ? `Patch 正常 · 禁 ${n} 项` : 'Patch 正常'),
        patchMissing: '未建 cordis.patch.yml',
        patchErr: (e) => `Patch 异常：${e}`,
        pkgErr: 'package.json 解析异常',
        missingInDeps: '清单缺依赖',
        unregisteredDeps: '未登记插件',
        issuesTitle: '诊断建议',
        issueActionOpen: '在桌面控制台一键排查修复',
        issueActionDownload: '下载桌面控制台辅助排查 (.exe)',
        credsTitle: '凭据脱敏就绪度',
        credsPrivacyHint: '严格零秘密原则：仅核对环境键是否非空，绝不读取、截断或传输真实密钥。',
        credReadyBadge: (ready, total) => `已就绪 ${ready} / ${total}`,
        credMissingFile: '未创建 .credentials.yaml',
        credProgressPatch: (ready, total) => `cordis.patch.yml 引用键就绪 ${ready} / ${total}`,
        credProgressFile: (nonEmpty, total) => `.credentials.yaml 非空键 ${nonEmpty} / ${total}`,
        credStatusReady: '已就绪',
        credStatusMissing: '未配置',
        credNoRefs: '当前双端 cordis.patch.yml 暂未引用任何 apiKeyEnv 环境变量。',
        credUpsellTitle: '需要图形化管理模型与密钥？',
        credUpsellDesc: '在 DSHGuard 桌面版「凭据中心」可直接点击卡片编辑 OpenAI / Anthropic / Gemini 接口、测试单模型连通性，或从 CC Switch 与本地 Agent 一键导入。',
        credUpsellOpen: '打开桌面端「凭据中心」',
        credUpsellDownload: '下载桌面完整版开启「凭据中心」(.exe)',
        snapshotsTitle: '变更前快照回望',
        snapshotsTotal: (t) => `共 ${t} 份`,
        snapshotsEmptyTitle: '暂无变更前快照存档',
        snapshotsEmptyDesc: '使用 DSHGuard 桌面控制台执行插件安装、升级或切换配置前，系统都会自动存档以供随时回滚。',
        snapItems: (n) => `${n} 项文件`,
        snapNoReason: '操作前自动快照',
        snapActionOpen: '在桌面端管理 / 回滚快照',
        snapActionDownload: '下载 Windows 桌面版开启自动快照 (.exe)',
        footerNote: `DSHGuard v${VERSION} · 严格零秘密只读伴生模式`,
        footerHome: 'GitHub 主页',
        errorRecover: '守护壳面板已触发渲染保护恢复',
        guideDesc: '双端配置体检 · 凭据键就绪状态 · 快照历史 · 联动 DSHGuard 桌面端',
      },
      en: {
        brand: 'DSHGuard',
        subtitle: 'Dual-Track Health · Masked Keys · Snapshots',
        versionPill: `v${VERSION}`,
        refresh: 'Refresh',
        refreshing: 'Checking',
        langSwitch: '中',
        healthy: 'Dual-Track Healthy',
        unhealthy: (n) => `${n} Issue${n === 1 ? '' : 's'} Found`,
        loading: 'Inspecting DSH profiles, credential readiness, and snapshots…',
        retry: 'Retry',
        checkedAt: (t) => `Checked ${t}`,
        tabOverview: 'Overview',
        tabCreds: 'Credentials',
        tabSnapshots: 'Snapshots',
        kpiProfilesLabel: 'Bundles',
        kpiProfilesSub: 'Web / Desktop',
        kpiCredsLabel: 'Key Slots',
        kpiCredsSub: 'Non-empty env',
        kpiSnapsLabel: 'Snapshots',
        kpiSnapsSub: 'Guard backups',
        supervisorTitleWinMissing: 'Install DSHGuard Desktop Console',
        supervisorBadgeWin: 'Windows · Non-Admin',
        supervisorDescWinMissing: 'This companion plugin stays strictly read-only. Install the Windows desktop app to unlock:',
        feat1Title: '4-Protocol Model Setup & Ping',
        feat1Sub: 'Auto /v1 & /v1beta endpoints',
        feat2Title: 'CC Switch & Multi-Source Import',
        feat2Sub: 'Drop configs or read local agents',
        feat3Title: 'Safe Batch Plugin Updates',
        feat3Sub: '10,700+ catalog, zero cross-track',
        feat4Title: 'Out-of-Band Recovery & Rollback',
        feat4Sub: 'Restore broken startups externally',
        btnDownloadExe: 'Direct Download Windows Installer (.exe)',
        btnViewRepo: 'Homepage & Visual Tour',
        btnRedetect: 'Installed? Re-detect',
        supervisorTitleWinReady: 'DSHGuard Desktop Console',
        supervisorBadgeReady: 'Desktop Console Ready',
        supervisorDescWinReady: 'Connected to your local DSHGuard executable for visual model configuration, CC Switch import, and snapshot rollback.',
        btnOpenGuard: 'Open DSHGuard Desktop Console',
        btnOpeningGuard: 'Launching…',
        supervisorTitleOther: 'Cross-Platform Read-Only Mode',
        supervisorDescOther: (plat) => `Running on ${plat} with full dual-track auditing, masked key checks, and /guard commands. Full out-of-band GUI tools run on Windows.`,
        btnViewReleases: 'View Windows Releases',
        profilesTitle: 'Web / Desktop Profile Alignment',
        webTrack: 'Web Engine',
        desktopTrack: 'Desktop',
        missingDir: 'Profile directory not found',
        bundlesLabel: 'Enabled',
        depsLabel: 'Deps',
        patchOk: (n) => (n > 0 ? `Patch OK · ${n} disabled` : 'Patch OK'),
        patchMissing: 'No cordis.patch.yml',
        patchErr: (e) => `Patch error: ${e}`,
        pkgErr: 'Invalid package.json',
        missingInDeps: 'Missing in deps',
        unregisteredDeps: 'Unregistered deps',
        issuesTitle: 'Diagnostics',
        issueActionOpen: 'Troubleshoot in DSHGuard Desktop',
        issueActionDownload: 'Download Windows Supervisor (.exe)',
        credsTitle: 'Masked Credential Readiness',
        credsPrivacyHint: 'Zero-secret guarantee: checks only whether key names are non-empty, never reading or transmitting secret values.',
        credReadyBadge: (ready, total) => `${ready} / ${total} Ready`,
        credMissingFile: '.credentials.yaml not found',
        credProgressPatch: (ready, total) => `Referenced apiKeyEnv keys ready: ${ready} / ${total}`,
        credProgressFile: (nonEmpty, total) => `Non-empty entries in .credentials.yaml: ${nonEmpty} / ${total}`,
        credStatusReady: 'Ready',
        credStatusMissing: 'Missing',
        credNoRefs: 'No apiKeyEnv references detected in either cordis.patch.yml.',
        credUpsellTitle: 'Configure models & keys visually',
        credUpsellDesc: 'Use the desktop Credential Hub to edit OpenAI / Anthropic / Gemini endpoints, test model connectivity, or import non-subscription keys from CC Switch.',
        credUpsellOpen: 'Open Credential Hub in Desktop',
        credUpsellDownload: 'Download Desktop Console for Credential Hub (.exe)',
        snapshotsTitle: 'Pre-Change Snapshot Timeline',
        snapshotsTotal: (t) => `${t} total`,
        snapshotsEmptyTitle: 'No pre-change snapshots yet',
        snapshotsEmptyDesc: 'DSHGuard automatically saves config and plugin snapshots before any risky change on Windows so you can roll back cleanly.',
        snapItems: (n) => `${n} files`,
        snapNoReason: 'Automatic checkpoint',
        snapActionOpen: 'Manage / Roll back in Desktop',
        snapActionDownload: 'Download Windows App for Auto-Snapshots (.exe)',
        footerNote: `DSHGuard v${VERSION} · Strictly Read-Only Companion`,
        footerHome: 'GitHub',
        errorRecover: 'Panel recovered by ErrorBoundary',
        guideDesc: 'Dual-track health · Masked key readiness · Snapshots · DSHGuard Desktop bridge',
      },
    }

    const CSS = `
      @keyframes dshg-spin {
        from { transform: rotate(0deg); }
        to { transform: rotate(360deg); }
      }
      @keyframes dshg-pulse {
        0%, 100% { opacity: 1; }
        50% { opacity: 0.45; }
      }
      .dshg-root {
        --dshg-card: var(--dsw-alias-card-fill, var(--dsw-alias-bg-layer-1, rgba(128, 128, 140, 0.08)));
        --dshg-card-hover: var(--dsw-alias-card-fill-hover, var(--dsw-alias-bg-layer-2, rgba(128, 128, 140, 0.14)));
        --dshg-fill: var(--dsw-alias-fill-l1, rgba(128, 128, 140, 0.11));
        --dshg-fill-subtle: var(--dsw-alias-bg-layer-2, rgba(128, 128, 140, 0.06));
        --dshg-border: var(--dsw-alias-border-l1, rgba(128, 128, 140, 0.18));
        --dshg-border-strong: var(--dsw-alias-border-l2, rgba(128, 128, 140, 0.30));
        --dshg-text: var(--dsw-alias-label-primary, inherit);
        --dshg-text-2: var(--dsw-alias-label-secondary, rgba(128, 128, 140, 0.88));
        --dshg-text-3: var(--dsw-alias-label-tertiary, rgba(128, 128, 140, 0.64));
        --dshg-accent: var(--dsw-alias-brand-primary, #2563eb);
        --dshg-accent-soft: rgba(37, 99, 235, 0.10);
        --dshg-accent-border: rgba(59, 130, 246, 0.34);
        --dshg-ok: #16a34a;
        --dshg-ok-soft: rgba(34, 197, 94, 0.12);
        --dshg-ok-border: rgba(34, 197, 94, 0.32);
        --dshg-warn: #d97706;
        --dshg-warn-soft: rgba(245, 158, 11, 0.12);
        --dshg-warn-border: rgba(245, 158, 11, 0.34);
        background: transparent;
        color: var(--dshg-text);
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif;
        display: flex;
        flex-direction: column;
        gap: 10px;
        padding: 12px;
        height: 100%;
        box-sizing: border-box;
        overflow-y: auto;
        font-size: 12px;
        line-height: 1.45;
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
        gap: 9px;
      }
      .dshg-card-spotlight {
        border: 1px solid var(--dshg-accent-border);
        background: var(--dshg-accent-soft);
      }
      .dshg-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
      }
      .dshg-brand {
        display: flex;
        align-items: center;
        gap: 9px;
        min-width: 0;
      }
      .dshg-logo-wrap {
        width: 34px;
        height: 34px;
        border-radius: 9px;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: var(--dshg-accent-soft);
        border: 1px solid var(--dshg-accent-border);
        color: var(--dshg-accent);
        flex-shrink: 0;
      }
      .dshg-title-row {
        display: flex;
        align-items: center;
        gap: 6px;
        flex-wrap: wrap;
      }
      .dshg-title {
        font-size: 13.5px;
        font-weight: 600;
        line-height: 1.2;
        letter-spacing: -0.01em;
      }
      .dshg-ver {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11px;
        font-weight: 500;
        padding: 1px 6px;
        border-radius: 99px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill);
        color: var(--dshg-text-2);
      }
      .dshg-sub {
        font-size: 11px;
        color: var(--dshg-text-2);
        margin-top: 2px;
      }
      .dshg-actions {
        display: flex;
        align-items: center;
        gap: 5px;
        flex-shrink: 0;
      }
      .dshg-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 6px;
        height: 28px;
        padding: 0 10px;
        border-radius: 8px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        color: var(--dshg-text);
        font-size: 11.5px;
        font-weight: 500;
        cursor: pointer;
        transition: background 0.15s ease, border-color 0.15s ease, opacity 0.15s ease;
        text-decoration: none;
        box-sizing: border-box;
        white-space: nowrap;
      }
      .dshg-btn:hover:not(:disabled) {
        background: var(--dshg-card-hover);
        border-color: var(--dshg-border-strong);
      }
      .dshg-btn:disabled {
        opacity: 0.58;
        cursor: default;
      }
      .dshg-btn-icon {
        width: 28px;
        padding: 0;
      }
      .dshg-btn-primary {
        background: var(--dshg-accent);
        border-color: transparent;
        color: #ffffff;
        height: 34px;
        padding: 0 14px;
        font-size: 12.5px;
        font-weight: 600;
        width: 100%;
        border-radius: 9px;
      }
      .dshg-btn-primary:hover:not(:disabled) {
        background: var(--dshg-accent);
        border-color: transparent;
        opacity: 0.92;
      }
      .dshg-btn-row {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 6px;
      }
      .dshg-btn-row .dshg-btn {
        width: 100%;
        padding: 0 8px;
        font-size: 11px;
      }
      .dshg-spin {
        animation: dshg-spin 0.9s linear infinite;
      }
      .dshg-badge {
        display: inline-flex;
        align-items: center;
        gap: 5px;
        padding: 2px 8px;
        border-radius: 99px;
        font-size: 11px;
        font-weight: 500;
        border: 1px solid transparent;
        width: fit-content;
      }
      .dshg-dot {
        width: 6px;
        height: 6px;
        border-radius: 50%;
        background: currentColor;
        animation: dshg-pulse 2.2s ease-in-out infinite;
      }
      .dshg-badge[data-tone="ok"] {
        background: var(--dshg-ok-soft);
        border-color: var(--dshg-ok-border);
        color: #10b981;
      }
      .dshg-badge[data-tone="warn"] {
        background: var(--dshg-warn-soft);
        border-color: var(--dshg-warn-border);
        color: #d97706;
      }
      .dshg-badge[data-tone="blue"] {
        background: var(--dshg-accent-soft);
        border-color: var(--dshg-accent-border);
        color: #3b82f6;
      }
      .dshg-badge[data-tone="muted"] {
        background: var(--dshg-fill);
        border-color: var(--dshg-border);
        color: var(--dshg-text-2);
      }
      .dshg-kpi-strip {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 6px;
      }
      .dshg-kpi {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        border-radius: 10px;
        padding: 8px 9px;
        display: flex;
        flex-direction: column;
        gap: 2px;
        text-align: left;
        cursor: pointer;
        color: var(--dshg-text);
        transition: border-color 0.15s ease, background 0.15s ease;
      }
      .dshg-kpi:hover {
        background: var(--dshg-card-hover);
        border-color: var(--dshg-border-strong);
      }
      .dshg-kpi[data-active="true"] {
        border-color: var(--dshg-accent-border);
        background: var(--dshg-accent-soft);
      }
      .dshg-kpi-label {
        font-size: 11px;
        color: var(--dshg-text-2);
      }
      .dshg-kpi-val {
        font-size: 14px;
        font-weight: 600;
        font-variant-numeric: tabular-nums;
        line-height: 1.25;
      }
      .dshg-kpi-sub {
        font-size: 11px;
        color: var(--dshg-text-3);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .dshg-seg {
        display: grid;
        grid-template-columns: repeat(3, minmax(0, 1fr));
        gap: 4px;
        padding: 3px;
        border-radius: 10px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill-subtle);
      }
      .dshg-seg-btn {
        height: 27px;
        border: 1px solid transparent;
        border-radius: 7px;
        background: transparent;
        color: var(--dshg-text-2);
        font-size: 11.5px;
        font-weight: 500;
        cursor: pointer;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 5px;
        transition: background 0.15s ease, color 0.15s ease, border-color 0.15s ease;
      }
      .dshg-seg-btn:hover {
        color: var(--dshg-text);
      }
      .dshg-seg-btn[data-active="true"] {
        background: var(--dshg-card);
        border-color: var(--dshg-border);
        color: var(--dshg-text);
        font-weight: 600;
      }
      .dshg-seg-dot {
        width: 5px;
        height: 5px;
        border-radius: 50%;
        background: #f59e0b;
      }
      .dshg-sec-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        font-size: 12px;
        font-weight: 600;
        color: var(--dshg-text);
      }
      .dshg-sec-sub {
        font-size: 11px;
        color: var(--dshg-text-2);
        line-height: 1.45;
      }
      .dshg-feat-grid {
        display: grid;
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 6px;
      }
      .dshg-feat-item {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        border-radius: 9px;
        padding: 8px 9px;
        display: flex;
        align-items: flex-start;
        gap: 7px;
      }
      .dshg-feat-ico {
        color: var(--dshg-accent);
        flex-shrink: 0;
        margin-top: 1px;
      }
      .dshg-feat-t {
        font-size: 11.5px;
        font-weight: 600;
        line-height: 1.25;
      }
      .dshg-feat-s {
        font-size: 11px;
        color: var(--dshg-text-2);
        margin-top: 2px;
        line-height: 1.3;
      }
      .dshg-profile-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
        gap: 7px;
      }
      .dshg-subcard {
        border: 1px solid var(--dshg-border);
        border-radius: 9px;
        padding: 9px 10px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        background: var(--dshg-fill-subtle);
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
        font-size: 11px;
        padding: 2px 7px;
        border-radius: 6px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill);
        color: var(--dshg-text);
        word-break: break-all;
      }
      .dshg-pill[data-tone="warn"] {
        background: var(--dshg-warn-soft);
        border-color: var(--dshg-warn-border);
        color: #f59e0b;
      }
      .dshg-progress-wrap {
        display: flex;
        flex-direction: column;
        gap: 5px;
      }
      .dshg-progress-track {
        height: 5px;
        border-radius: 99px;
        background: var(--dshg-fill);
        overflow: hidden;
      }
      .dshg-progress-fill {
        height: 100%;
        border-radius: 99px;
        background: #22c55e;
        transition: width 0.25s ease;
      }
      .dshg-cred-list {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .dshg-cred-item {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill-subtle);
        border-radius: 9px;
        padding: 7px 10px;
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
      }
      .dshg-cred-key {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11.5px;
        font-weight: 500;
        min-width: 0;
        word-break: break-all;
      }
      .dshg-timeline {
        display: flex;
        flex-direction: column;
        gap: 7px;
      }
      .dshg-snap-item {
        border: 1px solid var(--dshg-border);
        border-radius: 9px;
        padding: 8px 10px;
        display: flex;
        flex-direction: column;
        gap: 4px;
        background: var(--dshg-fill-subtle);
      }
      .dshg-snap-head {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 6px;
      }
      .dshg-snap-title {
        display: inline-flex;
        align-items: center;
        gap: 6px;
        font-size: 12px;
        font-weight: 500;
        min-width: 0;
      }
      .dshg-track-dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        flex-shrink: 0;
      }
      .dshg-mono {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11px;
        word-break: break-all;
        color: var(--dshg-text-2);
      }
      .dshg-path-box {
        border: 1px solid var(--dshg-border);
        border-radius: 8px;
        background: var(--dshg-fill-subtle);
        padding: 6px 9px;
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11px;
        color: var(--dshg-text-2);
        word-break: break-all;
      }
      .dshg-empty {
        border: 1px dashed var(--dshg-border-strong);
        border-radius: 10px;
        padding: 16px 12px;
        display: flex;
        flex-direction: column;
        align-items: center;
        text-align: center;
        gap: 7px;
        color: var(--dshg-text-2);
      }
      .dshg-notice {
        padding: 8px 10px;
        border-radius: 9px;
        font-size: 11.5px;
        border: 1px solid var(--dshg-accent-border);
        background: var(--dshg-accent-soft);
        color: var(--dshg-text);
      }
      .dshg-error {
        padding: 8px 10px;
        border-radius: 9px;
        font-size: 11.5px;
        border: 1px solid rgba(239, 68, 68, 0.4);
        background: rgba(239, 68, 68, 0.11);
        color: #f87171;
      }
      .dshg-footer {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        padding: 2px 4px 4px;
        font-size: 11px;
        color: var(--dshg-text-3);
      }
      .dshg-footer a {
        color: var(--dshg-text-2);
        text-decoration: none;
      }
      .dshg-footer a:hover {
        color: var(--dshg-text);
        text-decoration: underline;
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
      tab: 'overview',
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
          // Ignore subscriber errors safely.
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
      return `${d.getMonth() + 1}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}`
    }

    function formatClockOnly(value) {
      if (!value) return ''
      const d = new Date(value)
      if (Number.isNaN(d.getTime())) return String(value)
      const p = (n) => String(n).padStart(2, '0')
      return `${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
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
          style: { display: 'block', flexShrink: 0 },
        },
        h('path', { d: 'M12 2.8 4.8 5.8v5.7c0 4.6 3.1 8.3 7.2 9.7 4.1-1.4 7.2-5.1 7.2-9.7V5.8L12 2.8Z' }),
        h('path', { d: 'M8.5 13c.8-2 2.5-3 4.3-2.6 1.5.3 2.5 1.3 3.2 2.2.8-.6 1.4-1.3 1.7-2.2' }),
        h('circle', { cx: '11', cy: '12.6', r: '0.65', fill: 'currentColor' }),
      )
    }

    /** Flat outline SVG icon set (zero emoji). */
    function SvgIcon({ name, size = 14, className = '' }) {
      const common = {
        width: size,
        height: size,
        viewBox: '0 0 24 24',
        fill: 'none',
        stroke: 'currentColor',
        strokeWidth: 1.85,
        strokeLinecap: 'round',
        strokeLinejoin: 'round',
        'aria-hidden': true,
        focusable: false,
        className: className || undefined,
        style: { display: 'block', flexShrink: 0 },
      }
      switch (name) {
        case 'refresh':
          return h(
            'svg',
            common,
            h('path', { d: 'M20 11a8.1 8.1 0 0 0-15.5-2m-.5-4v4h4' }),
            h('path', { d: 'M4 13a8.1 8.1 0 0 0 15.5 2m.5 4v-4h-4' }),
          )
        case 'download':
          return h(
            'svg',
            common,
            h('path', { d: 'M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2' }),
            h('path', { d: 'M7 11l5 5 5-5' }),
            h('path', { d: 'M12 4v12' }),
          )
        case 'external':
          return h(
            'svg',
            common,
            h('path', { d: 'M12 6H6a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2v-6' }),
            h('path', { d: 'M11 13l9-9' }),
            h('path', { d: 'M15 4h5v5' }),
          )
        case 'window':
          return h(
            'svg',
            common,
            h('rect', { x: 3, y: 4, width: 18, height: 16, rx: 3 }),
            h('path', { d: 'M3 9h18' }),
            h('path', { d: 'M10 14.5l2.5-2.5 2.5 2.5' }),
          )
        case 'bolt':
          return h('svg', common, h('path', { d: 'M13 3L4 14h7l-1 7 9-11h-7l1-7z' }))
        case 'key':
          return h(
            'svg',
            common,
            h('circle', { cx: 8.5, cy: 15.5, r: 4.5 }),
            h('path', { d: 'M11.7 12.3L19 5' }),
            h('path', { d: 'M16.5 7.5l2 2' }),
            h('path', { d: 'M14 10l2 2' }),
          )
        case 'layers':
          return h(
            'svg',
            common,
            h('path', { d: 'M12 3l9 4.5-9 4.5-9-4.5 9-4.5z' }),
            h('path', { d: 'M3 12l9 4.5 9-4.5' }),
            h('path', { d: 'M3 16.5l9 4.5 9-4.5' }),
          )
        case 'shield':
          return h(
            'svg',
            common,
            h('path', { d: 'M12 3l7.5 3v6c0 4.5-3.2 8.2-7.5 9.5C7.7 20.2 4.5 16.5 4.5 12V6L12 3z' }),
            h('path', { d: 'M9 12l2 2 4-4' }),
          )
        case 'clock':
          return h(
            'svg',
            common,
            h('circle', { cx: 12, cy: 12, r: 9 }),
            h('path', { d: 'M12 7v5l3 2' }),
          )
        case 'check':
          return h('svg', common, h('path', { d: 'M5 12l5 5 9-9' }))
        case 'alert':
          return h(
            'svg',
            common,
            h('path', { d: 'M12 9v4' }),
            h('path', { d: 'M10.4 4.3L3.1 17a1.8 1.8 0 0 0 1.6 2.7h14.6a1.8 1.8 0 0 0 1.6-2.7L13.6 4.3a1.8 1.8 0 0 0-3.2 0z' }),
            h('circle', { cx: 12, cy: 16.3, r: 0.6, fill: 'currentColor' }),
          )
        default:
          return null
      }
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

      const patchInfo = profile.patch || {}
      const patchText = !patchInfo.exists
        ? t.patchMissing
        : patchInfo.syntaxOk
          ? t.patchOk(patchInfo.disabledCount || 0)
          : t.patchErr(patchInfo.error || 'YAML')
      const patchTone = patchInfo.exists && !patchInfo.syntaxOk ? 'warn' : 'ok'

      return h(
        'div',
        { className: 'dshg-subcard' },
        h(
          'div',
          { className: 'dshg-row' },
          h('span', { className: 'dshg-badge', 'data-tone': trackTone }, trackName),
          h(
            'span',
            { style: { fontSize: 11, color: 'var(--dshg-text-2)', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' } },
            `${profile.bundlesCount} ${t.bundlesLabel} · ${profile.dependenciesCount} ${t.depsLabel}`,
          ),
        ),
        h(
          'div',
          { className: 'dshg-badge', 'data-tone': patchTone, style: { whiteSpace: 'nowrap' } },
          h(SvgIcon, { name: patchTone === 'ok' ? 'check' : 'alert', size: 11 }),
          patchText,
        ),
        !profile.packageJsonOk
          ? h('div', { style: { fontSize: 11, color: '#f59e0b' } }, t.pkgErr)
          : null,
        Array.isArray(profile.missingInDeps) && profile.missingInDeps.length > 0
          ? h(
              'div',
              { className: 'dshg-pills' },
              h('span', { style: { fontSize: 11, color: '#f59e0b' } }, `${t.missingInDeps}:`),
              ...profile.missingInDeps.map((pkg) => h('span', { key: pkg, className: 'dshg-pill', 'data-tone': 'warn' }, pkg)),
            )
          : null,
        Array.isArray(profile.unregisteredDeps) && profile.unregisteredDeps.length > 0
          ? h(
              'div',
              { className: 'dshg-pills' },
              h('span', { style: { fontSize: 11, color: '#f59e0b' } }, `${t.unregisteredDeps}:`),
              ...profile.unregisteredDeps.map((pkg) => h('span', { key: pkg, className: 'dshg-pill', 'data-tone': 'warn' }, pkg)),
            )
          : null,
      )
    }

    function SupervisorCard({ platform, guard, installerUrl, releaseUrl, repoUrl, launching, t }) {
      const isWin = platform === 'win32'
      if (isWin && guard.installed) {
        return h(
          'div',
          { className: 'dshg-card dshg-card-spotlight' },
          h(
            'div',
            { className: 'dshg-sec-head' },
            h(
              'span',
              { style: { display: 'inline-flex', alignItems: 'center', gap: 6 } },
              h(GuardIcon, { size: 15, active: true }),
              t.supervisorTitleWinReady,
            ),
            h(
              'span',
              { className: 'dshg-badge', 'data-tone': 'ok' },
              h('span', { className: 'dshg-dot' }),
              t.supervisorBadgeReady,
            ),
          ),
          h('div', { className: 'dshg-sec-sub' }, t.supervisorDescWinReady),
          h('div', { className: 'dshg-path-box' }, guard.exePath || 'DSHGuard.exe'),
          h(
            'button',
            {
              className: 'dshg-btn dshg-btn-primary',
              disabled: launching,
              onClick: () => void launchDesktopGuard(),
            },
            h(SvgIcon, { name: 'window', size: 15 }),
            launching ? t.btnOpeningGuard : t.btnOpenGuard,
          ),
        )
      }

      if (isWin) {
        const feats = [
          { icon: 'bolt', title: t.feat1Title, sub: t.feat1Sub },
          { icon: 'key', title: t.feat2Title, sub: t.feat2Sub },
          { icon: 'layers', title: t.feat3Title, sub: t.feat3Sub },
          { icon: 'shield', title: t.feat4Title, sub: t.feat4Sub },
        ]
        return h(
          'div',
          { className: 'dshg-card dshg-card-spotlight' },
          h(
            'div',
            { className: 'dshg-sec-head' },
            h(
              'span',
              { style: { display: 'inline-flex', alignItems: 'center', gap: 6 } },
              h(GuardIcon, { size: 15, active: true }),
              t.supervisorTitleWinMissing,
            ),
            h('span', { className: 'dshg-badge', 'data-tone': 'blue' }, t.supervisorBadgeWin),
          ),
          h('div', { className: 'dshg-sec-sub' }, t.supervisorDescWinMissing),
          h(
            'div',
            { className: 'dshg-feat-grid' },
            ...feats.map((f) =>
              h(
                'div',
                { key: f.title, className: 'dshg-feat-item' },
                h('div', { className: 'dshg-feat-ico' }, h(SvgIcon, { name: f.icon, size: 14 })),
                h(
                  'div',
                  null,
                  h('div', { className: 'dshg-feat-t' }, f.title),
                  h('div', { className: 'dshg-feat-s' }, f.sub),
                ),
              ),
            ),
          ),
          h(
            'a',
            {
              className: 'dshg-btn dshg-btn-primary',
              href: installerUrl,
              target: '_blank',
              rel: 'noopener noreferrer',
            },
            h(SvgIcon, { name: 'download', size: 15 }),
            t.btnDownloadExe,
          ),
          h(
            'div',
            { className: 'dshg-btn-row' },
            h(
              'a',
              {
                className: 'dshg-btn',
                href: repoUrl,
                target: '_blank',
                rel: 'noopener noreferrer',
              },
              h(SvgIcon, { name: 'external', size: 12 }),
              t.btnViewRepo,
            ),
            h(
              'button',
              {
                className: 'dshg-btn',
                onClick: () => void fetchOverview(),
              },
              h(SvgIcon, { name: 'refresh', size: 12 }),
              t.btnRedetect,
            ),
          ),
        )
      }

      return h(
        'div',
        { className: 'dshg-card' },
        h(
          'div',
          { className: 'dshg-sec-head' },
          h(
            'span',
            { style: { display: 'inline-flex', alignItems: 'center', gap: 6 } },
            h(GuardIcon, { size: 15, active: true }),
            t.supervisorTitleOther,
          ),
          platform ? h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, platform) : null,
        ),
        h('div', { className: 'dshg-sec-sub' }, t.supervisorDescOther(platform || 'POSIX')),
        h(
          'div',
          { className: 'dshg-btn-row' },
          h(
            'a',
            {
              className: 'dshg-btn',
              href: repoUrl,
              target: '_blank',
              rel: 'noopener noreferrer',
            },
            h(SvgIcon, { name: 'external', size: 12 }),
            t.btnViewRepo,
          ),
          h(
            'a',
            {
              className: 'dshg-btn',
              href: releaseUrl,
              target: '_blank',
              rel: 'noopener noreferrer',
            },
            h(SvgIcon, { name: 'download', size: 12 }),
            t.btnViewReleases,
          ),
        ),
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
      const releaseUrl = (guard && guard.releaseUrl) || (snapshots && snapshots.releaseUrl) || RELEASE_FALLBACK
      const installerUrl = (data && data.installerUrl) || (guard && guard.installerUrl) || (snapshots && snapshots.installerUrl) || INSTALLER_FALLBACK
      const repoUrl = (data && data.repoUrl) || (guard && guard.repoUrl) || REPO_FALLBACK
      const creds = (health && health.credentials) || {}
      const refKeys = Array.isArray(creds.referencedKeys) ? creds.referencedKeys : []
      const snapList = snapshots && Array.isArray(snapshots.snapshots) ? snapshots.snapshots : []
      const profiles = health && Array.isArray(health.profiles) ? health.profiles : []

      const webProfile = profiles.find((p) => p.target === 'web')
      const deskProfile = profiles.find((p) => p.target === 'desktop')
      const kpiProfilesText = profiles.length
        ? `${webProfile && webProfile.exists ? webProfile.bundlesCount : 0} · ${deskProfile && deskProfile.exists ? deskProfile.bundlesCount : 0}`
        : '—'
      const refReadyCount = refKeys.filter((r) => r.ready).length
      const kpiCredsReady = refKeys.length > 0 ? refReadyCount : (creds.nonEmptyKeysCount || 0)
      const kpiCredsTotal = refKeys.length > 0 ? refKeys.length : (creds.totalKeysCount || 0)
      const kpiCredsText = health ? (creds.fileExists ? `${kpiCredsReady}/${kpiCredsTotal}` : '0/0') : '—'
      const kpiSnapsText = snapshots ? String(snapshots.total || 0) : '—'

      const hasProfileIssues = Boolean(health && Array.isArray(health.issues) && health.issues.length > 0)
      const hasMissingCreds = Boolean(
        !creds.fileExists || (Array.isArray(creds.missingReferencedKeys) && creds.missingReferencedKeys.length > 0),
      )

      const credRatioPct = kpiCredsTotal > 0 ? Math.min(100, Math.round((kpiCredsReady / kpiCredsTotal) * 100)) : 0

      function renderDesktopCta(openLabel, downloadLabel) {
        if (platform === 'win32' && guard.installed) {
          return h(
            'button',
            {
              className: 'dshg-btn dshg-btn-primary',
              disabled: snap.launching,
              onClick: () => void launchDesktopGuard(),
            },
            h(SvgIcon, { name: 'window', size: 14 }),
            snap.launching ? t.btnOpeningGuard : openLabel,
          )
        }
        if (platform === 'win32') {
          return h(
            'a',
            {
              className: 'dshg-btn dshg-btn-primary',
              href: installerUrl,
              target: '_blank',
              rel: 'noopener noreferrer',
            },
            h(SvgIcon, { name: 'download', size: 14 }),
            downloadLabel,
          )
        }
        return h(
          'a',
          {
            className: 'dshg-btn',
            href: repoUrl,
            target: '_blank',
            rel: 'noopener noreferrer',
          },
          h(SvgIcon, { name: 'external', size: 13 }),
          t.btnViewRepo,
        )
      }

      return h(
        'div',
        { className: 'dshg-root', 'data-variant': variant || 'dock' },

        // Brand Header Card
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
                h(
                  'div',
                  { className: 'dshg-title-row' },
                  h('span', { className: 'dshg-title' }, t.brand),
                  h('span', { className: 'dshg-ver' }, t.versionPill),
                ),
                h('div', { className: 'dshg-sub' }, t.subtitle),
              ),
            ),
            h(
              'div',
              { className: 'dshg-actions' },
              h(
                'button',
                {
                  className: 'dshg-btn dshg-btn-icon',
                  disabled: snap.loading,
                  title: t.refresh,
                  'aria-label': t.refresh,
                  onClick: () => void fetchOverview(),
                },
                h(SvgIcon, { name: 'refresh', size: 14, className: snap.loading ? 'dshg-spin' : '' }),
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
                  h('span', { className: 'dshg-dot' }),
                  health.healthy ? t.healthy : t.unhealthy(health.issuesCount || 0),
                ),
                snap.checkedAt
                  ? h(
                      'span',
                      { style: { fontSize: 11, color: 'var(--dshg-text-3)', fontVariantNumeric: 'tabular-nums' } },
                      t.checkedAt(formatClockOnly(snap.checkedAt)),
                    )
                  : null,
              )
            : null,
        ),

        // Compact 3-Column KPI Strip
        data
          ? h(
              'div',
              { className: 'dshg-kpi-strip' },
              h(
                'button',
                {
                  className: 'dshg-kpi',
                  'data-active': snap.tab === 'overview' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'overview' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiProfilesLabel),
                h('span', { className: 'dshg-kpi-val' }, kpiProfilesText),
                h('span', { className: 'dshg-kpi-sub' }, t.kpiProfilesSub),
              ),
              h(
                'button',
                {
                  className: 'dshg-kpi',
                  'data-active': snap.tab === 'creds' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'creds' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiCredsLabel),
                h('span', { className: 'dshg-kpi-val' }, kpiCredsText),
                h('span', { className: 'dshg-kpi-sub' }, t.kpiCredsSub),
              ),
              h(
                'button',
                {
                  className: 'dshg-kpi',
                  'data-active': snap.tab === 'snapshots' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'snapshots' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiSnapsLabel),
                h('span', { className: 'dshg-kpi-val' }, kpiSnapsText),
                h('span', { className: 'dshg-kpi-sub' }, t.kpiSnapsSub),
              ),
            )
          : null,

        // Segmented Control Tabs
        data
          ? h(
              'div',
              { className: 'dshg-seg', role: 'tablist' },
              h(
                'button',
                {
                  className: 'dshg-seg-btn',
                  role: 'tab',
                  'aria-selected': snap.tab === 'overview',
                  'data-active': snap.tab === 'overview' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'overview' }),
                },
                t.tabOverview,
                hasProfileIssues ? h('span', { className: 'dshg-seg-dot' }) : null,
              ),
              h(
                'button',
                {
                  className: 'dshg-seg-btn',
                  role: 'tab',
                  'aria-selected': snap.tab === 'creds',
                  'data-active': snap.tab === 'creds' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'creds' }),
                },
                t.tabCreds,
                hasMissingCreds ? h('span', { className: 'dshg-seg-dot' }) : null,
              ),
              h(
                'button',
                {
                  className: 'dshg-seg-btn',
                  role: 'tab',
                  'aria-selected': snap.tab === 'snapshots',
                  'data-active': snap.tab === 'snapshots' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'snapshots' }),
                },
                t.tabSnapshots,
              ),
            )
          : null,

        snap.error ? h('div', { className: 'dshg-error' }, snap.error) : null,
        snap.notice ? h('div', { className: 'dshg-notice' }, snap.notice) : null,

        !data && snap.loading
          ? h('div', { className: 'dshg-card', style: { color: 'var(--dshg-text-2)' } }, t.loading)
          : null,

        // TAB 1: Overview
        data && snap.tab === 'overview'
          ? [
              h(SupervisorCard, {
                key: 'supervisor',
                platform,
                guard,
                installerUrl,
                releaseUrl,
                repoUrl,
                launching: snap.launching,
                t,
              }),
              health && profiles.length > 0
                ? h(
                    'div',
                    { key: 'profiles', className: 'dshg-card' },
                    h('div', { className: 'dshg-sec-head' }, h('span', null, t.profilesTitle)),
                    h(
                      'div',
                      { className: 'dshg-profile-grid' },
                      ...profiles.map((p) => h(ProfileSubcard, { key: p.target, profile: p, t })),
                    ),
                  )
                : null,
              hasProfileIssues
                ? h(
                    'div',
                    { key: 'issues', className: 'dshg-card' },
                    h(
                      'div',
                      { className: 'dshg-sec-head' },
                      h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 6, color: '#f59e0b' } }, h(SvgIcon, { name: 'alert', size: 14 }), t.issuesTitle),
                    ),
                    ...health.issues.map((issue, idx) =>
                      h('div', { key: idx, style: { fontSize: 11.5, color: '#f59e0b', wordBreak: 'break-word' } }, `• ${issue}`),
                    ),
                    platform === 'win32'
                      ? renderDesktopCta(t.issueActionOpen, t.issueActionDownload)
                      : null,
                  )
                : null,
            ]
          : null,

        // TAB 2: Masked Credentials Readiness
        data && snap.tab === 'creds' && health
          ? [
              h(
                'div',
                { key: 'cred-status', className: 'dshg-card' },
                h(
                  'div',
                  { className: 'dshg-sec-head' },
                  h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 6 } }, h(SvgIcon, { name: 'key', size: 14 }), t.credsTitle),
                  h(
                    'span',
                    { className: 'dshg-badge', 'data-tone': creds.fileExists && !hasMissingCreds ? 'ok' : creds.fileExists ? 'warn' : 'muted' },
                    creds.fileExists
                      ? t.credReadyBadge(kpiCredsReady, kpiCredsTotal)
                      : t.credMissingFile,
                  ),
                ),
                h('div', { className: 'dshg-sec-sub' }, t.credsPrivacyHint),
                creds.fileExists
                  ? h(
                      'div',
                      { className: 'dshg-progress-wrap' },
                      h(
                        'div',
                        { className: 'dshg-row', style: { fontSize: 11, color: 'var(--dshg-text-2)' } },
                        h(
                          'span',
                          null,
                          refKeys.length > 0
                            ? t.credProgressPatch(refReadyCount, refKeys.length)
                            : t.credProgressFile(creds.nonEmptyKeysCount || 0, creds.totalKeysCount || 0),
                        ),
                        h('span', { style: { fontVariantNumeric: 'tabular-nums' } }, `${credRatioPct}%`),
                      ),
                      h(
                        'div',
                        { className: 'dshg-progress-track' },
                        h('div', { className: 'dshg-progress-fill', style: { width: `${credRatioPct}%` } }),
                      ),
                    )
                  : null,
                refKeys.length > 0
                  ? h(
                      'div',
                      { className: 'dshg-cred-list' },
                      ...refKeys.map((item) =>
                        h(
                          'div',
                          { key: item.key, className: 'dshg-cred-item' },
                          h(
                            'span',
                            { className: 'dshg-cred-key' },
                            h(SvgIcon, { name: 'key', size: 12 }),
                            item.key,
                          ),
                          h(
                            'span',
                            { className: 'dshg-badge', 'data-tone': item.ready ? 'ok' : 'warn' },
                            h(SvgIcon, { name: item.ready ? 'check' : 'alert', size: 11 }),
                            item.ready ? t.credStatusReady : t.credStatusMissing,
                          ),
                        ),
                      ),
                    )
                  : h('div', { className: 'dshg-sec-sub' }, t.credNoRefs),
              ),
              h(
                'div',
                { key: 'cred-upsell', className: 'dshg-card dshg-card-spotlight' },
                h('div', { className: 'dshg-sec-head' }, h('span', null, t.credUpsellTitle)),
                h('div', { className: 'dshg-sec-sub' }, t.credUpsellDesc),
                renderDesktopCta(t.credUpsellOpen, t.credUpsellDownload),
              ),
            ]
          : null,

        // TAB 3: Snapshots Timeline
        data && snap.tab === 'snapshots' && snapshots
          ? h(
              'div',
              { className: 'dshg-card' },
              h(
                'div',
                { className: 'dshg-sec-head' },
                h('span', { style: { display: 'inline-flex', alignItems: 'center', gap: 6 } }, h(SvgIcon, { name: 'clock', size: 14 }), t.snapshotsTitle),
                h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.snapshotsTotal(snapshots.total || 0)),
              ),
              snapList.length > 0
                ? [
                    h(
                      'div',
                      { key: 'list', className: 'dshg-timeline' },
                      ...snapList.slice(0, 10).map((s) =>
                        h(
                          'div',
                          { key: s.id, className: 'dshg-snap-item' },
                          h(
                            'div',
                            { className: 'dshg-snap-head' },
                            h(
                              'span',
                              { className: 'dshg-snap-title' },
                              h('span', {
                                className: 'dshg-track-dot',
                                style: { background: s.target === 'desktop' ? '#22c55e' : '#3b82f6' },
                              }),
                              s.reason || t.snapNoReason,
                            ),
                            h(
                              'span',
                              { className: 'dshg-badge', 'data-tone': s.target === 'desktop' ? 'ok' : 'blue' },
                              s.target === 'desktop' ? t.desktopTrack : t.webTrack,
                            ),
                          ),
                          h(
                            'div',
                            { className: 'dshg-row', style: { fontSize: 11, color: 'var(--dshg-text-2)' } },
                            h('span', null, t.snapItems(s.itemCount || 0)),
                            h('span', { style: { fontVariantNumeric: 'tabular-nums' } }, formatShortTime(s.createdAt)),
                          ),
                          h('div', { className: 'dshg-mono' }, s.id),
                        ),
                      ),
                    ),
                    h('div', { key: 'snap-cta' }, renderDesktopCta(t.snapActionOpen, t.snapActionDownload)),
                  ]
                : h(
                    'div',
                    { className: 'dshg-empty' },
                    h('div', { style: { color: 'var(--dshg-text-2)' } }, h(SvgIcon, { name: 'clock', size: 22 })),
                    h('div', { style: { fontWeight: 600, fontSize: 12, color: 'var(--dshg-text)' } }, t.snapshotsEmptyTitle),
                    h('div', { style: { fontSize: 11, lineHeight: 1.5 } }, t.snapshotsEmptyDesc),
                    renderDesktopCta(t.snapActionOpen, t.snapActionDownload),
                  ),
            )
          : null,

        // Subtle Footer
        h(
          'div',
          { className: 'dshg-footer' },
          h('span', null, t.footerNote),
          h(
            'a',
            { href: repoUrl, target: '_blank', rel: 'noopener noreferrer' },
            t.footerHome,
          ),
        ),
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
