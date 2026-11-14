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
      dependencies: { '@gluztm/dsh-guard': '^2.4.0' },
      dsh: { bundles: { '@gluztm/dsh-guard': {} } },
    }),
  )
  writeFileSync(join(desktopDir, 'cordis.patch.yml'), '- id: "@gluztm/dsh-guard"\n')

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

  // 3. Verify Cordis apply(ctx) plugin wiring (tools, commands, webServer HTTP routes)
  const registeredTools = []
  const registeredCommands = []
  const registeredRoutes = []
  const disposerCallbacks = []
  let mockRejection = 0
  const runEffect = (fn) => {
    const cleanup = typeof fn === 'function' ? fn() : null
    if (typeof cleanup === 'function') disposerCallbacks.push(cleanup)
    return () => {}
  }
  const mockCtx = {
    get(svc) {
      if (svc === 'connection') {
        return { requestRejection: () => mockRejection }
      }
      return null
    },
    inject(deps, cb) {
      if (deps.includes('tools')) {
        cb({
          tools: { register: (def) => { registeredTools.push(def); return () => {} } },
          effect: runEffect,
        })
      }
      if (deps.includes('commands')) {
        cb({
          commands: { register: (cmd) => { registeredCommands.push(cmd); return () => {} } },
          effect: runEffect,
        })
      }
      if (deps.includes('webServer')) {
        cb({
          webServer: { register: (route) => { registeredRoutes.push(route); return () => {} } },
          effect: runEffect,
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
  assert.deepEqual(
    registeredRoutes.map((r) => r.path).sort(),
    ['/api/dsh-guard/open', '/api/dsh-guard/overview'],
  )

  function invokeMockHttp(routePath, method = 'GET') {
    return new Promise((resolve) => {
      const route = registeredRoutes.find((r) => r.path === routePath)
      assert.ok(route, `Route ${routePath} should be registered`)
      const headers = {}
      const res = {
        statusCode: 200,
        setHeader(k, v) { headers[String(k).toLowerCase()] = v },
        end(body = '') {
          resolve({ statusCode: res.statusCode, headers, body, json: body ? JSON.parse(body) : null })
        },
      }
      Promise.resolve(route.handler({ method, headers: {} }, res))
    })
  }

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

    // Verify HTTP API routes + anti-leak + connection rejection gate
    const httpOverview = await invokeMockHttp('/api/dsh-guard/overview', 'GET')
    assert.equal(httpOverview.statusCode, 200)
    assert.equal(httpOverview.json.ok, true)
    assert.equal(httpOverview.json.snapshots.total, 2)
    assert.equal(httpOverview.body.includes(secretToken), false, 'Secret leaked via HTTP overview!')
    assert.equal(httpOverview.body.includes('998877'), false, 'Secret suffix leaked via HTTP overview!')

    const httpOpenGet = await invokeMockHttp('/api/dsh-guard/open', 'GET')
    assert.equal(httpOpenGet.statusCode, 405)

    const httpOpenPost = await invokeMockHttp('/api/dsh-guard/open', 'POST')
    assert.equal(httpOpenPost.statusCode, 200)
    assert.equal(httpOpenPost.json.ok, true)

    mockRejection = 401
    const httpDenied = await invokeMockHttp('/api/dsh-guard/overview', 'GET')
    assert.equal(httpDenied.statusCode, 401)
    assert.equal(httpDenied.json.ok, false)
    mockRejection = 0
  } finally {
    if (prevDshHome === undefined) delete process.env.DSH_HOME
    else process.env.DSH_HOME = prevDshHome
    if (prevGuardHome === undefined) delete process.env.DSHGUARD_HOME
    else process.env.DSHGUARD_HOME = prevGuardHome
  }

  // 4. Verify Web UI client entry registration via ModuleLoader stub
  let loadedModule = null
  const hadWindow = Object.prototype.hasOwnProperty.call(globalThis, 'window')
  const prevWindow = globalThis.window
  globalThis.window = {
    __ModuleLoader__: {
      load(entry) { loadedModule = entry },
    },
  }
  try {
    await import('../lib/client.js')
    assert.ok(loadedModule, 'lib/client.js should register via __ModuleLoader__.load')
    assert.equal(loadedModule.id, '@gluztm/dsh-guard')
    const clientPlugin = loadedModule.factory(() => ({
      createElement: (...args) => args,
      useState: (init) => [init, () => {}],
      useEffect: () => {},
      Component: class {},
    }))
    assert.deepEqual(clientPlugin.inject, ['slots', 'sidebarRightTabs'])
    const clientTabs = []
    const clientSlots = []
    clientPlugin.apply({
      effect: (fn) => { const d = fn(); return typeof d === 'function' ? d : () => {} },
      get: (svc) => (svc === 'sidebarRightTabs' ? { register: (t) => { clientTabs.push(t); return () => {} } } : null),
      slots: {
        inject: (_name, cb) => cb(),
        register: (meta) => { clientSlots.push(meta); return () => {} },
      },
    })
    assert.deepEqual(clientTabs.map((t) => t.id), ['dsh-guard'])
    assert.deepEqual(
      clientSlots.map((s) => s.name).sort(),
      ['main', 'sidebar.panellist', 'sidebar.right.pane.tab'],
    )
  } finally {
    if (hadWindow) globalThis.window = prevWindow
    else delete globalThis.window
  }

  // 5. Verify CLI execution in subprocess
  const cliOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'status', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliJson = JSON.parse(cliOut)
  assert.equal(cliJson.issuesCount, report.issuesCount)
  assert.equal(cliOut.includes(secretToken), false, 'Secret leaked via CLI!')

  console.log('dsh-guard smoke test PASSED (all assertions + HTTP routes + Web client + anti-leak + CLI verified)')
} finally {
  rmSync(sandbox, { recursive: true, force: true })
}
