import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  apply,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
} from '../lib/index.mjs'

const __dirname = dirname(fileURLToPath(import.meta.url))
const pkgRoot = resolve(__dirname, '..')
const sandbox = mkdtempSync(join(tmpdir(), 'dsh-guard-smoke-'))

try {
  const dshHome = join(sandbox, '.dsh')
  const webDir = join(dshHome, 'profiles', 'web')
  const desktopDir = join(dshHome, 'profiles', 'desktop')
  const guardHome = join(sandbox, 'guard')
  const snapshotsDir = join(guardHome, 'Snapshots')

  mkdirSync(webDir, { recursive: true })
  mkdirSync(desktopDir, { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-web-20261114-120000'), { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-desktop-20261114-121000'), { recursive: true })

  // Fake DSHGuard.exe marker in isolated sandbox (never executed)
  writeFileSync(join(guardHome, 'DSHGuard.exe'), '')

  // Snapshot manifests
  writeFileSync(
    join(snapshotsDir, 'snap-web-20261114-120000', 'manifest.json'),
    JSON.stringify({ target: 'web', reason: 'before plugin update', createdAt: '2026-11-14T12:00:00Z', files: ['package.json', 'cordis.patch.yml'] }),
  )
  writeFileSync(
    join(snapshotsDir, 'snap-desktop-20261114-121000', 'manifest.json'),
    JSON.stringify({ target: 'desktop', reason: 'manual checkpoint', createdAt: '2026-11-14T12:10:00Z', files: ['package.json'] }),
  )

  // Web profile: deliberate drift ('dsh-broken-plugin' in bundles but missing from dependencies) + env key refs
  writeFileSync(
    join(webDir, 'package.json'),
    JSON.stringify({
      dependencies: { 'dsh-good-plugin': '^1.0.0' },
      dsh: { bundles: { 'dsh-good-plugin': {}, 'dsh-broken-plugin': {} } },
    }),
  )
  writeFileSync(
    join(webDir, 'cordis.patch.yml'),
    [
      '- id: llm-pi-ai',
      '  apiKeyEnv: DEEPSEEK_API_KEY',
      '- id: openai-extra',
      '  apiKeyEnv: OPENAI_API_KEY',
      '  enabled: false',
    ].join('\n'),
  )

  // Desktop profile: clean
  writeFileSync(
    join(desktopDir, 'package.json'),
    JSON.stringify({
      dependencies: { 'dsh-guard': '^2.4.0' },
      dsh: { bundles: { 'dsh-guard': {} } },
    }),
  )
  writeFileSync(join(desktopDir, 'cordis.patch.yml'), '- id: dsh-guard\n')

  // Fake secret fixture (must NEVER leak in any output!)
  const secretToken = 'sk-TOP-SECRET-NEVER-LEAK-998877'
  writeFileSync(
    join(dshHome, '.credentials.yaml'),
    `DEEPSEEK_API_KEY: "${secretToken}"\nEMPTY_KEY: ""\n`,
  )

  // 1. Inspect health & security anti-leak assertions
  const report = inspectHealth({ dshHome, guardHome })
  assert.equal(report.healthy, false, 'Expected unhealthy due to bundle drift and missing OPENAI_API_KEY')
  assert.ok(
    report.issues.some((x) => x.includes('dsh-broken-plugin')),
    'Should detect bundle missing from dependencies',
  )
  assert.deepEqual(report.credentials.missingReferencedKeys, ['OPENAI_API_KEY'])
  assert.equal(report.credentials.nonEmptyKeysCount, 1)

  const reportJson = JSON.stringify(report)
  const reportText = formatHealthText(report)
  assert.equal(reportJson.includes(secretToken), false, 'Secret token leaked into JSON output!')
  assert.equal(reportJson.includes('998877'), false, 'Secret suffix leaked into JSON output!')
  assert.equal(reportText.includes(secretToken), false, 'Secret token leaked into text output!')
  assert.equal(report.guard.installed, true)

  // 2. Inspect snapshots
  const snapReport = inspectSnapshots({ guardHome, target: 'all', limit: 10 })
  assert.equal(snapReport.total, 2)
  assert.equal(snapReport.snapshots[0].id, 'snap-desktop-20261114-121000')
  assert.ok(formatSnapshotsText(snapReport).includes('snap-web-20261114-120000'))

  // 3. Verify Cordis apply(ctx) plugin wiring
  const registeredTools = []
  const registeredCommands = []
  const disposerCallbacks = []
  const mockCtx = {
    inject(deps, cb) {
      if (deps.includes('tools')) {
        cb({
          tools: { register: (def) => { registeredTools.push(def); return () => {} } },
          effect: (fn) => disposerCallbacks.push(fn),
        })
      }
      if (deps.includes('commands')) {
        cb({
          commands: { register: (cmd) => registeredCommands.push(cmd) },
        })
      }
    },
  }
  apply(mockCtx)
  assert.deepEqual(
    registeredTools.map((t) => t.name).sort(),
    ['dsh_guard_open', 'dsh_guard_snapshots', 'dsh_guard_status'],
  )
  assert.deepEqual(
    registeredCommands.map((c) => c.name),
    ['guard'],
  )
  const prevDshHome = process.env.DSH_HOME
  const prevGuardHome = process.env.DSHGUARD_HOME
  process.env.DSH_HOME = dshHome
  process.env.DSHGUARD_HOME = guardHome
  try {
    const toolStatus = await registeredTools.find((t) => t.name === 'dsh_guard_status').execute({})
    assert.equal(toolStatus.healthy, false)
    assert.equal(toolStatus.summary.includes(secretToken), false)
    const toolSnaps = await registeredTools.find((t) => t.name === 'dsh_guard_snapshots').execute({ limit: 5 })
    assert.equal(toolSnaps.total, 2)
    const toolOpen = await registeredTools.find((t) => t.name === 'dsh_guard_open').execute({ launch: false })
    assert.equal(toolOpen.installed, true)
    assert.equal(toolOpen.launched, false)

    const cmdStatus = await registeredCommands[0].handler({ rawInput: 'status' })
    assert.equal(cmdStatus.kind, 'success')
    const cmdSnaps = await registeredCommands[0].handler({ rawInput: 'snapshots' })
    assert.equal(cmdSnaps.kind, 'success')
    assert.ok(cmdSnaps.text.includes('snap-web-20261114-120000'))
  } finally {
    if (prevDshHome === undefined) delete process.env.DSH_HOME
    else process.env.DSH_HOME = prevDshHome
    if (prevGuardHome === undefined) delete process.env.DSHGUARD_HOME
    else process.env.DSHGUARD_HOME = prevGuardHome
  }

  // 4. Verify CLI execution in subprocess
  const cliOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'status', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliJson = JSON.parse(cliOut)
  assert.equal(cliJson.issuesCount, report.issuesCount)
  assert.equal(cliOut.includes(secretToken), false, 'Secret leaked via CLI!')

  console.log('dsh-guard smoke test PASSED (all assertions + anti-leak + CLI verified)')
} finally {
  rmSync(sandbox, { recursive: true, force: true })
}
