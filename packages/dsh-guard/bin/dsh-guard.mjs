#!/usr/bin/env node
import {
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  openGuard,
} from '../lib/inspector.mjs'

function parseArgs(argv) {
  const args = argv.slice(2)
  let command = 'status'
  let dshHome = null
  let guardHome = null
  let snapshotsDir = null
  let target = 'all'
  let limit = 20
  let launch = false
  let json = false
  let help = false

  for (let i = 0; i < args.length; i++) {
    const a = args[i]
    if (a === '--help' || a === '-h') help = true
    else if (a === '--json') json = true
    else if (a === '--launch') launch = true
    else if (a === '--dsh-home' && i + 1 < args.length) dshHome = args[++i]
    else if (a === '--guard-home' && i + 1 < args.length) guardHome = args[++i]
    else if (a === '--snapshots-dir' && i + 1 < args.length) snapshotsDir = args[++i]
    else if (a === '--target' && i + 1 < args.length) target = args[++i]
    else if (a === '--limit' && i + 1 < args.length) limit = Number(args[++i]) || 20
    else if (!a.startsWith('-')) command = a
  }

  return { command, dshHome, guardHome, snapshotsDir, target, limit, launch, json, help }
}

const opts = parseArgs(process.argv)

if (opts.help) {
  console.log([
    'dsh-guard — Companion read-only inspector & snapshot bridge for DSHGuard',
    '',
    'Usage:',
    '  dsh-guard status [--target all|web|desktop] [--dsh-home <dir>] [--guard-home <dir>] [--json]',
    '  dsh-guard snapshots [--target all|web|desktop] [--limit N] [--guard-home <dir>] [--json]',
    '  dsh-guard open [--launch] [--guard-home <dir>] [--json]',
  ].join('\n'))
  process.exit(0)
}

if (opts.command === 'snapshots') {
  const report = inspectSnapshots(opts)
  console.log(opts.json ? JSON.stringify(report, null, 2) : formatSnapshotsText(report))
  process.exit(0)
}

if (opts.command === 'open') {
  const res = openGuard(opts)
  console.log(opts.json ? JSON.stringify(res, null, 2) : res.message)
  process.exit(0)
}

const health = inspectHealth(opts)
console.log(opts.json ? JSON.stringify(health, null, 2) : formatHealthText(health))
process.exit(0)
