#!/usr/bin/env node
import {
  PACKAGE_VERSION,
  createSnapshot,
  detectActiveProfile,
  formatContextAuditText,
  formatHealthText,
  formatMcpProbeText,
  formatSnapshotsText,
  inspectContextAudit,
  inspectHealth,
  inspectSnapshots,
  normalizeProfileTarget,
  openGuard,
  probeMcpServers,
  readAuditLog,
  repairBrokenModuleLinks,
  resolveDshHome,
} from '../lib/inspector.mjs'

function parseArgs(argv) {
  const opts = {
    command: 'status',
    json: false,
    launch: false,
    target: '',
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
    (opts.command === 'backup' || opts.command === 'repair' || opts.command === 'context' || opts.command === 'mcp-probe')
  ) {
    opts.target = positionals[1]
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
