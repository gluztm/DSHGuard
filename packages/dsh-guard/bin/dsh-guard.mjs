#!/usr/bin/env node
import {
  PACKAGE_VERSION,
  applyScene,
  createSnapshot,
  detectActiveProfile,
  executeStorageCleanup,
  formatContextAuditText,
  formatHealthText,
  formatLogRadarText,
  formatMcpProbeText,
  formatProfileDiffText,
  formatScenesText,
  formatSnapshotsText,
  formatStorageHygieneText,
  inspectContextAudit,
  inspectHealth,
  inspectLogRadar,
  inspectProfileDiff,
  inspectSnapshots,
  inspectStorageHygiene,
  listScenes,
  normalizeProfileTarget,
  openGuard,
  previewSceneApply,
  probeMcpServers,
  readAuditLog,
  repairBrokenModuleLinks,
  resolveDshHome,
  restoreRecycleBatch,
  syncProfileSections,
} from '../lib/inspector.mjs'

function parseArgs(argv) {
  const opts = {
    command: 'status',
    json: false,
    launch: false,
    dryRun: false,
    sync: false,
    recycle: false,
    target: '',
    diffSource: '',
    diffTarget: '',
    applySceneId: '',
    previewSceneId: '',
    restoreBatchId: '',
    buckets: [],
    sections: [],
    hours: 168,
    minAgeDays: 0,
    limit: 20,
    dshHome: undefined,
    guardHome: undefined,
    help: false,
  }
  const positionals = []
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    if (arg === '--json') {
      opts.json = true
    } else if (arg === '--launch') {
      opts.launch = true
    } else if (arg === '--dry-run') {
      opts.dryRun = true
    } else if (arg === '--sync') {
      opts.sync = true
    } else if (arg === '--recycle') {
      opts.recycle = true
    } else if (arg === '-h' || arg === '--help') {
      opts.help = true
    } else if (arg === '--target' && i + 1 < argv.length) {
      opts.target = argv[++i]
    } else if (arg.startsWith('--target=')) {
      opts.target = arg.slice('--target='.length)
    } else if (arg === '--limit' && i + 1 < argv.length) {
      opts.limit = Number(argv[++i]) || 20
    } else if (arg.startsWith('--limit=')) {
      opts.limit = Number(arg.slice('--limit='.length)) || 20
    } else if (arg === '--hours' && i + 1 < argv.length) {
      opts.hours = Number(argv[++i]) || 168
    } else if (arg.startsWith('--hours=')) {
      opts.hours = Number(arg.slice('--hours='.length)) || 168
    } else if (arg === '--min-age' && i + 1 < argv.length) {
      opts.minAgeDays = Number(argv[++i]) || 0
    } else if (arg.startsWith('--min-age=')) {
      opts.minAgeDays = Number(arg.slice('--min-age='.length)) || 0
    } else if (arg === '--apply' && i + 1 < argv.length) {
      opts.applySceneId = argv[++i]
    } else if (arg.startsWith('--apply=')) {
      opts.applySceneId = arg.slice('--apply='.length)
    } else if (arg === '--preview' && i + 1 < argv.length) {
      opts.previewSceneId = argv[++i]
    } else if (arg.startsWith('--preview=')) {
      opts.previewSceneId = arg.slice('--preview='.length)
    } else if (arg === '--restore' && i + 1 < argv.length) {
      opts.restoreBatchId = argv[++i]
    } else if (arg.startsWith('--restore=')) {
      opts.restoreBatchId = arg.slice('--restore='.length)
    } else if (arg === '--buckets' && i + 1 < argv.length) {
      opts.buckets = argv[++i].split(',').map((s) => s.trim()).filter(Boolean)
    } else if (arg.startsWith('--buckets=')) {
      opts.buckets = arg.slice('--buckets='.length).split(',').map((s) => s.trim()).filter(Boolean)
    } else if (arg === '--sections' && i + 1 < argv.length) {
      opts.sections = argv[++i].split(',').map((s) => s.trim()).filter(Boolean)
    } else if (arg.startsWith('--sections=')) {
      opts.sections = arg.slice('--sections='.length).split(',').map((s) => s.trim()).filter(Boolean)
    } else if (arg === '--dsh-home' && i + 1 < argv.length) {
      opts.dshHome = argv[++i]
    } else if (arg.startsWith('--dsh-home=')) {
      opts.dshHome = arg.slice('--dsh-home='.length)
    } else if (arg === '--guard-home' && i + 1 < argv.length) {
      opts.guardHome = argv[++i]
    } else if (arg.startsWith('--guard-home=')) {
      opts.guardHome = arg.slice('--guard-home='.length)
    } else if (!arg.startsWith('-')) {
      positionals.push(arg)
    }
  }
  if (positionals.length > 0) {
    opts.command = positionals[0].toLowerCase()
  }
  if (
    positionals.length > 1 &&
    (opts.command === 'backup' ||
      opts.command === 'repair' ||
      opts.command === 'context' ||
      opts.command === 'mcp-probe' ||
      opts.command === 'scenes')
  ) {
    opts.target = positionals[1]
  }
  if (opts.command === 'diff') {
    if (positionals[1]) opts.diffSource = positionals[1]
    if (positionals[2]) opts.diffTarget = positionals[2]
  }
  return opts
}

function resolveCliTarget(rawTarget, dshHome) {
  const explicit = normalizeProfileTarget(rawTarget)
  if (explicit) return explicit
  return detectActiveProfile({ dshHome, preferExistingOnCli: true })
}

function printHelp() {
  console.log(
    [
      `dsh-guard v${PACKAGE_VERSION} — DSH 单端自动感知伴生管理工具`,
      '',
      '用法:',
      '  dsh-guard status [--target desktop|web] [--dsh-home PATH] [--json]',
      '  dsh-guard context [--target desktop|web] [--dsh-home PATH] [--json]',
      '  dsh-guard mcp-probe [--target desktop|web] [--dsh-home PATH] [--json]',
      '  dsh-guard scenes [--target desktop|web] [--preview ID | --apply ID] [--dry-run] [--json]',
      '  dsh-guard diff [desktop|web] [web|desktop] [--sections s1,s2] [--sync] [--dry-run] [--json]',
      '  dsh-guard storage [--recycle --buckets cache,profile-bak --min-age N | --restore ID] [--dry-run] [--json]',
      '  dsh-guard logs [--hours N] [--limit N] [--dsh-home PATH] [--json]',
      '  dsh-guard snapshots [--target desktop|web] [--limit N] [--json]',
      '  dsh-guard backup [desktop|web] [--dsh-home PATH] [--json]',
      '  dsh-guard repair [desktop|web] [--dsh-home PATH] [--json]',
      '  dsh-guard audit [--limit N] [--dsh-home PATH] [--json]',
      '  dsh-guard open [--launch] [--json]',
    ].join('\n'),
  )
}

async function main() {
  const opts = parseArgs(process.argv.slice(2))
  if (opts.help || opts.command === 'help') {
    printHelp()
    return
  }

  const dshHome = resolveDshHome(opts.dshHome)
  const activeTarget = resolveCliTarget(opts.target, dshHome)

  if (opts.command === 'snapshots' || opts.command === 'snap') {
    const report = inspectSnapshots({
      dshHome,
      guardHome: opts.guardHome,
      target: activeTarget,
      limit: opts.limit,
    })
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatSnapshotsText(report))
    return
  }

  if (opts.command === 'context' || opts.command === 'ctx') {
    const health = inspectHealth({ dshHome, guardHome: opts.guardHome, activeProfile: activeTarget })
    const report = inspectContextAudit(dshHome, health, activeTarget)
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatContextAuditText(report))
    return
  }

  if (opts.command === 'mcp-probe' || opts.command === 'mcp') {
    const health = inspectHealth({ dshHome, guardHome: opts.guardHome, activeProfile: activeTarget })
    const report = await probeMcpServers(dshHome, health.extensions, activeTarget)
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatMcpProbeText(report))
    return
  }

  if (opts.command === 'scenes' || opts.command === 'scene') {
    if (opts.previewSceneId) {
      const res = previewSceneApply(dshHome, opts.previewSceneId, { target: activeTarget })
      console.log(
        opts.json
          ? JSON.stringify(res, null, 2)
          : res.ok
            ? `场景「${res.scene.name}」Dry-Run (${res.target}): 插件 ${res.pluginChanges.length}，MCP ${res.mcpChanges.length}，技能 ${res.skillChanges.length}`
            : res.error,
      )
      if (!res.ok) process.exitCode = 1
      return
    }
    if (opts.applySceneId) {
      const res = applyScene(dshHome, opts.applySceneId, { target: activeTarget, dryRun: opts.dryRun })
      console.log(opts.json ? JSON.stringify(res, null, 2) : res.ok ? (res.restartHint || res.scene.name) : res.error)
      if (!res.ok) process.exitCode = 1
      return
    }
    const report = listScenes(dshHome, { target: activeTarget })
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatScenesText(report))
    return
  }

  if (opts.command === 'diff') {
    const source = normalizeProfileTarget(opts.diffSource) || activeTarget
    const target = normalizeProfileTarget(opts.diffTarget) || (source === 'desktop' ? 'web' : 'desktop')
    const sections = opts.sections.length > 0 ? opts.sections : undefined
    if (opts.sync) {
      const res = syncProfileSections(dshHome, { source, target, sections, dryRun: opts.dryRun })
      console.log(opts.json ? JSON.stringify(res, null, 2) : res.ok ? (res.restartHint || `Dry-Run 预计变更 ${res.changeCount} 项`) : res.error)
      if (!res.ok) process.exitCode = 1
      return
    }
    const report = inspectProfileDiff(dshHome, { source, target, sections })
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatProfileDiffText(report))
    if (!report.ok) process.exitCode = 1
    return
  }

  if (opts.command === 'storage' || opts.command === 'disk') {
    if (opts.restoreBatchId) {
      const res = restoreRecycleBatch(dshHome, opts.restoreBatchId)
      console.log(opts.json ? JSON.stringify(res, null, 2) : res.ok ? res.message : res.error)
      if (!res.ok) process.exitCode = 1
      return
    }
    if (opts.recycle || opts.dryRun) {
      const res = executeStorageCleanup(dshHome, {
        buckets: opts.buckets.length > 0 ? opts.buckets : undefined,
        minAgeDays: opts.minAgeDays,
        dryRun: opts.dryRun,
      })
      console.log(
        opts.json
          ? JSON.stringify(res, null, 2)
          : res.ok
            ? (res.dryRun ? `Dry-Run 预计清理 ${res.itemCount} 文件 (${res.humanTotalSize})` : res.message)
            : res.error,
      )
      if (!res.ok) process.exitCode = 1
      return
    }
    const report = inspectStorageHygiene(dshHome)
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatStorageHygieneText(report))
    return
  }

  if (opts.command === 'logs' || opts.command === 'radar') {
    const report = inspectLogRadar(dshHome, { hours: opts.hours, maxGroups: opts.limit })
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatLogRadarText(report))
    return
  }

  if (opts.command === 'audit') {
    const records = readAuditLog(dshHome, opts.limit)
    if (opts.json) {
      console.log(JSON.stringify({ total: records.length, records }, null, 2))
    } else if (records.length === 0) {
      console.log('暂无变更审计记录。')
    } else {
      for (const r of records) {
        console.log(`[${r.ts}] (${r.action}/${r.target}) ${r.summary}${r.snapshotId ? ` [snap=${r.snapshotId}]` : ''}`)
      }
    }
    return
  }

  if (opts.command === 'backup') {
    const res = createSnapshot(dshHome, activeTarget, 'manual', 'CLI dsh-guard backup')
    if (opts.json) {
      console.log(JSON.stringify(res, null, 2))
    } else {
      console.log(`已创建配置快照 ${res.snapshot.id}（${res.snapshot.target} · ${res.snapshot.itemCount} 个文件）`)
    }
    return
  }

  if (opts.command === 'repair') {
    const res = repairBrokenModuleLinks(dshHome, activeTarget)
    if (opts.json) {
      console.log(JSON.stringify(res, null, 2))
    } else {
      console.log(res.message || `断链修复 (${res.target}): 已修复 ${res.repairedCount}，未解析 ${res.unresolvableCount}`)
    }
    return
  }

  if (opts.command === 'open') {
    const res = openGuard({ guardHome: opts.guardHome, launch: opts.launch })
    console.log(opts.json ? JSON.stringify(res, null, 2) : res.message)
    return
  }

  const health = inspectHealth({ dshHome, guardHome: opts.guardHome, activeProfile: activeTarget })
  console.log(opts.json ? JSON.stringify(health, null, 2) : formatHealthText(health))
}

main().catch((err) => {
  console.error(err instanceof Error ? err.message : String(err))
  process.exitCode = 1
})
