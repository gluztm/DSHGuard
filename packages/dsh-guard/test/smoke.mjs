import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  apply,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  openGuard,
  repairBrokenModuleLinks,
  togglePatchEntry,
  upsertCredentialKey,
} from '../lib/index.mjs'

const __dirname = dirname(fileURLToPath(import.meta.url))
const pkgRoot = resolve(__dirname, '..')
const sandbox = mkdtempSync(join(tmpdir(), 'dsh-guard-smoke-'))

try {
  const dshHome = join(sandbox, '.dsh')
  const webDir = join(dshHome, 'profiles', 'web')
  const desktopDir = join(dshHome, 'profiles', 'desktop')
  const fallbackPkgDir = join(dshHome, '.dsh-module-fallback', 'node_modules', 'broken-helper')
  const guardHome = join(sandbox, 'guard')
  const snapshotsDir = join(guardHome, 'Snapshots')

  mkdirSync(join(webDir, 'node_modules', 'dsh-good-plugin'), { recursive: true })
  mkdirSync(join(webDir, 'node_modules', '@scope', 'community-plugin'), { recursive: true })
  mkdirSync(desktopDir, { recursive: true })
  mkdirSync(fallbackPkgDir, { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-web-20261114-120000'), { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-desktop-20261114-121000'), { recursive: true })

  // Valid fallback package + third-party package metadata with dsh.bundle.patch
  writeFileSync(join(fallbackPkgDir, 'package.json'), JSON.stringify({ name: 'broken-helper', version: '1.0.0' }))
  writeFileSync(
    join(webDir, 'node_modules', '@scope', 'community-plugin', 'package.json'),
    JSON.stringify({
      name: '@scope/community-plugin',
      version: '1.2.0',
      description: 'Sample community extension',
      dsh: { bundle: { patch: './cordis.patch.yml' } },
    }),
  )
  writeFileSync(
    join(webDir, 'node_modules', '@scope', 'community-plugin', 'cordis.patch.yml'),
    '- insert:\n    id: "@scope/community-loader"\n    name: "@scope/community-plugin"\n',
  )

  // Broken symlink/junction inside web node_modules
  const deadTarget = join(sandbox, 'missing-target-dir')
  symlinkSync(deadTarget, join(webDir, 'node_modules', 'broken-helper'), process.platform === 'win32' ? 'junction' : 'dir')

  // Fake DSHGuard.exe marker in isolated sandbox (never executed)
  writeFileSync(join(guardHome, 'DSHGuard.exe'), '')

  // Snapshot manifests (desktop legacy format)
  writeFileSync(
    join(snapshotsDir, 'snap-web-20261114-120000', 'manifest.json'),
    JSON.stringify({ target: 'web', reason: 'before plugin update', createdAt: '2026-11-14T12:00:00Z', files: ['package.json', 'cordis.patch.yml'] }),
  )
  writeFileSync(
    join(snapshotsDir, 'snap-desktop-20261114-121000', 'manifest.json'),
    JSON.stringify({ target: 'desktop', reason: 'manual checkpoint', createdAt: '2026-11-14T12:10:00Z', files: ['package.json'] }),
  )

  // Web profile: bundle drift + MCP + llm-pi-ai + !!js disabled quirk + community plugin
  writeFileSync(
    join(webDir, 'package.json'),
    JSON.stringify({
      dependencies: {
        '@deepseek-ai/dsh-core': '^1.0.0',
        '@gluztm/dsh-guard': '^2.4.2',
        '@scope/community-plugin': '^1.2.0',
        'dsh-good-plugin': '^1.0.0',
      },
      dsh: { bundles: { 'dsh-good-plugin': {}, 'dsh-broken-plugin': {} } },
    }),
  )
  const initialWebPatch = [
    '- id: llm-pi-ai',
    '  config:',
    '    providers:',
    '      deepseek:',
    '        displayName: DeepSeek',
    '        api: openai-completions',
    '        baseURL: https://api.deepseek.com/v1',
    '        apiKeyEnv: DEEPSEEK_API_KEY',
    '        models:',
    '          - id: deepseek-chat',
    '            name: DeepSeek Chat',
    '- id: openai-extra',
    '  apiKeyEnv: OPENAI_API_KEY',
    '  disabled: !!js false',
    '- insert:',
    '    name: "@deepseek-ai/dsh-mcp-client"',
    '    id: mcp-github',
    '    config:',
    '      serverName: github',
    '      transport: stdio',
    '      command: npx',
  ].join('\n') + '\n'
  writeFileSync(join(webDir, 'cordis.patch.yml'), initialWebPatch)

  // Desktop profile: clean
  writeFileSync(
    join(desktopDir, 'package.json'),
    JSON.stringify({
      dependencies: { '@gluztm/dsh-guard': '^2.4.2' },
      dsh: { bundles: { '@gluztm/dsh-guard': {} } },
    }),
  )
  writeFileSync(join(desktopDir, 'cordis.patch.yml'), '- id: "@gluztm/dsh-guard"\n')

  // Official .credentials.yaml structure with records + refs + protected OAuth blob
  const secretToken = 'sk-TOP-SECRET-NEVER-LEAK-998877'
  const oauthSecret = 'oauth-secret-keep-intact-554433'
  writeFileSync(
    join(dshHome, '.credentials.yaml'),
    [
      'version: 1',
      'records:',
      '  - provider: codex',
      'refs:',
      `  DEEPSEEK_API_KEY: "${secretToken}"`,
      '  EMPTY_KEY: ""',
      `  OPENAI_ACCOUNT_WORK: |`,
      `    {"access_token":"${oauthSecret}"}`,
    ].join('\n') + '\n',
  )

  // 1. Inspect health, bundle drift, broken links, providers, and strict anti-leak assertions
  const report = inspectHealth({ dshHome, guardHome })
  assert.equal(report.healthy, false, 'Expected unhealthy due to bundle drift, broken link, and missing OPENAI_API_KEY')
  assert.ok(report.issues.some((x) => x.includes('dsh-broken-plugin')), 'Should detect bundle missing from dependencies')
  assert.equal(report.profiles.web.brokenLinkCount, 1)
  assert.deepEqual(report.credentials.missingReferencedKeys, ['OPENAI_API_KEY'])
  assert.equal(report.credentials.nonEmptyKeysCount, 2)
  assert.equal(report.providers.length, 1)
  assert.equal(report.providers[0].id, 'deepseek')
  assert.equal(report.providers[0].keyConfigured, true)

  const communityExt = report.extensions.web.plugins.find((p) => p.name === '@scope/community-plugin')
  assert.ok(communityExt && communityExt.canToggle === true)
  assert.equal(communityExt.primaryId, '@scope/community-loader')
  assert.equal(communityExt.enabled, true)

  const mcpItem = report.extensions.web.mcpServers.find((m) => m.id === 'mcp-github')
  assert.ok(mcpItem && mcpItem.enabled === true)

  const reportJson = JSON.stringify(report)
  const reportText = formatHealthText(report)
  assert.equal(reportJson.includes(secretToken), false, 'Secret token leaked into JSON output!')
  assert.equal(reportJson.includes(oauthSecret), false, 'OAuth secret leaked into JSON output!')
  assert.equal(reportJson.includes('998877'), false, 'Secret suffix leaked into JSON output!')
  assert.equal(reportText.includes(secretToken), false, 'Secret token leaked into text output!')
  assert.equal(report.guard.installed, true)
  assert.ok(String(report.guard.installerUrl || '').endsWith('/DSHGuard-Setup-2.4.2.exe'))

  // 1b. Missing DSHGuard.exe keeps status summary concise and focused on companion diagnostics
  const missingGuardReport = inspectHealth({ dshHome, guardHome: join(sandbox, 'nonexistent-guard') })
  assert.equal(missingGuardReport.guard.installed, false)
  const missingGuardText = formatHealthText(missingGuardReport)
  assert.equal(missingGuardText.includes('DSHGuard-Setup-'), false, 'Status summary should stay concise without extra setup URLs')
  assert.equal(missingGuardText.includes(secretToken), false)
  const missingOpen = openGuard({ guardHome: join(sandbox, 'nonexistent-guard'), launch: false })
  assert.equal(missingOpen.installed, false)
  assert.ok(String(missingOpen.installerUrl || '').endsWith('/DSHGuard-Setup-2.4.2.exe'))

  // 2. Inspect snapshots baseline
  const snapReport = inspectSnapshots({ guardHome, target: 'all', limit: 10 })
  assert.equal(snapReport.total, 2)
  assert.equal(snapReport.snapshots[0].id, 'snap-desktop-20261114-121000')
  assert.ok(formatSnapshotsText(snapReport).includes('snap-web-20261114-120000'))

  // 2b. Verify one-way blind-write into .credentials.yaml preserves records & protected OAuth blobs
  const protectedReject = upsertCredentialKey(dshHome, 'OPENAI_ACCOUNT_WORK', 'bad-overwrite')
  assert.equal(protectedReject.ok, false)
  const newKeyValue = 'sk-NEW-BLIND-WRITE-112233'
  const writeOk = upsertCredentialKey(dshHome, 'OPENAI_API_KEY', newKeyValue)
  assert.equal(writeOk.ok, true)
  assert.equal(JSON.stringify(writeOk).includes(newKeyValue), false, 'Blind write response must never echo secret')
  const updatedCredRaw = readFileSync(join(dshHome, '.credentials.yaml'), 'utf8')
  assert.ok(updatedCredRaw.includes('version: 1'))
  assert.ok(updatedCredRaw.includes('- provider: codex'))
  assert.ok(updatedCredRaw.includes(oauthSecret), 'Existing protected OAuth blob must stay untouched')
  assert.ok(updatedCredRaw.includes(`OPENAI_API_KEY: "${newKeyValue}"`))

  // 2c. Verify togglePatchEntry + auto pre-snapshot + self/core protection
  const denySelf = togglePatchEntry(dshHome, 'web', '@gluztm/dsh-guard', true)
  assert.equal(denySelf.ok, false)
  const denyCore = togglePatchEntry(dshHome, 'web', '@deepseek-ai/dsh-core', true)
  assert.equal(denyCore.ok, false)

  const disableRes = togglePatchEntry(dshHome, 'web', '@scope/community-loader', true)
  assert.equal(disableRes.ok, true)
  assert.ok(disableRes.preSnapshotId)
  const patchAfterDisable = readFileSync(join(webDir, 'cordis.patch.yml'), 'utf8')
  assert.ok(patchAfterDisable.includes('- id: "@scope/community-loader"'))
  assert.equal(
    inspectHealth({ dshHome, guardHome }).extensions.web.plugins.find((p) => p.name === '@scope/community-plugin').enabled,
    false,
  )
  const reenableRes = togglePatchEntry(dshHome, 'web', '@scope/community-loader', false)
  assert.equal(reenableRes.ok, true)
  assert.equal(
    inspectHealth({ dshHome, guardHome }).extensions.web.plugins.find((p) => p.name === '@scope/community-plugin').enabled,
    true,
  )

  // 2d. Verify broken module link repair
  const repairRes = repairBrokenModuleLinks(dshHome, 'web')
  assert.equal(repairRes.ok, true)
  assert.equal(repairRes.repairedCount, 1)
  assert.equal(existsSync(join(webDir, 'node_modules', 'broken-helper', 'package.json')), true)
  assert.equal(inspectHealth({ dshHome, guardHome }).profiles.web.brokenLinkCount, 0)

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
    ['dsh_guard_backup', 'dsh_guard_open', 'dsh_guard_snapshots', 'dsh_guard_status'],
  )
  assert.deepEqual(registeredCommands.map((c) => c.name), ['guard'])
  assert.deepEqual(
    registeredRoutes.map((r) => r.path).sort(),
    [
      '/api/dsh-guard/credential-key',
      '/api/dsh-guard/open',
      '/api/dsh-guard/overview',
      '/api/dsh-guard/repair-links',
      '/api/dsh-guard/snapshots/create',
      '/api/dsh-guard/snapshots/restore-config',
      '/api/dsh-guard/toggle-entry',
    ],
  )

  function invokeMockHttp(routePath, method = 'GET', jsonBody = undefined) {
    return new Promise((resolvePromise) => {
      const route = registeredRoutes.find((r) => r.path === routePath)
      assert.ok(route, `Route ${routePath} should be registered`)
      const headers = {}
      const req = new EventEmitter()
      req.method = method
      req.headers = {}
      const res = {
        statusCode: 200,
        setHeader(k, v) { headers[String(k).toLowerCase()] = v },
        end(body = '') {
          resolvePromise({ statusCode: res.statusCode, headers, body, json: body ? JSON.parse(body) : null })
        },
      }
      Promise.resolve(route.handler(req, res))
      queueMicrotask(() => {
        if (jsonBody !== undefined) {
          req.emit('data', Buffer.from(JSON.stringify(jsonBody), 'utf8'))
        }
        req.emit('end')
      })
    })
  }

  const prevDshHome = process.env.DSH_HOME
  const prevGuardHome = process.env.DSHGUARD_HOME
  process.env.DSH_HOME = dshHome
  process.env.DSHGUARD_HOME = guardHome
  try {
    const toolStatus = await registeredTools.find((t) => t.name === 'dsh_guard_status').execute({})
    assert.equal(toolStatus.summary.includes(secretToken), false)
    const toolBackup = await registeredTools.find((t) => t.name === 'dsh_guard_backup').execute({ target: 'web', reason: 'tool test' })
    assert.equal(toolBackup.ok, true)
    assert.equal(existsSync(join(dshHome, 'guard-snapshots', toolBackup.snapshotId, '.credentials.yaml')), false, 'Snapshots MUST NEVER copy .credentials.yaml!')

    const toolSnaps = await registeredTools.find((t) => t.name === 'dsh_guard_snapshots').execute({ limit: 15 })
    assert.ok(toolSnaps.total >= 3)
    const toolOpen = await registeredTools.find((t) => t.name === 'dsh_guard_open').execute({ launch: false })
    assert.equal(toolOpen.installed, true)
    assert.equal(toolOpen.launched, false)

    // Mutate cordis.patch.yml then verify HTTP snapshot diff + config restore!
    const customMarker = '# modified-after-snapshot'
    writeFileSync(join(webDir, 'cordis.patch.yml'), `${readFileSync(join(webDir, 'cordis.patch.yml'), 'utf8')}${customMarker}\n`)

    const httpOverview = await invokeMockHttp('/api/dsh-guard/overview', 'GET')
    assert.equal(httpOverview.statusCode, 200)
    assert.equal(httpOverview.json.ok, true)
    assert.equal(httpOverview.json.version, '2.4.2')
    assert.equal(httpOverview.body.includes(secretToken), false, 'Secret leaked via HTTP overview!')
    assert.equal(httpOverview.body.includes(newKeyValue), false, 'Written key leaked via HTTP overview!')
    const modifiedSnap = httpOverview.json.snapshots.snapshots.find((s) => s.id === toolBackup.snapshotId)
    assert.ok(modifiedSnap && modifiedSnap.changedCount >= 1, 'Live diff should detect modified cordis.patch.yml')

    const httpRestore = await invokeMockHttp('/api/dsh-guard/snapshots/restore-config', 'POST', { snapshotId: toolBackup.snapshotId })
    assert.equal(httpRestore.statusCode, 200)
    assert.equal(httpRestore.json.ok, true)
    assert.ok(httpRestore.json.restoredFiles.includes('profiles/web/cordis.patch.yml'))
    assert.equal(readFileSync(join(webDir, 'cordis.patch.yml'), 'utf8').includes(customMarker), false)

    // HTTP blind write via POST route must not echo secret
    const httpCred = await invokeMockHttp('/api/dsh-guard/credential-key', 'POST', { key: 'CUSTOM_TOKEN_TEST', value: 'sec-http-never-echo-7766' })
    assert.equal(httpCred.statusCode, 200)
    assert.equal(httpCred.body.includes('sec-http-never-echo-7766'), false)

    const httpOpenGet = await invokeMockHttp('/api/dsh-guard/open', 'GET')
    assert.equal(httpOpenGet.statusCode, 405)

    mockRejection = 401
    for (const protectedPath of ['/api/dsh-guard/overview', '/api/dsh-guard/toggle-entry', '/api/dsh-guard/credential-key', '/api/dsh-guard/snapshots/restore-config']) {
      const denied = await invokeMockHttp(protectedPath, protectedPath.endsWith('/overview') ? 'GET' : 'POST', {})
      assert.equal(denied.statusCode, 401, `${protectedPath} must enforce 401 auth gate`)
    }
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
    assert.deepEqual(clientSlots.map((s) => s.name).sort(), ['main', 'sidebar.panellist', 'sidebar.right.pane.tab'])
  } finally {
    if (hadWindow) globalThis.window = prevWindow
    else delete globalThis.window
  }

  // Verify client source uses the unified compact KPI layout and collapsible desktop boundary disclosures
  const clientSrc = readFileSync(join(pkgRoot, 'lib', 'client.js'), 'utf8')
  assert.equal(clientSrc.includes('dshg-hero'), false, 'Client UI should use the unified KPI header instead of legacy dshg-hero banner')
  assert.ok(clientSrc.includes('dshg-boundary'), 'Client UI should render collapsible desktop boundary disclosures')

  // 5. Verify CLI execution in subprocess (status + backup)
  const cliOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'status', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliJson = JSON.parse(cliOut)
  assert.equal(cliJson.companionVersion, '2.4.2')
  assert.equal(cliOut.includes(secretToken), false, 'Secret leaked via CLI!')

  const cliBackupOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'backup', 'web', '--dsh-home', dshHome, '--json'],
    { encoding: 'utf8' },
  )
  assert.equal(JSON.parse(cliBackupOut).ok, true)

  console.log('dsh-guard v2.4.2 smoke test PASSED (all functional + blind-write + symlink + snapshot diff/restore + HTTP + Web client + anti-leak + CLI checks)')
} finally {
  rmSync(sandbox, { recursive: true, force: true })
}
