/**
 * Client half of @gluztm/dsh-guard — Cross-platform DSH profile manager,
 * plugin/MCP switchboard, one-way .credentials.yaml writer, llm-pi-ai inspector,
 * and local config snapshot & diff rollback panel for DeepSeek Harness.
 *
 * Strictly zero-secret-leak: never reads, displays, or transmits plaintext secret values.
 * Uses only React.createElement, useState, useEffect, and Component for universal ModuleLoader compatibility.
 */
window.__ModuleLoader__.load({
  id: '@gluztm/dsh-guard',
  factory(require) {
    const React = require('react')
    const h = React.createElement

    const SIDE_ID = 'dsh-guard'
    const API_BASE = 'api/dsh-guard'
    const STYLE_ID = 'dsh-guard-panel-styles-v243'
    const VERSION = '2.4.3'
    const REPO_FALLBACK = 'https://github.com/gluztm/DSHGuard'
    const RELEASE_FALLBACK = 'https://github.com/gluztm/DSHGuard/releases/tag/2.4.3'
    const INSTALLER_FALLBACK = 'https://github.com/gluztm/DSHGuard/releases/download/2.4.3/DSHGuard-Setup-2.4.3.exe'

    const ENV_KEY_PRESETS = [
      { id: 'deepseek', label: 'DeepSeek 官方', envKey: 'DEEPSEEK_API_KEY' },
      { id: 'openai', label: 'OpenAI / 通用中继', envKey: 'OPENAI_API_KEY' },
      { id: 'anthropic', label: 'Anthropic Claude', envKey: 'ANTHROPIC_API_KEY' },
      { id: 'gemini', label: 'Google Gemini', envKey: 'GEMINI_API_KEY' },
      { id: 'openrouter', label: 'OpenRouter', envKey: 'OPENROUTER_API_KEY' },
      { id: 'siliconflow', label: '硅基流动 SiliconFlow', envKey: 'SILICONFLOW_API_KEY' },
      { id: 'stepfun', label: '阶跃星辰 StepFun', envKey: 'STEPFUN_API_KEY' },
    ]

    const COPY = {
      zh: {
        brand: 'DSH 守护伴生',
        subtitle: '运行时底座 · 扩展 · 上下文 · 凭据 · 快照',
        guideDesc: '自动识别当前宿主（桌面端 / Web 端），内置依赖自愈、插件/MCP 启停与探活、上下文与 Skills 治理、单向写 .credentials.yaml 与本地快照置顶回滚。',
        versionPill: `v${VERSION}`,
        refresh: '刷新',
        refreshing: '同步中',
        langSwitch: 'EN',
        healthy: '配置健康',
        unhealthy: (n) => `${n} 项待处理`,
        loading: '正在读取当前运行档案、上下文负担、MCP/模型与快照…',
        retry: '重试',
        checkedAt: (t) => `同步于 ${t}`,
        openDesktopBtn: '打开桌面端',
        launchingBtn: '正在拉起…',
        tabOverview: '总览',
        tabPlugins: '扩展',
        tabContext: '上下文',
        tabCreds: '凭据',
        tabSnapshots: '快照',
        kpiHealth: '配置健康',
        kpiHealthOk: '一致',
        kpiHealthWarn: (n) => `${n} 项`,
        kpiExt: '插件 / MCP',
        kpiCreds: '密钥 / 提供商',
        kpiSnaps: '配置快照',
        secProfiles: '当前运行档案',
        webTrack: 'Web 端',
        desktopTrack: '桌面端',
        present: '已就绪',
        absent: '未初始化',
        depsLabel: '总依赖',
        thirdPartyLabel: '第三方',
        mcpLabel: 'MCP 服务',
        disabledLabel: '已禁用',
        brokenLabel: '断链数',
        repairLinksBtn: '一键修复断链',
        repairingBtn: '修复中…',
        allHealthyTitle: '当前运行档案结构健康且无断裂软链接',
        allHealthySub: '当前端的插件依赖、MCP 补丁与环境配置均已对齐，可在上方切换标签管理扩展/探活、上下文与 Skills、单向写密钥或配置快照。',
        quickSnapWeb: '备份当前配置',
        quickGoPlugins: '管理扩展开关',
        quickGoContext: '查看上下文与 Skills',
        contextCardTitle: '开机上下文负担画像',
        contextTokens: (n) => `~${n} tokens 基线`,
        contextLevelLight: '轻量',
        contextLevelModerate: '适中',
        contextLevelHeavy: '较重',
        contextStatsLine: (plug, mcp, sk) => `启用第三方 ${plug} · MCP ${mcp} · 模型可见技能 ${sk}`,
        contextPromptsTitle: '常驻提示词与配置注入体积',
        contextSkillsTitle: 'Skills 可见性治理',
        contextSkillSearch: '搜索技能名称、来源或说明…',
        contextSkillsEmpty: '未匹配到技能文件。',
        skillReadOnly: '插件只读',
        skillModelVis: '模型可见',
        skillCmdVis: '命令可见',
        promptsBytes: (b, tk) => `${b} B · ~${tk} tok`,
        warningsTitle: '巡检提示',
        searchPlaceholder: '搜索插件包名、loader ID 或 MCP 服务名…',
        mcpSectionTitle: 'MCP 服务管理与探活',
        mcpEmpty: '当前档案尚未配置 MCP 服务。',
        mcpProbeBtn: '探测本地 MCP',
        mcpProbingBtn: '探测中…',
        mcpTimeoutBtn: '超时',
        mcpTimeoutSave: '保存超时',
        mcpTimeoutHint: '单次工具调用超时（5000 - 600000 ms）',
        pluginsSectionTitle: '已安装插件',
        pluginsEmpty: '未匹配到插件条目。',
        badgeCore: '核心保护',
        badgeSelf: '当前伴生',
        badgeEnabled: '已启用',
        badgeDisabled: '已禁用',
        badgeAllCount: (shown, total) => `${shown} / ${total}`,
        boundaryPluginsSummary: '能力边界说明：为什么插件全新安装/卸载与强杀锁死进程不在此完成？',
        boundaryPluginsP1: '本插件运行于当前 DSH Node 宿主进程内部：在运行时热删除或重装 node_modules 可能导致已加载模块撕裂锁死；同时为遵循插件最小权限安全红线，本插件不会从远端拉取二进制安装包或托管宿主进程生命周期。',
        boundaryPluginsP2: '在伴生插件内，你可直接安全启停第三方插件与 MCP 服务、调整 MCP 超时、执行本地只读探活并修复断裂依赖链接；完整增删插件与进程外强启停由独立原生桌面端承担。',
        boundaryCredsSummary: '能力边界说明：为什么不在此回显/导出明文密钥或提供 Windows DPAPI 加密库？',
        boundaryCredsP1: '安全合规要求持有前端请求能力的插件不得反向读取并回显真实 API 密钥。本插件采用「只核验键名 + 单向覆盖写入 .credentials.yaml」机制，所有明文仅从输入框单向落盘（0o600），响应报文绝不返回密钥字符。',
        boundaryCredsP2: '硬件绑定的 Windows DPAPI 凭据保险箱加密以及跨 CC Switch 等工具链批量迁移属于系统原生能力，仅独立的 Windows 原生桌面端具备。',
        boundarySnapsSummary: '能力边界说明：为什么配置快照仅恢复 YAML/JSON 配对文件而不覆盖整包扩展？',
        boundarySnapsP1: '伴生插件可在不干扰宿主运行的前提下，安全完成 cordis.patch.yml、cordis.yml、package.json 与 settings.yaml 的实时 SHA256 比对、备份、置顶保护与白名单一键回滚（自动排除 .credentials.yaml）。',
        boundarySnapsP2: '涵盖 node_modules 整包 ZIP 回溯与主程序崩溃后的带外拉起恢复必须脱离宿主进程执行，仅在独立原生桌面端提供。',
        desktopWinLink: `下载 Windows 原生桌面端 v${VERSION} ↗`,
        desktopReleaseLink: '查看 GitHub Releases ↗',
        desktopNonWinNote: '注：原生桌面端目前仅提供 Windows 安装包；当前平台的启停开关、上下文治理、单向写密钥与配置回滚已全部内置于伴生插件。',
        providersTitle: '模型提供商 (llm-pi-ai)',
        providersEmpty: '尚未在 cordis.patch.yml 中发现 llm-pi-ai 提供商配置。',
        modelsUnit: '个模型',
        keyReady: '密钥就绪',
        keyMissing: '缺密钥',
        setKeyBtn: '设置密钥',
        updateKeyBtn: '更新密钥',
        cancelBtn: '取消',
        keyPlaceholder: '输入 API Key（单向盲写，保存后立即清空，绝不回显）',
        writeKeyBtn: '写入 .credentials.yaml',
        writingKeyBtn: '写入中…',
        addKeyToggle: '+ 新增自定义环境变量密钥',
        presetSelectPlaceholder: '常用平台预设（可选自动填入键名）…',
        envNameLabel: '环境变量名 (如 DEEPSEEK_API_KEY)',
        credsAuditTitle: '.credentials.yaml 键名状态审计',
        credsAuditSub: '仅展示键名是否存在及保护标记（绝不读取或传输密钥字符）。',
        credProtected: '受保护 OAuth',
        credPresent: '已写入',
        credEnvOnly: '仅环境变量',
        credsEmpty: '当前尚未创建 .credentials.yaml 或 refs 下尚无键值。',
        snapCreateTargetBtn: '+ 备份当前配置',
        snapCreating: '备份中…',
        snapSourcePlugin: '插件快照',
        snapSourceDesktop: '桌面端快照',
        snapSame: '与磁盘一致',
        snapDiff: (n) => `${n} 处已变更`,
        snapFilesToggle: (n) => `查看 ${n} 个文件比对明细`,
        snapRestoreBtn: '恢复此配置快照',
        snapRestoring: '恢复中…',
        snapPinBtn: '📌 置顶',
        snapUnpinBtn: '取消置顶',
        snapPinnedBadge: '已置顶保护',
        snapNoFiles: '该快照暂无可比对配置文件',
        snapDesktopOnlyTip: '桌面端全量包产物需在桌面端还原',
        auditSectionSummary: (n) => `变更审计流水（最近 ${n} 条脱敏操作记录）`,
        auditEmpty: '暂无通过伴生插件写入的审计记录。',
        fileSame: '一致',
        fileModified: '与现行配置不同',
        fileMissing: '磁盘已删除',
        fileUnknown: '仅记录',
        snapshotsEmpty: '暂无本地配置快照',
        snapshotsEmptyDesc: '点击上方备份按钮，或在修改插件/MCP 开关时自动留存一份配置档案。',
        footerNote: `@gluztm/dsh-guard v${VERSION}`,
        footerHome: 'GitHub',
      },
      en: {
        brand: 'DSH Companion',
        subtitle: 'Runtime Control · Ext · Context · Creds · Snapshots',
        guideDesc: 'Auto-detects active host (Desktop / Web): symlink repair, plugin & MCP toggles/probe, context & Skills visibility, one-way .credentials.yaml writes, and pinned YAML snapshots.',
        versionPill: `v${VERSION}`,
        refresh: 'Refresh',
        refreshing: 'Syncing',
        langSwitch: '中文',
        healthy: 'Healthy',
        unhealthy: (n) => `${n} alert`,
        loading: 'Loading active profile, context load, MCP/providers, and snapshots…',
        retry: 'Retry',
        checkedAt: (t) => `Synced ${t}`,
        openDesktopBtn: 'Open Desktop',
        launchingBtn: 'Launching…',
        tabOverview: 'Overview',
        tabPlugins: 'Ext',
        tabContext: 'Context',
        tabCreds: 'Creds',
        tabSnapshots: 'Snaps',
        kpiHealth: 'Health',
        kpiHealthOk: 'Synced',
        kpiHealthWarn: (n) => `${n} alert`,
        kpiExt: 'Plugins / MCP',
        kpiCreds: 'Keys / Providers',
        kpiSnaps: 'Snapshots',
        secProfiles: 'Active Runtime Profile',
        webTrack: 'Web',
        desktopTrack: 'Desktop',
        present: 'Ready',
        absent: 'Absent',
        depsLabel: 'Deps',
        thirdPartyLabel: '3rd-party',
        mcpLabel: 'MCP',
        disabledLabel: 'Disabled',
        brokenLabel: 'Broken Links',
        repairLinksBtn: 'Repair Broken Links',
        repairingBtn: 'Repairing…',
        allHealthyTitle: 'Active profile is healthy with no broken links',
        allHealthySub: 'Dependencies, MCP entries, and Cordis patches look clean. Switch tabs above to manage extensions, context/Skills, blind-write credentials, or snapshots.',
        quickSnapWeb: 'Backup Config',
        quickGoPlugins: 'Manage Extensions',
        quickGoContext: 'Context & Skills',
        contextCardTitle: 'Startup Context Footprint',
        contextTokens: (n) => `~${n} tokens baseline`,
        contextLevelLight: 'Light',
        contextLevelModerate: 'Moderate',
        contextLevelHeavy: 'Heavy',
        contextStatsLine: (plug, mcp, sk) => `${plug} 3rd-party · ${mcp} MCP · ${sk} model-visible skills`,
        contextPromptsTitle: 'Permanent Prompt & Config Sizes',
        contextSkillsTitle: 'Skills Visibility Governance',
        contextSkillSearch: 'Filter skill name, source, or summary…',
        contextSkillsEmpty: 'No matching skills found.',
        skillReadOnly: 'Plugin (RO)',
        skillModelVis: 'Model',
        skillCmdVis: 'Command',
        promptsBytes: (b, tk) => `${b} B · ~${tk} tok`,
        warningsTitle: 'Diagnostics',
        searchPlaceholder: 'Filter package name, loader ID, or MCP server…',
        mcpSectionTitle: 'MCP Servers & Local Probe',
        mcpEmpty: 'No MCP entries configured in this profile.',
        mcpProbeBtn: 'Probe Local MCPs',
        mcpProbingBtn: 'Probing…',
        mcpTimeoutBtn: 'Timeout',
        mcpTimeoutSave: 'Save ms',
        mcpTimeoutHint: 'Tool call timeout in ms (5000 - 600000)',
        pluginsSectionTitle: 'Installed Plugins',
        pluginsEmpty: 'No matching plugins.',
        badgeCore: 'Core',
        badgeSelf: 'Self',
        badgeEnabled: 'On',
        badgeDisabled: 'Off',
        badgeAllCount: (shown, total) => `${shown} / ${total}`,
        boundaryPluginsSummary: 'Capability boundary: why fresh package install & force process control live outside this plugin',
        boundaryPluginsP1: 'This plugin shares the live DSH Node process: mutating node_modules via npm install at runtime can lock or tear active modules, and companion plugins intentionally lack remote binary execution and host watchdog rights.',
        boundaryPluginsP2: 'Safe plugin & MCP toggling, local loopback/stdio MCP probing, timeout tuning, and symlink repair work right here; full package install/uninstall and process supervision belong to the native desktop supervisor.',
        boundaryCredsSummary: 'Security boundary: why plaintext key readback & Windows DPAPI vault live outside this plugin',
        boundaryCredsP1: 'By security policy, Web UI plugins must never reverse-read and display plaintext secrets. This tab enforces strict one-way blind-write directly into .credentials.yaml refs: while showing only key presence.',
        boundaryCredsP2: 'Hardware-backed Windows DPAPI encryption vaults and CC Switch cross-tool migrations rely on native Windows security APIs unavailable inside a cross-platform DSH plugin.',
        boundarySnapsSummary: 'Runtime boundary: why snapshots restore YAML/JSON configs rather than full node_modules bundles',
        boundarySnapsP1: 'Within the running DSH process, this tab safely computes live SHA256 diffs, creates/pins snapshots, and performs whitelist rollback of cordis.patch.yml, cordis.yml, package.json, and settings.yaml (excluding secrets).',
        boundarySnapsP2: 'Full extension bundle ZIP replacement and out-of-band crash recovery require shutting down or restarting the DSH process from an external native binary.',
        desktopWinLink: `Download Windows Desktop v${VERSION} ↗`,
        desktopReleaseLink: 'GitHub Releases ↗',
        desktopNonWinNote: 'Note: The native desktop shell is Windows-only; toggling, context governance, blind-write credentials, and config rollback work natively on this OS.',
        providersTitle: 'Model Providers (llm-pi-ai)',
        providersEmpty: 'No llm-pi-ai provider entries found in cordis.patch.yml.',
        modelsUnit: 'models',
        keyReady: 'Key Ready',
        keyMissing: 'Missing Key',
        setKeyBtn: 'Set Key',
        updateKeyBtn: 'Update Key',
        cancelBtn: 'Cancel',
        keyPlaceholder: 'Paste API key (one-way write only; cleared & never echoed)',
        writeKeyBtn: 'Save to .credentials.yaml',
        writingKeyBtn: 'Saving…',
        addKeyToggle: '+ Add custom env ref key',
        presetSelectPlaceholder: 'Common platform presets (auto-fill env key)…',
        envNameLabel: 'Env Var Name (e.g. DEEPSEEK_API_KEY)',
        credsAuditTitle: '.credentials.yaml Key Presence Audit',
        credsAuditSub: 'Verifies key readiness and protected OAuth flags without reading secret values.',
        credProtected: 'Protected OAuth',
        credPresent: 'Configured',
        credEnvOnly: 'Env Only',
        credsEmpty: 'No .credentials.yaml entries found yet.',
        snapCreateTargetBtn: '+ Create Snapshot',
        snapCreating: 'Backing up…',
        snapSourcePlugin: 'Plugin',
        snapSourceDesktop: 'Desktop',
        snapSame: 'Matches Disk',
        snapDiff: (n) => `${n} modified`,
        snapFilesToggle: (n) => `Inspect ${n} file diff(s)`,
        snapRestoreBtn: 'Restore Config Files',
        snapRestoring: 'Restoring…',
        snapPinBtn: '📌 Pin',
        snapUnpinBtn: 'Unpin',
        snapPinnedBadge: 'Pinned',
        snapNoFiles: 'No comparable config files in this snapshot',
        snapDesktopOnlyTip: 'Desktop binary archives require the native desktop app',
        auditSectionSummary: (n) => `Mutation Audit Log (latest ${n} redacted events)`,
        auditEmpty: 'No mutation events logged yet.',
        fileSame: 'Same',
        fileModified: 'Changed on disk',
        fileMissing: 'Deleted on disk',
        fileUnknown: 'Recorded',
        snapshotsEmpty: 'No configuration snapshots yet',
        snapshotsEmptyDesc: 'Create a manual backup above or toggle an extension to automatically preserve a snapshot.',
        footerNote: `@gluztm/dsh-guard v${VERSION}`,
        footerHome: 'GitHub',
      },
    }

    const CSS = `
      @keyframes dshg-spin { to { transform: rotate(360deg); } }
      @keyframes dshg-pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.4; } }

      .dshg-wrap {
        --dshg-bg: var(--dsw-alias-bg-container, transparent);
        --dshg-card: var(--dsw-alias-card-fill, var(--dsw-alias-bg-elevated, rgba(127, 127, 127, 0.06)));
        --dshg-card-hover: var(--dsw-alias-card-fill-hover, var(--dsw-alias-fill-secondary, rgba(127, 127, 127, 0.1)));
        --dshg-fill: var(--dsw-alias-fill-l1, var(--dsw-alias-fill-tertiary, rgba(127, 127, 127, 0.12)));
        --dshg-fill-subtle: var(--dsw-alias-bg-layer-2, var(--dsw-alias-fill-quaternary, rgba(127, 127, 127, 0.04)));
        --dshg-border: var(--dsw-alias-border-l1, var(--dsw-alias-border-secondary, rgba(127, 127, 127, 0.18)));
        --dshg-border-strong: var(--dsw-alias-border-l2, var(--dsw-alias-border-primary, rgba(127, 127, 127, 0.32)));
        --dshg-text: var(--dsw-alias-label-primary, var(--dsw-alias-text-primary, inherit));
        --dshg-text-2: var(--dsw-alias-label-secondary, var(--dsw-alias-text-secondary, rgba(127, 127, 127, 0.92)));
        --dshg-text-3: var(--dsw-alias-label-tertiary, var(--dsw-alias-text-tertiary, rgba(127, 127, 127, 0.7)));
        --dshg-accent: var(--dsw-alias-brand-primary, var(--dsw-alias-color-primary, #2563eb));
        --dshg-accent-soft: rgba(59, 130, 246, 0.11);
        --dshg-accent-border: rgba(59, 130, 246, 0.28);
        --dshg-ok-soft: rgba(16, 185, 129, 0.12);
        --dshg-ok-border: rgba(16, 185, 129, 0.28);
        --dshg-warn-soft: rgba(245, 158, 11, 0.12);
        --dshg-warn-border: rgba(245, 158, 11, 0.3);
        display: flex;
        flex-direction: column;
        gap: 11px;
        height: 100%;
        min-height: 0;
        padding: 12px 14px 16px;
        box-sizing: border-box;
        overflow-y: auto;
        background: var(--dshg-bg);
        color: var(--dshg-text);
        font-family: inherit;
        font-size: 13px;
        line-height: 1.45;
      }
      .dshg-wrap[data-variant="main"] {
        max-width: 960px;
        margin: 0 auto;
        padding: 22px 26px 32px;
      }
      .dshg-tab-body {
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .dshg-header {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 10px;
        flex-wrap: wrap;
      }
      .dshg-brand {
        display: flex;
        align-items: center;
        gap: 9px;
        min-width: 0;
      }
      .dshg-logo {
        width: 30px;
        height: 30px;
        border-radius: 9px;
        display: flex;
        align-items: center;
        justify-content: center;
        background: linear-gradient(135deg, var(--dshg-accent), #6366f1);
        color: #ffffff;
        flex-shrink: 0;
        box-shadow: 0 2px 7px rgba(59, 130, 246, 0.24);
      }
      .dshg-title-box {
        display: flex;
        flex-direction: column;
        min-width: 0;
      }
      .dshg-title-row {
        display: flex;
        align-items: center;
        gap: 6px;
      }
      .dshg-title {
        font-size: 14px;
        font-weight: 650;
        letter-spacing: -0.01em;
        white-space: nowrap;
      }
      .dshg-ver {
        font-size: 10.5px;
        font-weight: 500;
        padding: 1px 6px;
        border-radius: 99px;
        background: var(--dshg-fill);
        color: var(--dshg-text-2);
        border: 1px solid var(--dshg-border);
      }
      .dshg-sub {
        font-size: 11px;
        color: var(--dshg-text-3);
        white-space: nowrap;
        overflow: hidden;
        text-overflow: ellipsis;
      }
      .dshg-actions {
        display: flex;
        align-items: center;
        gap: 6px;
        flex-shrink: 0;
      }
      .dshg-btn {
        appearance: none;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 5px;
        height: 28px;
        padding: 0 10px;
        border-radius: 8px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        color: var(--dshg-text);
        font-family: inherit;
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
        opacity: 0.52;
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
        font-weight: 600;
      }
      .dshg-btn-primary:hover:not(:disabled) {
        background: var(--dshg-accent);
        border-color: transparent;
        opacity: 0.92;
      }
      .dshg-btn-sm {
        height: 24px;
        padding: 0 8px;
        font-size: 11px;
        border-radius: 6px;
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
        grid-template-columns: repeat(2, minmax(0, 1fr));
        gap: 6px;
      }
      .dshg-wrap[data-variant="main"] .dshg-kpi-strip {
        grid-template-columns: repeat(4, minmax(0, 1fr));
      }
      .dshg-kpi {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        border-radius: 10px;
        padding: 8px 10px;
        display: flex;
        flex-direction: column;
        gap: 2px;
        text-align: left;
        cursor: pointer;
        color: inherit;
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
        color: var(--dshg-text-3);
      }
      .dshg-kpi-val {
        font-size: 14.5px;
        font-weight: 650;
        letter-spacing: -0.02em;
      }
      .dshg-seg {
        display: grid;
        grid-template-columns: repeat(5, minmax(0, 1fr));
        gap: 3px;
        padding: 3px;
        border-radius: 10px;
        background: var(--dshg-fill-subtle);
        border: 1px solid var(--dshg-border);
      }
      .dshg-seg-2 {
        display: inline-flex;
        flex-wrap: wrap;
        gap: 3px;
        padding: 2px;
        border-radius: 8px;
        background: var(--dshg-fill-subtle);
        border: 1px solid var(--dshg-border);
      }
      .dshg-seg-2 .dshg-seg-btn {
        padding: 4px 9px;
      }
      .dshg-select {
        height: 29px;
        border-radius: 8px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill-subtle);
        color: var(--dshg-text);
        font-family: inherit;
        font-size: 11.5px;
        padding: 0 8px;
        outline: none;
        cursor: pointer;
      }
      .dshg-select option {
        background: #1c2230;
        color: #f3f4f6;
      }
      .dshg-seg-btn {
        appearance: none;
        border: none;
        background: transparent;
        color: var(--dshg-text-2);
        font-family: inherit;
        font-size: 11.5px;
        font-weight: 500;
        padding: 5px 4px;
        border-radius: 7px;
        cursor: pointer;
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 4px;
        transition: background 0.15s ease, color 0.15s ease;
      }
      .dshg-seg-btn[data-active="true"] {
        background: var(--dshg-card-hover);
        color: var(--dshg-text);
        font-weight: 600;
      }
      .dshg-card {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-card);
        border-radius: 11px;
        padding: 11px 12px;
        display: flex;
        flex-direction: column;
        gap: 9px;
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
      .dshg-profile-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(145px, 1fr));
        gap: 7px;
      }
      .dshg-subcard {
        border: 1px solid var(--dshg-border);
        border-radius: 9px;
        padding: 9px 10px;
        display: flex;
        flex-direction: column;
        gap: 5px;
        background: var(--dshg-fill-subtle);
      }
      .dshg-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 6px;
        font-size: 11.5px;
      }
      .dshg-toolbar {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        flex-wrap: wrap;
      }
      .dshg-search-wrap {
        position: relative;
        flex: 1 1 160px;
        min-width: 140px;
      }
      .dshg-search-ico {
        position: absolute;
        left: 9px;
        top: 50%;
        transform: translateY(-50%);
        color: var(--dshg-text-3);
        pointer-events: none;
      }
      .dshg-input {
        width: 100%;
        height: 29px;
        box-sizing: border-box;
        border-radius: 8px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill-subtle);
        color: var(--dshg-text);
        font-family: inherit;
        font-size: 11.5px;
        padding: 0 9px;
        outline: none;
        transition: border-color 0.15s ease, background 0.15s ease;
      }
      .dshg-input.with-ico {
        padding-left: 28px;
      }
      .dshg-input:focus {
        border-color: var(--dshg-accent);
        background: var(--dshg-card);
      }
      .dshg-list {
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .dshg-item {
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill-subtle);
        border-radius: 9px;
        padding: 8px 10px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .dshg-item-row {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
      }
      .dshg-item-main {
        min-width: 0;
        flex: 1;
        display: flex;
        flex-direction: column;
        gap: 2px;
      }
      .dshg-item-title {
        display: flex;
        align-items: center;
        gap: 6px;
        flex-wrap: wrap;
        font-size: 12px;
        font-weight: 600;
        word-break: break-word;
      }
      .dshg-mono {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 11px;
        word-break: break-all;
        color: var(--dshg-text-2);
      }
      .dshg-switch {
        position: relative;
        width: 34px;
        height: 19px;
        border-radius: 99px;
        border: 1px solid var(--dshg-border-strong);
        background: var(--dshg-fill);
        cursor: pointer;
        padding: 0;
        flex-shrink: 0;
        transition: background 0.16s ease, border-color 0.16s ease;
      }
      .dshg-switch[data-on="true"] {
        background: #10b981;
        border-color: #10b981;
      }
      .dshg-switch:disabled {
        opacity: 0.45;
        cursor: default;
      }
      .dshg-switch-thumb {
        position: absolute;
        top: 2px;
        left: 2px;
        width: 13px;
        height: 13px;
        border-radius: 50%;
        background: #ffffff;
        box-shadow: 0 1px 2px rgba(0, 0, 0, 0.22);
        transition: transform 0.16s ease;
      }
      .dshg-switch[data-on="true"] .dshg-switch-thumb {
        transform: translateX(15px);
      }
      .dshg-drawer {
        border-top: 1px solid var(--dshg-border);
        padding-top: 8px;
        display: flex;
        flex-direction: column;
        gap: 6px;
      }
      .dshg-drawer-row {
        display: flex;
        gap: 6px;
        align-items: center;
      }
      .dshg-pills {
        display: flex;
        flex-wrap: wrap;
        gap: 4px;
      }
      .dshg-pill {
        font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace;
        font-size: 10.5px;
        padding: 1.5px 6px;
        border-radius: 5px;
        border: 1px solid var(--dshg-border);
        background: var(--dshg-fill);
        color: var(--dshg-text-2);
        word-break: break-all;
      }
      .dshg-boundary {
        border: 1px solid var(--dshg-border);
        border-radius: 10px;
        background: var(--dshg-fill-subtle);
        overflow: hidden;
      }
      .dshg-boundary > summary {
        list-style: none;
        cursor: pointer;
        padding: 8px 11px;
        font-size: 11px;
        font-weight: 500;
        color: var(--dshg-text-2);
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        user-select: none;
      }
      .dshg-boundary > summary::-webkit-details-marker {
        display: none;
      }
      .dshg-boundary[open] > summary {
        border-bottom: 1px solid var(--dshg-border);
        color: var(--dshg-text);
      }
      .dshg-boundary-body {
        padding: 9px 11px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        font-size: 11px;
        line-height: 1.5;
        color: var(--dshg-text-2);
      }
      .dshg-boundary-links {
        display: flex;
        flex-wrap: wrap;
        gap: 8px;
        margin-top: 2px;
      }
      .dshg-boundary-links a {
        font-size: 11px;
        color: var(--dshg-accent);
        text-decoration: none;
        font-weight: 500;
      }
      .dshg-boundary-links a:hover {
        text-decoration: underline;
      }
      .dshg-track-dot {
        width: 7px;
        height: 7px;
        border-radius: 50%;
        flex-shrink: 0;
      }
      .dshg-empty {
        border: 1px dashed var(--dshg-border-strong);
        border-radius: 10px;
        padding: 15px 12px;
        display: flex;
        flex-direction: column;
        align-items: center;
        text-align: center;
        gap: 6px;
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
      search: '',
      skillSearch: '',
      loading: false,
      busyKey: '',
      error: '',
      notice: '',
      data: null,
      checkedAt: 0,
      editProviderKey: '',
      draftSecret: '',
      showAddKey: false,
      newKeyName: '',
      newKeySecret: '',
      expandedSnaps: {},
      mcpProbes: {},
      editMcpId: '',
      draftMcpTimeout: '',
    }

    const listeners = new Set()

    function notify() {
      for (const listener of listeners) {
        try {
          listener()
        } catch {
          // Ignore unmounted subscriber errors cleanly.
        }
      }
    }

    function patch(next) {
      Object.assign(store, next)
      notify()
    }

    function useStore() {
      const [, setTick] = React.useState(0)
      React.useEffect(() => {
        const listener = () => setTick((v) => v + 1)
        listeners.add(listener)
        return () => listeners.delete(listener)
      }, [])
      return store
    }

    async function readJsonResponse(res) {
      const text = await res.text()
      if (!text) return { json: null, rawText: '' }
      try {
        return { json: JSON.parse(text), rawText: text }
      } catch {
        return { json: null, rawText: text }
      }
    }

    function buildHttpErrorMessage(res, parsed) {
      if (parsed && parsed.json && (parsed.json.error || parsed.json.message)) {
        return String(parsed.json.error || parsed.json.message)
      }
      const snippet = parsed && parsed.rawText ? String(parsed.rawText).trim().slice(0, 120) : ''
      return snippet ? `HTTP ${res.status}: ${snippet}` : `HTTP ${res.status}`
    }

    async function fetchOverview(force = false) {
      if (store.loading && !force) return
      patch({ loading: true, error: '' })
      try {
        const res = await fetch(`${API_BASE}/overview`, { credentials: 'same-origin' })
        const parsed = await readJsonResponse(res)
        const json = parsed.json
        if (!res.ok || !json || !json.ok) {
          throw new Error(buildHttpErrorMessage(res, parsed))
        }
        patch({
          loading: false,
          data: json,
          checkedAt: json.checkedAt || Date.now(),
        })
      } catch (err) {
        patch({
          loading: false,
          error: err && err.message ? String(err.message) : 'Failed to load companion state',
        })
      }
    }

    async function postAction(endpoint, payload, busyId, successMsgBuilder) {
      patch({ busyKey: busyId, error: '', notice: '' })
      try {
        const res = await fetch(`${API_BASE}${endpoint}`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(payload || {}),
        })
        const parsed = await readJsonResponse(res)
        const json = parsed.json
        if (!res.ok || !json || json.ok === false) {
          throw new Error(buildHttpErrorMessage(res, parsed))
        }
        const msg = typeof successMsgBuilder === 'function' ? successMsgBuilder(json) : ''
        patch({ busyKey: '', notice: msg || '' })
        await fetchOverview(true)
        return json
      } catch (err) {
        patch({
          busyKey: '',
          error: err && err.message ? String(err.message) : 'Action failed',
        })
        return null
      }
    }

    async function launchGuardDesktop() {
      await postAction('/open', {}, 'open-desktop', (json) => json.message || '')
    }

    async function probeMcpsForProfile(targetProfile) {
      patch({ busyKey: 'probe-mcp', error: '', notice: '' })
      try {
        const res = await fetch(`${API_BASE}/mcp/probe`, {
          method: 'POST',
          credentials: 'same-origin',
          headers: { 'content-type': 'application/json' },
          body: JSON.stringify(targetProfile ? { target: targetProfile } : {}),
        })
        const parsed = await readJsonResponse(res)
        const json = parsed.json
        if (!res.ok || !json || json.ok === false) {
          throw new Error(buildHttpErrorMessage(res, parsed))
        }
        const nextMap = { ...store.mcpProbes }
        for (const r of json.results || []) {
          nextMap[`${r.profile}:${r.id}`] = r
        }
        patch({
          busyKey: '',
          mcpProbes: nextMap,
          notice: `MCP 探活完成：启用 ${json.activeCount || 0} 台 · 异常 ${json.unhealthyCount || 0} 台`,
        })
      } catch (err) {
        patch({
          busyKey: '',
          error: err && err.message ? String(err.message) : 'MCP probe failed',
        })
      }
    }

    function SvgIcon(props) {
      const size = props.size || 15
      return h(
        'svg',
        {
          width: size,
          height: size,
          viewBox: '0 0 24 24',
          fill: 'none',
          stroke: 'currentColor',
          strokeWidth: 2,
          strokeLinecap: 'round',
          strokeLinejoin: 'round',
          className: props.className || undefined,
          'aria-hidden': 'true',
        },
        props.children,
      )
    }

    function GuardIcon(props) {
      return h(
        SvgIcon,
        props,
        h('path', { d: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z' }),
        h('path', { d: 'm9 12 2 2 4-4' }),
      )
    }

    function RefreshIcon(props) {
      return h(
        SvgIcon,
        props,
        h('path', { d: 'M21 12a9 9 0 0 0-9-9 9.75 9.75 0 0 0-6.74 2.74L3 8' }),
        h('path', { d: 'M3 3v5h5' }),
        h('path', { d: 'M3 12a9 9 0 0 0 9 9 9.75 9.75 0 0 0 6.74-2.74L21 16' }),
        h('path', { d: 'M16 16h5v5' }),
      )
    }

    function SearchIcon(props) {
      return h(
        SvgIcon,
        props,
        h('circle', { cx: 11, cy: 11, r: 7 }),
        h('path', { d: 'm20 20-3.5-3.5' }),
      )
    }

    function ChevronIcon(props) {
      return h(
        SvgIcon,
        { size: props.size || 13 },
        h('path', { d: 'm6 9 6 6 6-6' }),
      )
    }

    function ExternalIcon(props) {
      return h(
        SvgIcon,
        props,
        h('path', { d: 'M15 3h6v6' }),
        h('path', { d: 'M10 14 21 3' }),
        h('path', { d: 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6' }),
      )
    }

    function CheckCircleIcon(props) {
      return h(
        SvgIcon,
        props,
        h('circle', { cx: 12, cy: 12, r: 9 }),
        h('path', { d: 'm9 12 2 2 4-4' }),
      )
    }

    function LockIcon(props) {
      return h(
        SvgIcon,
        props,
        h('rect', { x: 4, y: 11, width: 16, height: 10, rx: 2 }),
        h('path', { d: 'M7 11V7a5 5 0 0 1 10 0v4' }),
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
          return h(
            'div',
            { className: 'dshg-wrap' },
            h(
              'div',
              { className: 'dshg-error' },
              `DSH Companion Error: ${String(this.state.error.message || this.state.error)}`,
            ),
          )
        }
        return this.props.children
      }
    }

    function formatTimeShort(value) {
      if (!value) return '--'
      try {
        const d = typeof value === 'number' ? new Date(value) : new Date(String(value))
        if (Number.isNaN(d.getTime())) return String(value).slice(0, 16)
        const now = new Date()
        const sameDay =
          d.getFullYear() === now.getFullYear() &&
          d.getMonth() === now.getMonth() &&
          d.getDate() === now.getDate()
        const hh = String(d.getHours()).padStart(2, '0')
        const mm = String(d.getMinutes()).padStart(2, '0')
        if (sameDay) return `${hh}:${mm}`
        return `${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')} ${hh}:${mm}`
      } catch {
        return String(value).slice(0, 16)
      }
    }

    function BoundaryDisclosure(props) {
      const { summary, p1, p2, isWin, installerUrl, releaseUrl, t } = props
      return h(
        'details',
        { className: 'dshg-boundary' },
        h(
          'summary',
          null,
          h('span', null, summary),
          h(ChevronIcon, { size: 13 }),
        ),
        h(
          'div',
          { className: 'dshg-boundary-body' },
          h('div', null, p1),
          h('div', null, p2),
          isWin
            ? h(
                'div',
                { className: 'dshg-boundary-links' },
                h(
                  'a',
                  { href: installerUrl, target: '_blank', rel: 'noopener noreferrer' },
                  t.desktopWinLink,
                ),
                h(
                  'a',
                  { href: releaseUrl, target: '_blank', rel: 'noopener noreferrer' },
                  t.desktopReleaseLink,
                ),
              )
            : h('div', { style: { opacity: 0.85 } }, t.desktopNonWinNote),
        ),
      )
    }

    function ToggleSwitch(props) {
      const { on, disabled, busy, onToggle, title } = props
      return h(
        'button',
        {
          type: 'button',
          role: 'switch',
          'aria-checked': Boolean(on),
          'aria-label': title,
          className: 'dshg-switch',
          'data-on': Boolean(on) ? 'true' : 'false',
          disabled: Boolean(disabled || busy),
          onClick: onToggle,
        },
        h('span', { className: 'dshg-switch-thumb' }),
      )
    }

    function GuardPanelInner(props) {
      ensureStyles()
      const state = useStore()
      const t = COPY[state.lang] || COPY.zh

      React.useEffect(() => {
        if (!store.data && !store.loading) {
          fetchOverview(false)
        }
      }, [])

      const data = state.data
      const health = (data && data.health) || null
      const snapshots = (data && data.snapshots) || (health && health.snapshots) || { total: 0, snapshots: [] }
      const guard = (health && health.guard) || { installed: false }
      const isWin = ((data && data.platform) || (health && health.platform) || '') === 'win32'
      const installerUrl = (data && data.installerUrl) || INSTALLER_FALLBACK
      const releaseUrl = (data && data.releaseUrl) || RELEASE_FALLBACK
      const repoUrl = (data && data.repoUrl) || REPO_FALLBACK

      const activeProfile = (data && data.activeProfile) || (health && health.activeProfile) || 'web'
      const profileLabel = (name) => (name === 'desktop' ? t.desktopTrack : t.webTrack)

      const currentProf =
        (health && health.profiles && health.profiles[activeProfile]) ||
        (health && health.profile) || {
          exists: false,
          totalDependencies: 0,
          thirdPartyCount: 0,
          mcpCount: 0,
          disabledEntryCount: 0,
          brokenLinkCount: 0,
          brokenLinks: [],
        }
      const credentials = (health && health.credentials) || { present: false, count: 0, keys: [] }
      const providers = Array.isArray(health && health.providers) ? health.providers : []
      const issues = Array.isArray(health && health.issues) ? health.issues : []
      const healthy = health ? Boolean(health.healthy) : true

      const activeExtProfile = activeProfile
      const extTrack = (health && health.extensions && health.extensions[activeProfile]) || {
        plugins: [], mcpServers: [],
      }

      const query = state.search.trim().toLowerCase()
      const filteredMcps = extTrack.mcpServers.filter((m) =>
        !query ||
        String(m.serverName || '').toLowerCase().includes(query) ||
        String(m.id || '').toLowerCase().includes(query),
      )
      const filteredPlugins = extTrack.plugins.filter((p) =>
        !query ||
        String(p.name || '').toLowerCase().includes(query) ||
        String(p.primaryId || '').toLowerCase().includes(query),
      )

      const totalExtCount = (currentProf.thirdPartyCount || 0) + (currentProf.mcpCount || 0)
      const credsSummaryVal = `${credentials.count || 0} / ${providers.length}`
      const brokenLinks = Array.isArray(currentProf.brokenLinks) ? currentProf.brokenLinks : []
      const totalBrokenLinks = currentProf.brokenLinkCount || brokenLinks.length || 0

      const contextAudit = (health && health.contextAudit) || {
        profiles: [],
        skills: { total: 0, writableCount: 0, modelVisibleCount: 0, userInvocableCount: 0, skills: [] },
        recommendations: [],
      }
      const ctxProfiles = Array.isArray(contextAudit.profiles) ? contextAudit.profiles : []
      const activeCtxProfile =
        ctxProfiles.find((p) => p.profile === activeProfile) ||
        ctxProfiles[0] || {
          profile: activeProfile,
          level: 'light',
          estimatedBaseTokens: 0,
          enabledPlugins: 0,
          totalThirdPartyPlugins: 0,
          enabledMcps: 0,
          totalMcps: 0,
          modelVisibleSkills: 0,
          prompts: { files: [], alwaysInjectedBytes: 0, alwaysInjectedTokens: 0 },
        }
      const levelLabel = (lv) =>
        lv === 'heavy' ? t.contextLevelHeavy : lv === 'moderate' ? t.contextLevelModerate : t.contextLevelLight
      const levelTone = (lv) => (lv === 'heavy' ? 'warn' : lv === 'moderate' ? 'blue' : 'ok')
      const auditLog = Array.isArray(health && health.auditLog) ? health.auditLog : []

      const skillQuery = state.skillSearch.trim().toLowerCase()
      const allSkills = Array.isArray(contextAudit.skills && contextAudit.skills.skills) ? contextAudit.skills.skills : []
      const filteredSkills = allSkills.filter((sk) =>
        !skillQuery ||
        String(sk.name || '').toLowerCase().includes(skillQuery) ||
        String(sk.sourceLabel || '').toLowerCase().includes(skillQuery) ||
        String(sk.description || '').toLowerCase().includes(skillQuery),
      )

      const renderOverviewTab = () =>
        h(
          'div',
          { className: 'dshg-tab-body' },
          totalBrokenLinks > 0
            ? h(
                'div',
                { className: 'dshg-card', style: { borderColor: 'var(--dshg-warn-border)' } },
                h(
                  'div',
                  { className: 'dshg-sec-head' },
                  h('span', null, `${t.brokenLabel} (${totalBrokenLinks})`),
                  h(
                    'div',
                    { className: 'dshg-actions', style: { flexWrap: 'wrap' } },
                    h(
                      'button',
                      {
                        type: 'button',
                        className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                        disabled: Boolean(state.busyKey),
                        onClick: () =>
                          postAction(
                            '/repair-links',
                            { target: activeProfile },
                            `repair-${activeProfile}`,
                            (res) => (res && res.message) || `${profileLabel(activeProfile)} 断链处理完成：修复 ${res.repairedCount}，未解析 ${res.unresolvableCount}`,
                          ),
                      },
                      state.busyKey === `repair-${activeProfile}` ? t.repairingBtn : `${t.repairLinksBtn} (${profileLabel(activeProfile)})`,
                    ),
                  ),
                ),
                h(
                  'div',
                  { className: 'dshg-pills' },
                  ...brokenLinks.map((b, i) => h('span', { key: `${b.name}-${i}`, className: 'dshg-pill' }, b.name)),
                ),
              )
            : null,

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, `${t.secProfiles} · ${profileLabel(activeProfile)}`),
              h(
                'span',
                { className: 'dshg-badge', 'data-tone': currentProf.exists ? 'ok' : 'muted' },
                currentProf.exists ? t.present : t.absent,
              ),
            ),
            h('div', { className: 'dshg-row' }, h('span', { style: { color: 'var(--dshg-text-3)' } }, t.depsLabel), h('span', { className: 'dshg-mono' }, String(currentProf.totalDependencies || 0))),
            h('div', { className: 'dshg-row' }, h('span', { style: { color: 'var(--dshg-text-3)' } }, t.thirdPartyLabel), h('span', { className: 'dshg-mono' }, String(currentProf.thirdPartyCount || 0))),
            h('div', { className: 'dshg-row' }, h('span', { style: { color: 'var(--dshg-text-3)' } }, t.mcpLabel), h('span', { className: 'dshg-mono' }, String(currentProf.mcpCount || 0))),
            h('div', { className: 'dshg-row' }, h('span', { style: { color: 'var(--dshg-text-3)' } }, t.disabledLabel), h('span', { className: 'dshg-mono' }, String(currentProf.disabledEntryCount || 0))),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, `${t.contextCardTitle} (${profileLabel(activeCtxProfile.profile)})`),
              h(
                'span',
                { className: 'dshg-badge', 'data-tone': levelTone(activeCtxProfile.level) },
                `${levelLabel(activeCtxProfile.level)} · ${t.contextTokens(activeCtxProfile.estimatedBaseTokens || 0)}`,
              ),
            ),
            h(
              'div',
              { className: 'dshg-sec-sub' },
              t.contextStatsLine(
                activeCtxProfile.enabledPlugins || 0,
                activeCtxProfile.enabledMcps || 0,
                activeCtxProfile.modelVisibleSkills || 0,
              ),
            ),
            h(
              'div',
              { className: 'dshg-actions' },
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-btn dshg-btn-sm',
                  onClick: () => patch({ tab: 'context' }),
                },
                t.quickGoContext,
              ),
            ),
          ),

          issues.length > 0
            ? h(
                'div',
                { className: 'dshg-card' },
                h('div', { className: 'dshg-sec-head' }, h('span', null, t.warningsTitle)),
                h(
                  'div',
                  { className: 'dshg-list' },
                  ...issues.map((msg, idx) =>
                    h('div', { key: idx, className: 'dshg-item', style: { fontSize: 11.5 } }, msg),
                  ),
                ),
              )
            : h(
                'div',
                { className: 'dshg-card' },
                h(
                  'div',
                  { className: 'dshg-item-row' },
                  h(
                    'div',
                    { className: 'dshg-item-main' },
                    h(
                      'div',
                      { className: 'dshg-item-title' },
                      h(CheckCircleIcon, { size: 14, className: undefined }),
                      h('span', null, t.allHealthyTitle),
                    ),
                    h('div', { className: 'dshg-sec-sub' }, t.allHealthySub),
                  ),
                ),
                h(
                  'div',
                  { className: 'dshg-actions', style: { flexWrap: 'wrap' } },
                  h(
                    'button',
                    {
                      type: 'button',
                      className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                      disabled: Boolean(state.busyKey),
                      onClick: () =>
                        postAction(
                          '/snapshots/create',
                          { target: activeProfile, reason: `${activeProfile} 总览快捷备份` },
                          'quick-snap',
                          (res) => `已创建快照 ${res.snapshot.id}`,
                        ),
                    },
                    state.busyKey === 'quick-snap' ? t.snapCreating : t.quickSnapWeb,
                  ),
                  h(
                    'button',
                    {
                      type: 'button',
                      className: 'dshg-btn dshg-btn-sm',
                      onClick: () => patch({ tab: 'plugins' }),
                    },
                    t.quickGoPlugins,
                  ),
                ),
              ),
        )

      const renderPluginsTab = () =>
        h(
          'div',
          { className: 'dshg-tab-body' },
          h(
            'div',
            { className: 'dshg-toolbar' },
            h(
              'div',
              { className: 'dshg-search-wrap' },
              h('span', { className: 'dshg-search-ico' }, h(SearchIcon, { size: 13 })),
              h('input', {
                type: 'search',
                className: 'dshg-input with-ico',
                value: state.search,
                placeholder: t.searchPlaceholder,
                onChange: (e) => patch({ search: e.target.value }),
              }),
            ),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, `${t.mcpSectionTitle} (${filteredMcps.length})`),
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-btn dshg-btn-sm',
                  disabled: Boolean(state.busyKey || filteredMcps.length === 0),
                  onClick: () => probeMcpsForProfile(activeExtProfile),
                },
                state.busyKey === 'probe-mcp' ? t.mcpProbingBtn : t.mcpProbeBtn,
              ),
            ),
            filteredMcps.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...filteredMcps.map((m) => {
                    const probe = state.mcpProbes[`${activeExtProfile}:${m.id}`] || null
                    const probeTone = probe
                      ? probe.status === 'disabled' || probe.status === 'remote_skipped'
                        ? 'muted'
                        : probe.healthy
                          ? 'ok'
                          : 'warn'
                      : null
                    const editingTimeout = state.editMcpId === m.id

                    return h(
                      'div',
                      { key: m.id, className: 'dshg-item' },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'div',
                          { className: 'dshg-item-main' },
                          h(
                            'div',
                            { className: 'dshg-item-title' },
                            h('span', null, m.serverName),
                            h('span', { className: 'dshg-pill' }, m.transport),
                            h('span', { className: 'dshg-pill' }, `${m.toolCallTimeoutMs || 60000}ms`),
                            probe
                              ? h(
                                  'span',
                                  { className: 'dshg-badge', 'data-tone': probeTone, title: probe.detail },
                                  probe.status === 'reachable'
                                    ? `${probe.latencyMs}ms OK`
                                    : probe.status,
                                )
                              : null,
                          ),
                          h('div', { className: 'dshg-mono' }, m.command || m.url || m.id),
                          probe && !probe.healthy
                            ? h('div', { style: { fontSize: 10.5, color: '#d97706' } }, probe.detail)
                            : null,
                        ),
                        h(
                          'div',
                          { className: 'dshg-actions' },
                          h(
                            'button',
                            {
                              type: 'button',
                              className: 'dshg-btn dshg-btn-sm',
                              onClick: () =>
                                patch({
                                  editMcpId: editingTimeout ? '' : m.id,
                                  draftMcpTimeout: String(m.toolCallTimeoutMs || 60000),
                                }),
                            },
                            editingTimeout ? t.cancelBtn : t.mcpTimeoutBtn,
                          ),
                          h(ToggleSwitch, {
                            on: m.enabled,
                            busy: state.busyKey === `toggle-${m.id}`,
                            title: m.serverName,
                            onToggle: () =>
                              postAction(
                                '/toggle-entry',
                                { target: activeExtProfile, id: m.id, disabled: m.enabled },
                                `toggle-${m.id}`,
                                (res) => res.restartHint,
                              ),
                          }),
                        ),
                      ),
                      editingTimeout
                        ? h(
                            'div',
                            { className: 'dshg-drawer' },
                            h('div', { className: 'dshg-sec-sub' }, t.mcpTimeoutHint),
                            h(
                              'div',
                              { className: 'dshg-drawer-row' },
                              h('input', {
                                type: 'number',
                                min: 5000,
                                max: 600000,
                                step: 5000,
                                className: 'dshg-input',
                                value: state.draftMcpTimeout,
                                onChange: (e) => patch({ draftMcpTimeout: e.target.value }),
                              }),
                              h(
                                'button',
                                {
                                  type: 'button',
                                  className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                                  disabled: Boolean(state.busyKey),
                                  onClick: async () => {
                                    const ms = Number(state.draftMcpTimeout)
                                    const res = await postAction(
                                      '/mcp/timeout',
                                      { target: activeExtProfile, id: m.id, timeoutMs: ms },
                                      `timeout-${m.id}`,
                                      (r) => `已将 ${m.serverName} 超时设为 ${r.toolCallTimeoutMs}ms（已留存快照）`,
                                    )
                                    if (res && res.ok) patch({ editMcpId: '' })
                                  },
                                },
                                t.mcpTimeoutSave,
                              ),
                            ),
                          )
                        : null,
                    )
                  }),
                )
              : h('div', { className: 'dshg-sec-sub' }, t.mcpEmpty),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, t.pluginsSectionTitle),
              h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.badgeAllCount(filteredPlugins.length, extTrack.plugins.length)),
            ),
            filteredPlugins.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...filteredPlugins.map((p) =>
                    h(
                      'div',
                      { key: p.name, className: 'dshg-item' },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'div',
                          { className: 'dshg-item-main' },
                          h(
                            'div',
                            { className: 'dshg-item-title' },
                            h('span', null, p.name),
                            p.isSelf
                              ? h('span', { className: 'dshg-badge', 'data-tone': 'blue' }, t.badgeSelf)
                              : p.isCore
                                ? h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.badgeCore)
                                : h('span', { className: 'dshg-badge', 'data-tone': p.enabled ? 'ok' : 'warn' }, p.enabled ? t.badgeEnabled : t.badgeDisabled),
                          ),
                          h('div', { className: 'dshg-mono' }, `${p.primaryId}${p.version ? ` · v${p.version}` : ''}`),
                        ),
                        h(ToggleSwitch, {
                          on: p.enabled,
                          disabled: !p.canToggle,
                          busy: state.busyKey === `toggle-${p.primaryId}`,
                          title: p.name,
                          onToggle: () =>
                            postAction(
                              '/toggle-entry',
                              { target: activeExtProfile, id: p.primaryId, disabled: p.enabled },
                              `toggle-${p.primaryId}`,
                              (res) => res.restartHint,
                            ),
                        }),
                      ),
                    ),
                  ),
                )
              : h('div', { className: 'dshg-sec-sub' }, t.pluginsEmpty),
          ),

          h(BoundaryDisclosure, {
            summary: t.boundaryPluginsSummary,
            p1: t.boundaryPluginsP1,
            p2: t.boundaryPluginsP2,
            isWin,
            installerUrl,
            releaseUrl,
            t,
          }),
        )

      const renderContextTab = () =>
        h(
          'div',
          { className: 'dshg-tab-body' },
          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, `${t.contextCardTitle} (${profileLabel(activeCtxProfile.profile)})`),
              h(
                'span',
                { className: 'dshg-badge', 'data-tone': levelTone(activeCtxProfile.level) },
                `${levelLabel(activeCtxProfile.level)} · ${t.contextTokens(activeCtxProfile.estimatedBaseTokens || 0)}`,
              ),
            ),
            h(
              'div',
              { className: 'dshg-sec-sub' },
              t.contextStatsLine(
                activeCtxProfile.enabledPlugins || 0,
                activeCtxProfile.enabledMcps || 0,
                activeCtxProfile.modelVisibleSkills || 0,
              ),
            ),
            h(
              'div',
              { className: 'dshg-list' },
              ...(contextAudit.recommendations || []).map((rec, idx) =>
                h('div', { key: idx, className: 'dshg-item', style: { fontSize: 11 } }, rec),
              ),
            ),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h('div', { className: 'dshg-sec-head' }, h('span', null, t.contextPromptsTitle)),
            h(
              'div',
              { className: 'dshg-list' },
              ...((activeCtxProfile.prompts && activeCtxProfile.prompts.files) || []).map((f) =>
                h(
                  'div',
                  { key: f.id, className: 'dshg-row' },
                  h('span', { className: 'dshg-mono' }, f.label),
                  h(
                    'span',
                    { className: 'dshg-badge', 'data-tone': f.exists ? 'muted' : 'ok' },
                    f.exists ? t.promptsBytes(f.bytes, f.estTokens) : t.absent,
                  ),
                ),
              ),
            ),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, t.contextSkillsTitle),
              h(
                'span',
                { className: 'dshg-badge', 'data-tone': 'blue' },
                t.badgeAllCount(contextAudit.skills?.modelVisibleCount || 0, contextAudit.skills?.total || 0),
              ),
            ),
            h(
              'div',
              { className: 'dshg-search-wrap' },
              h('span', { className: 'dshg-search-ico' }, h(SearchIcon, { size: 13 })),
              h('input', {
                type: 'search',
                className: 'dshg-input with-ico',
                value: state.skillSearch,
                placeholder: t.contextSkillSearch,
                onChange: (e) => patch({ skillSearch: e.target.value }),
              }),
            ),
            filteredSkills.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...filteredSkills.map((sk) =>
                    h(
                      'div',
                      { key: sk.id, className: 'dshg-item' },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'div',
                          { className: 'dshg-item-main' },
                          h(
                            'div',
                            { className: 'dshg-item-title' },
                            h('span', null, sk.name),
                            h('span', { className: 'dshg-pill' }, sk.sourceLabel),
                            h('span', { className: 'dshg-pill' }, `~${sk.estTokens} tok`),
                            !sk.writable
                              ? h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.skillReadOnly)
                              : null,
                          ),
                          sk.description ? h('div', { className: 'dshg-sec-sub' }, sk.description) : null,
                        ),
                      ),
                      sk.writable
                        ? h(
                            'div',
                            { className: 'dshg-actions', style: { justifyContent: 'space-between', marginTop: 2 } },
                            h(
                              'div',
                              { className: 'dshg-actions' },
                              h('span', { className: 'dshg-sec-sub' }, t.skillModelVis),
                              h(ToggleSwitch, {
                                on: sk.modelVisible,
                                busy: state.busyKey === `skill-${sk.id}`,
                                title: `${sk.name} model visibility`,
                                onToggle: () =>
                                  postAction(
                                    '/skills/toggle',
                                    { skillId: sk.id, modelVisible: !sk.modelVisible, fingerprint: sk.fingerprint },
                                    `skill-${sk.id}`,
                                    () => `${sk.name} 模型可见性已更新`,
                                  ),
                              }),
                            ),
                            h(
                              'div',
                              { className: 'dshg-actions' },
                              h('span', { className: 'dshg-sec-sub' }, t.skillCmdVis),
                              h(ToggleSwitch, {
                                on: sk.userInvocable,
                                busy: state.busyKey === `skill-${sk.id}`,
                                title: `${sk.name} command visibility`,
                                onToggle: () =>
                                  postAction(
                                    '/skills/toggle',
                                    { skillId: sk.id, userInvocable: !sk.userInvocable, fingerprint: sk.fingerprint },
                                    `skill-${sk.id}`,
                                    () => `${sk.name} 命令可见性已更新`,
                                  ),
                              }),
                            ),
                          )
                        : null,
                    ),
                  ),
                )
              : h('div', { className: 'dshg-sec-sub' }, t.contextSkillsEmpty),
          ),
        )

      const renderCredsTab = () =>
        h(
          'div',
          { className: 'dshg-tab-body' },
          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, t.providersTitle),
              h('span', { className: 'dshg-badge', 'data-tone': 'blue' }, String(providers.length)),
            ),
            providers.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...providers.map((prov) => {
                    const editingThis = Boolean(prov.apiKeyEnv && state.editProviderKey === prov.id)
                    return h(
                      'div',
                      { key: prov.id, className: 'dshg-item' },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'div',
                          { className: 'dshg-item-main' },
                          h(
                            'div',
                            { className: 'dshg-item-title' },
                            h('span', null, prov.displayName),
                            h('span', { className: 'dshg-pill' }, prov.api),
                            h('span', { className: 'dshg-pill' }, `${prov.models.length} ${t.modelsUnit}`),
                          ),
                          prov.baseURL ? h('div', { className: 'dshg-mono' }, prov.baseURL) : null,
                          h(
                            'div',
                            { className: 'dshg-pills', style: { marginTop: 2 } },
                            ...(prov.profiles || []).map((pf) => h('span', { key: pf, className: 'dshg-pill' }, pf)),
                            prov.apiKeyEnv
                              ? h('span', { className: 'dshg-badge', 'data-tone': prov.keyConfigured ? 'ok' : 'warn' }, `${prov.apiKeyEnv} · ${prov.keyConfigured ? t.keyReady : t.keyMissing}`)
                              : null,
                          ),
                        ),
                        prov.apiKeyEnv
                          ? h(
                              'button',
                              {
                                type: 'button',
                                className: 'dshg-btn dshg-btn-sm',
                                onClick: () =>
                                  patch({
                                    editProviderKey: editingThis ? '' : prov.id,
                                    draftSecret: '',
                                  }),
                              },
                              editingThis ? t.cancelBtn : prov.keyConfigured ? t.updateKeyBtn : t.setKeyBtn,
                            )
                          : null,
                      ),
                      editingThis
                        ? h(
                            'div',
                            { className: 'dshg-drawer' },
                            h(
                              'div',
                              { className: 'dshg-drawer-row' },
                              h('input', {
                                type: 'password',
                                className: 'dshg-input',
                                autoComplete: 'new-password',
                                value: state.draftSecret,
                                placeholder: t.keyPlaceholder,
                                onChange: (e) => patch({ draftSecret: e.target.value }),
                              }),
                              h(
                                'button',
                                {
                                  type: 'button',
                                  className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                                  disabled: Boolean(state.busyKey || !state.draftSecret.trim()),
                                  onClick: async () => {
                                    const secret = state.draftSecret
                                    patch({ draftSecret: '' })
                                    const res = await postAction(
                                      '/credential-key',
                                      { key: prov.apiKeyEnv, value: secret },
                                      `write-${prov.apiKeyEnv}`,
                                      (r) => `已将 ${r.key} 单向写入 .credentials.yaml（绝不回显）`,
                                    )
                                    if (res && res.ok) patch({ editProviderKey: '' })
                                  },
                                },
                                state.busyKey === `write-${prov.apiKeyEnv}` ? t.writingKeyBtn : t.writeKeyBtn,
                              ),
                            ),
                          )
                        : null,
                    )
                  }),
                )
              : h('div', { className: 'dshg-sec-sub' }, t.providersEmpty),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, t.credsAuditTitle),
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-btn dshg-btn-sm',
                  onClick: () => patch({ showAddKey: !state.showAddKey, newKeySecret: '' }),
                },
                state.showAddKey ? t.cancelBtn : t.addKeyToggle,
              ),
            ),
            h('div', { className: 'dshg-sec-sub' }, t.credsAuditSub),
            state.showAddKey
              ? h(
                  'div',
                  { className: 'dshg-drawer', style: { borderTop: 'none', paddingTop: 0 } },
                  h(
                    'select',
                    {
                      className: 'dshg-select',
                      value: '',
                      onChange: (e) => {
                        const val = e.target.value
                        if (val) patch({ newKeyName: val })
                      },
                    },
                    h('option', { value: '' }, t.presetSelectPlaceholder),
                    ...ENV_KEY_PRESETS.map((pr) =>
                      h('option', { key: pr.id, value: pr.envKey }, `${pr.label} (${pr.envKey})`),
                    ),
                  ),
                  h('input', {
                    type: 'text',
                    className: 'dshg-input',
                    value: state.newKeyName,
                    placeholder: t.envNameLabel,
                    onChange: (e) => patch({ newKeyName: e.target.value }),
                  }),
                  h(
                    'div',
                    { className: 'dshg-drawer-row' },
                    h('input', {
                      type: 'password',
                      className: 'dshg-input',
                      autoComplete: 'new-password',
                      value: state.newKeySecret,
                      placeholder: t.keyPlaceholder,
                      onChange: (e) => patch({ newKeySecret: e.target.value }),
                    }),
                    h(
                      'button',
                      {
                        type: 'button',
                        className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                        disabled: Boolean(state.busyKey || !state.newKeyName.trim() || !state.newKeySecret.trim()),
                        onClick: async () => {
                          const keyName = state.newKeyName.trim()
                          const secret = state.newKeySecret
                          patch({ newKeySecret: '' })
                          const res = await postAction(
                            '/credential-key',
                            { key: keyName, value: secret },
                            'write-custom-key',
                            (r) => `已将 ${r.key} 单向写入 .credentials.yaml（绝不回显）`,
                          )
                          if (res && res.ok) patch({ newKeyName: '', showAddKey: false })
                        },
                      },
                      state.busyKey === 'write-custom-key' ? t.writingKeyBtn : t.writeKeyBtn,
                    ),
                  ),
                )
              : null,
            credentials.keys && credentials.keys.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...credentials.keys.map((k) =>
                    h(
                      'div',
                      { key: k.key, className: 'dshg-item', style: { padding: '6px 9px' } },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'span',
                          {
                            className: 'dshg-mono',
                            style: {
                              color: 'var(--dshg-text)',
                              fontWeight: 500,
                              display: 'inline-flex',
                              alignItems: 'center',
                              gap: 5,
                            },
                          },
                          k.protected ? h(LockIcon, { size: 12 }) : null,
                          k.key,
                        ),
                        k.protected
                          ? h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, t.credProtected)
                          : h('span', { className: 'dshg-badge', 'data-tone': k.present ? 'ok' : 'blue' }, k.present ? t.credPresent : t.credEnvOnly),
                      ),
                    ),
                  ),
                )
              : h('div', { className: 'dshg-sec-sub' }, t.credsEmpty),
          ),

          h(BoundaryDisclosure, {
            summary: t.boundaryCredsSummary,
            p1: t.boundaryCredsP1,
            p2: t.boundaryCredsP2,
            isWin,
            installerUrl,
            releaseUrl,
            t,
          }),
        )

      const renderSnapshotsTab = () =>
        h(
          'div',
          { className: 'dshg-tab-body' },
          h(
            'div',
            { className: 'dshg-toolbar' },
            h(
              'button',
              {
                type: 'button',
                className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                disabled: Boolean(state.busyKey),
                onClick: () =>
                  postAction(
                    '/snapshots/create',
                    { target: activeProfile, reason: `${activeProfile} 手动备份` },
                    'snap-create',
                    (res) => `已创建快照 ${res.snapshot.id}`,
                  ),
              },
              state.busyKey === 'snap-create' ? t.snapCreating : t.snapCreateTargetBtn,
            ),
          ),

          h(
            'div',
            { className: 'dshg-card' },
            h(
              'div',
              { className: 'dshg-sec-head' },
              h('span', null, t.kpiSnaps),
              h('span', { className: 'dshg-badge', 'data-tone': 'muted' }, String(snapshots.total || 0)),
            ),
            snapshots.snapshots && snapshots.snapshots.length > 0
              ? h(
                  'div',
                  { className: 'dshg-list' },
                  ...snapshots.snapshots.map((s) => {
                    const expanded = Boolean(state.expandedSnaps[s.id])
                    const trackColor = s.target === 'desktop' ? '#f59e0b' : s.target === 'all' ? '#10b981' : '#3b82f6'
                    const fileStatusLabel = (st) =>
                      st === 'same' ? t.fileSame : st === 'modified' ? t.fileModified : st === 'missing_now' ? t.fileMissing : t.fileUnknown
                    const fileTone = (st) =>
                      st === 'same' ? 'ok' : st === 'modified' || st === 'missing_now' ? 'warn' : 'muted'

                    return h(
                      'div',
                      { key: s.id, className: 'dshg-item' },
                      h(
                        'div',
                        { className: 'dshg-item-row' },
                        h(
                          'div',
                          { className: 'dshg-item-main' },
                          h(
                            'div',
                            { className: 'dshg-item-title' },
                            h('span', { className: 'dshg-track-dot', style: { background: trackColor } }),
                            h('span', null, s.reason || s.id),
                            h('span', { className: 'dshg-pill' }, s.source === 'desktop' ? t.snapSourceDesktop : t.snapSourcePlugin),
                            s.pinned
                              ? h('span', { className: 'dshg-badge', 'data-tone': 'blue' }, s.pinLabel || t.snapPinnedBadge)
                              : null,
                          ),
                          h('div', { className: 'dshg-mono' }, `${s.target} · ${formatTimeShort(s.createdAt)} · ${s.itemCount} files`),
                        ),
                        h(
                          'span',
                          { className: 'dshg-badge', 'data-tone': s.changedCount > 0 ? 'warn' : 'ok' },
                          s.changedCount > 0 ? t.snapDiff(s.changedCount) : t.snapSame,
                        ),
                      ),
                      h(
                        'div',
                        { className: 'dshg-actions', style: { justifyContent: 'space-between', marginTop: 2, flexWrap: 'wrap' } },
                        h(
                          'div',
                          { className: 'dshg-actions' },
                          h(
                            'button',
                            {
                              type: 'button',
                              className: 'dshg-btn dshg-btn-sm',
                              onClick: () =>
                                patch({
                                  expandedSnaps: { ...state.expandedSnaps, [s.id]: !expanded },
                                }),
                            },
                            t.snapFilesToggle((s.files || []).length),
                          ),
                          s.source !== 'desktop'
                            ? h(
                                'button',
                                {
                                  type: 'button',
                                  className: 'dshg-btn dshg-btn-sm',
                                  disabled: Boolean(state.busyKey),
                                  onClick: () =>
                                    postAction(
                                      '/snapshots/pin',
                                      { snapshotId: s.id, pinned: !s.pinned, pinLabel: s.pinned ? '' : '基线置顶' },
                                      `pin-${s.id}`,
                                      (r) => (r.pinned ? '已置顶保护此快照' : '已取消快照置顶'),
                                    ),
                                },
                                s.pinned ? t.snapUnpinBtn : t.snapPinBtn,
                              )
                            : null,
                        ),
                        s.restorableCount > 0
                          ? h(
                              'button',
                              {
                                type: 'button',
                                className: 'dshg-btn dshg-btn-primary dshg-btn-sm',
                                disabled: Boolean(state.busyKey),
                                onClick: () =>
                                  postAction(
                                    '/snapshots/restore-config',
                                    { snapshotId: s.id },
                                    `restore-${s.id}`,
                                    (res) => `已恢复 ${res.restoredFiles.length} 个配置文件（恢复前已自动备份 ${res.preRestoreId}）`,
                                  ),
                              },
                              state.busyKey === `restore-${s.id}` ? t.snapRestoring : t.snapRestoreBtn,
                            )
                          : h('span', { className: 'dshg-sec-sub' }, t.snapDesktopOnlyTip),
                      ),
                      expanded
                        ? h(
                            'div',
                            { className: 'dshg-drawer' },
                            s.files && s.files.length > 0
                              ? s.files.map((f) =>
                                  h(
                                    'div',
                                    { key: f.relPath || f.name, className: 'dshg-row' },
                                    h('span', { className: 'dshg-mono' }, f.relPath || f.name),
                                    h('span', { className: 'dshg-badge', 'data-tone': fileTone(f.status) }, fileStatusLabel(f.status)),
                                  ),
                                )
                              : h('div', { className: 'dshg-sec-sub' }, t.snapNoFiles),
                          )
                        : null,
                    )
                  }),
                )
              : h(
                  'div',
                  { className: 'dshg-empty' },
                  h('div', { style: { fontWeight: 600, fontSize: 12 } }, t.snapshotsEmpty),
                  h('div', { style: { fontSize: 11, lineHeight: 1.5 } }, t.snapshotsEmptyDesc),
                ),
          ),

          h(
            'details',
            { className: 'dshg-boundary' },
            h(
              'summary',
              null,
              h('span', null, t.auditSectionSummary(auditLog.length)),
              h(ChevronIcon, { size: 13 }),
            ),
            h(
              'div',
              { className: 'dshg-boundary-body' },
              auditLog.length > 0
                ? auditLog.map((entry, idx) =>
                    h(
                      'div',
                      { key: `${entry.ts}-${idx}`, className: 'dshg-row' },
                      h('span', { className: 'dshg-mono' }, `[${formatTimeShort(entry.ts)}] ${entry.action} · ${entry.target}`),
                      h('span', { style: { fontSize: 10.5 } }, entry.summary),
                    ),
                  )
                : h('div', { className: 'dshg-sec-sub' }, t.auditEmpty),
            ),
          ),

          h(BoundaryDisclosure, {
            summary: t.boundarySnapsSummary,
            p1: t.boundarySnapsP1,
            p2: t.boundarySnapsP2,
            isWin,
            installerUrl,
            releaseUrl,
            t,
          }),
        )

      return h(
        'div',
        { className: 'dshg-wrap', 'data-variant': (props && props.variant) || 'dock' },
        // Clean Header (zero install-nudge CTA when EXE is missing)
        h(
          'div',
          { className: 'dshg-header' },
          h(
            'div',
            { className: 'dshg-brand' },
            h('div', { className: 'dshg-logo' }, h(GuardIcon, { size: 16 })),
            h(
              'div',
              { className: 'dshg-title-box' },
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
            guard.installed
              ? h(
                  'button',
                  {
                    type: 'button',
                    className: 'dshg-btn',
                    disabled: Boolean(state.busyKey),
                    onClick: launchGuardDesktop,
                  },
                  h(ExternalIcon, { size: 12 }),
                  h('span', null, state.busyKey === 'open-desktop' ? t.launchingBtn : t.openDesktopBtn),
                )
              : null,
            h(
              'button',
              {
                type: 'button',
                className: 'dshg-btn dshg-btn-icon',
                title: t.refresh,
                disabled: state.loading,
                onClick: () => fetchOverview(true),
              },
              h(RefreshIcon, { size: 13, className: state.loading ? 'dshg-spin' : undefined }),
            ),
            h(
              'button',
              {
                type: 'button',
                className: 'dshg-btn',
                onClick: () => patch({ lang: state.lang === 'zh' ? 'en' : 'zh' }),
              },
              t.langSwitch,
            ),
          ),
        ),

        state.error ? h('div', { className: 'dshg-error' }, state.error) : null,
        state.notice ? h('div', { className: 'dshg-notice' }, state.notice) : null,

        // Status row
        data
          ? h(
              'div',
              { className: 'dshg-row' },
              h(
                'div',
                { className: 'dshg-actions', style: { gap: 6 } },
                h(
                  'span',
                  { className: 'dshg-badge', 'data-tone': 'blue' },
                  h('span', { className: 'dshg-dot' }),
                  profileLabel(activeProfile),
                ),
                h(
                  'span',
                  { className: 'dshg-badge', 'data-tone': healthy ? 'ok' : 'warn' },
                  h('span', { className: 'dshg-dot' }),
                  healthy ? t.healthy : t.unhealthy(issues.length),
                ),
              ),
              state.checkedAt
                ? h('span', { style: { fontSize: 11, color: 'var(--dshg-text-3)' } }, t.checkedAt(formatTimeShort(state.checkedAt)))
                : null,
            )
          : null,

        // Interactive 4-KPI Strip
        data
          ? h(
              'div',
              { className: 'dshg-kpi-strip' },
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-kpi',
                  'data-active': state.tab === 'overview' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'overview' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiHealth),
                h('span', { className: 'dshg-kpi-val' }, healthy ? t.kpiHealthOk : t.kpiHealthWarn(issues.length)),
              ),
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-kpi',
                  'data-active': state.tab === 'plugins' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'plugins' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiExt),
                h('span', { className: 'dshg-kpi-val' }, String(totalExtCount)),
              ),
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-kpi',
                  'data-active': state.tab === 'creds' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'creds' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiCreds),
                h('span', { className: 'dshg-kpi-val' }, credsSummaryVal),
              ),
              h(
                'button',
                {
                  type: 'button',
                  className: 'dshg-kpi',
                  'data-active': state.tab === 'snapshots' ? 'true' : 'false',
                  onClick: () => patch({ tab: 'snapshots' }),
                },
                h('span', { className: 'dshg-kpi-label' }, t.kpiSnaps),
                h('span', { className: 'dshg-kpi-val' }, String(snapshots.total || 0)),
              ),
            )
          : null,

        // Segmented 5-Tab Bar
        data
          ? h(
              'div',
              { className: 'dshg-seg' },
              ...[
                ['overview', t.tabOverview],
                ['plugins', t.tabPlugins],
                ['context', t.tabContext],
                ['creds', t.tabCreds],
                ['snapshots', t.tabSnapshots],
              ].map(([id, label]) =>
                h(
                  'button',
                  {
                    key: id,
                    type: 'button',
                    className: 'dshg-seg-btn',
                    'data-active': state.tab === id ? 'true' : 'false',
                    onClick: () => patch({ tab: id }),
                  },
                  label,
                ),
              ),
            )
          : null,

        !data && state.loading
          ? h('div', { className: 'dshg-card', style: { fontSize: 12, color: 'var(--dshg-text-2)' } }, t.loading)
          : null,

        !data && !state.loading && state.error
          ? h(
              'div',
              { className: 'dshg-empty' },
              h('div', { style: { fontSize: 12 } }, state.error),
              h('button', { type: 'button', className: 'dshg-btn', onClick: () => fetchOverview(true) }, t.retry),
            )
          : null,

        data && state.tab === 'overview' ? renderOverviewTab() : null,
        data && state.tab === 'plugins' ? renderPluginsTab() : null,
        data && state.tab === 'context' ? renderContextTab() : null,
        data && state.tab === 'creds' ? renderCredsTab() : null,
        data && state.tab === 'snapshots' ? renderSnapshotsTab() : null,

        h(
          'div',
          { className: 'dshg-footer' },
          h('span', null, t.footerNote),
          h(
            'div',
            { style: { display: 'flex', gap: 10 } },
            h('a', { href: releaseUrl, target: '_blank', rel: 'noopener noreferrer' }, 'Releases'),
            h('a', { href: repoUrl, target: '_blank', rel: 'noopener noreferrer' }, t.footerHome),
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
