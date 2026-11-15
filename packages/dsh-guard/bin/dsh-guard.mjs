#!/usr/bin/env node
import {
  PACKAGE_VERSION,
  createSnapshot,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  openGuard,
  repairBrokenModuleLinks,
  resolveDshHome,
} from '../lib/inspector.mjs'

function parseArgs(argv) {
  const opts = {
    command: 'status',
    json: false,
    launch: false,
    target: 'all',
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
  if (positionals.length > 1 && (opts.command === 'backup' || opts.command === 'repair')) {
    opts.target = positionals[1].toLowerCase()
  }
  return opts
}

function printHelp() {
  console.log(
    [
      `dsh-guard v${PACKAGE_VERSION} — DSH 伴生管理工具`,
      '',
      '用法:',
      '  dsh-guard status [--target all|web|desktop] [--dsh-home PATH] [--json]',
      '  dsh-guard snapshots [--target all|web|desktop] [--limit N] [--json]',
      '  dsh-guard backup [web|desktop|all] [--dsh-home PATH] [--json]',
      '  dsh-guard repair [web|desktop] [--dsh-home PATH] [--json]',
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

  if (opts.command === 'snapshots' || opts.command === 'snap') {
    const report = inspectSnapshots({
      dshHome: opts.dshHome,
      guardHome: opts.guardHome,
      target: opts.target,
      limit: opts.limit,
    })
    console.log(opts.json ? JSON.stringify(report, null, 2) : formatSnapshotsText(report))
    return
  }

  if (opts.command === 'backup') {
    const t = opts.target === 'desktop' || opts.target === 'all' ? opts.target : 'web'
    const res = createSnapshot(resolveDshHome(opts.dshHome), t, 'manual', 'CLI dsh-guard backup')
    if (opts.json) {
      console.log(JSON.stringify(res, null, 2))
    } else {
      console.log(`已创建配置快照 ${res.snapshot.id}（${res.snapshot.target} · ${res.snapshot.itemCount} 个文件）`)
    }
    return
  }

  if (opts.command === 'repair') {
    const t = opts.target === 'desktop' ? 'desktop' : 'web'
    const res = repairBrokenModuleLinks(resolveDshHome(opts.dshHome), t)
    if (opts.json) {
      console.log(JSON.stringify(res, null, 2))
    } else {
      console.log(`断链修复 (${res.target}): 已修复 ${res.repairedCount}，未解析 ${res.unresolvableCount}`)
    }
    return
  }

  if (opts.command === 'open') {
    const res = openGuard({ guardHome: opts.guardHome, launch: opts.launch })
    console.log(opts.json ? JSON.stringify(res, null, 2) : res.message)
    return
  }

  const health = inspectHealth({ dshHome: opts.dshHome, guardHome: opts.guardHome, target: opts.target })
  console.log(opts.json ? JSON.stringify(health, null, 2) : formatHealthText(health))
}

main().catch((err) => {
  console.error(err instanceof Error ? err.message : String(err))
  process.exitCode = 1
})
