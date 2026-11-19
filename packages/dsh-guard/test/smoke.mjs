import assert from 'node:assert/strict'
import { EventEmitter } from 'node:events'
import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import net from 'node:net'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import {
  apply,
  createSnapshot,
  detectActiveProfile,
  discoverProfiles,
  formatContextAuditText,
  formatHealthText,
  formatMcpProbeText,
  formatSnapshotsText,
  inspectContextAudit,
  inspectHealth,
  inspectSkills,
  inspectSnapshots,
  openGuard,
  pinSnapshot,
  probeMcpServers,
  readAuditLog,
  repairBrokenModuleLinks,
  togglePatchEntry,
  toggleSkillVisibility,
  updateMcpTimeout,
  upsertCredentialKey,
} from '../lib/index.mjs'

const __dirname = dirname(fileURLToPath(import.meta.url))
const pkgRoot = resolve(__dirname, '..')
const sandbox = mkdtempSync(join(tmpdir(), 'dsh-guard-smoke-'))

function getFreePort() {
  return new Promise((resolvePort, rejectPort) => {
    const srv = net.createServer()
    srv.unref()
    srv.on('error', rejectPort)
    srv.listen(0, '127.0.0.1', () => {
      const addr = srv.address()
      const port = typeof addr === 'object' && addr ? addr.port : 0
      srv.close((err) => {
        if (err) rejectPort(err)
        else resolvePort(port)
      })
    })
  })
}

let probeServer = null
try {
  const dshHome = join(sandbox, '.dsh')
  const agentsHome = join(sandbox, '.agents')
  const webDir = join(dshHome, 'profiles', 'web')
  const desktopDir = join(dshHome, 'profiles', 'desktop')
  const headlessDir = join(dshHome, 'profiles', 'headless')
  const customDir = join(dshHome, 'profiles', 'custom-profile')
  const fallbackPkgDir = join(dshHome, '.dsh-module-fallback', 'node_modules', 'broken-helper')
  const guardHome = join(sandbox, 'guard')
  const snapshotsDir = join(guardHome, 'Snapshots')

  mkdirSync(join(webDir, 'node_modules', 'dsh-good-plugin'), { recursive: true })
  mkdirSync(join(webDir, 'node_modules', '@scope', 'community-plugin', 'skills', 'helper'), { recursive: true })
  mkdirSync(join(desktopDir, 'node_modules'), { recursive: true })
  mkdirSync(join(headlessDir, 'node_modules'), { recursive: true })
  mkdirSync(join(customDir, 'node_modules'), { recursive: true })
  mkdirSync(fallbackPkgDir, { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-web-20261114-120000'), { recursive: true })
  mkdirSync(join(snapshotsDir, 'snap-desktop-20261114-121000'), { recursive: true })
  mkdirSync(join(dshHome, 'skills', 'my-user-skill'), { recursive: true })
  mkdirSync(join(agentsHome, 'skills', 'shared-workflow'), { recursive: true })
  mkdirSync(join(dshHome, 'prompts'), { recursive: true })

  // Spin up local loopback server for safe TCP MCP probing
  probeServer = net.createServer((sock) => {
    sock.end()
  })
  const livePort = await new Promise((resolveListen, rejectListen) => {
    probeServer.on('error', rejectListen)
    probeServer.listen(0, '127.0.0.1', () => {
      const addr = probeServer.address()
      resolveListen(addr.port)
    })
  })
  const deadPort = await getFreePort()

  // Permanent prompt metadata files (metadata stats only)
  writeFileSync(join(dshHome, 'prompt-inject.md'), 'Always keep responses concise and accurate.\n')
  writeFileSync(join(dshHome, 'MEMORY.md'), '# Project Memory\nKeep workspace clean.\n')
  writeFileSync(join(dshHome, 'prompts', 'review.md'), '# Code Review Prompt\n')

  // Writable user skills (preserve CRLF & custom keys on toggle)
  const userSkillPath = join(dshHome, 'skills', 'my-user-skill', 'SKILL.md')
  writeFileSync(
    userSkillPath,
    '---\r\nname: my-user-skill\r\ndescription: Custom user skill for verification\r\nauthor: keep-this-field\r\n---\r\n\r\n# Skill Body\r\nOriginal instructions must stay byte-exact.\r\n',
  )
  writeFileSync(
    join(agentsHome, 'skills', 'shared-workflow', 'SKILL.md'),
    '---\nname: shared-workflow\ndescription: Cross-agent workflow guide\n---\n\nWorkflow body\n',
  )

  // Valid fallback package + third-party package metadata with dsh.bundle.patch + read-only plugin skill
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
  writeFileSync(
    join(webDir, 'node_modules', '@scope', 'community-plugin', 'skills', 'helper', 'SKILL.md'),
    '---\nname: plugin-helper\ndescription: Bundled read-only plugin skill\n---\n\nPlugin skill body\n',
  )

  // Broken symlink/junctions: resolvable inside web, unresolvable inside desktop, ignored inside headless/custom-profile
  const deadTarget = join(sandbox, 'missing-target-dir')
  symlinkSync(deadTarget, join(webDir, 'node_modules', 'broken-helper'), process.platform === 'win32' ? 'junction' : 'dir')
  symlinkSync(deadTarget, join(desktopDir, 'node_modules', 'orphan-unresolvable'), process.platform === 'win32' ? 'junction' : 'dir')
  symlinkSync(deadTarget, join(headlessDir, 'node_modules', 'ignored-headless-broken'), process.platform === 'win32' ? 'junction' : 'dir')
  symlinkSync(deadTarget, join(customDir, 'node_modules', 'ignored-custom-broken'), process.platform === 'win32' ? 'junction' : 'dir')

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

  // Web profile: bundle drift + MCP (stdio ok, stdio missing script, loopback ok, loopback dead, remote skipped) + llm-pi-ai + !!js disabled quirk
  writeFileSync(
    join(webDir, 'package.json'),
    JSON.stringify({
      dependencies: {
        '@deepseek-ai/dsh-core': '^1.0.0',
        '@gluztm/dsh-guard': '^2.4.3',
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
    '      command: node',
    '- insert:',
    '    - id: mcp-local-ok',
    '      name: "@deepseek-ai/dsh-mcp-client"',
    '      config:',
    '        serverName: local-ok',
    '        transport: streamable-http',
    `        url: "http://127.0.0.1:${livePort}/mcp"`,
    '        toolCallTimeoutMs: 45000',
    '    - id: mcp-local-dead',
    '      name: "@deepseek-ai/dsh-mcp-client"',
    '      config:',
    '        serverName: local-dead',
    '        transport: streamable-http',
    `        url: "http://127.0.0.1:${deadPort}/mcp"`,
    '    - id: mcp-remote',
    '      name: "@deepseek-ai/dsh-mcp-client"',
    '      config:',
    '        serverName: remote-sse',
    '        transport: sse',
    '        url: "https://mcp.example.com/sse"',
    '    - id: mcp-missing-script',
    '      name: "@deepseek-ai/dsh-mcp-client"',
    '      config:',
    '        serverName: missing-script',
    '        transport: stdio',
    '        command: node',
    '        args:',
    '          - ./missing-entry-point.mjs',
  ].join('\n') + '\n'
  writeFileSync(join(webDir, 'cordis.patch.yml'), initialWebPatch)

  // Desktop profile: clean
  writeFileSync(
    join(desktopDir, 'package.json'),
    JSON.stringify({
      dependencies: { '@gluztm/dsh-guard': '^2.4.3' },
      dsh: { bundles: { '@gluztm/dsh-guard': {} } },
    }),
  )
  writeFileSync(join(desktopDir, 'cordis.patch.yml'), '- id: "@gluztm/dsh-guard"\n')

  // Ignored 3rd/private profile dirs: headless & custom-profile (must be completely skipped by discovery/inspection)
  writeFileSync(
    join(headlessDir, 'package.json'),
    JSON.stringify({
      dependencies: { '@gluztm/dsh-guard': '^2.4.3', '@scope/community-plugin': '^1.2.0' },
      dsh: { bundles: { '@gluztm/dsh-guard': {}, '@scope/community-plugin': {} } },
    }),
  )
  writeFileSync(join(headlessDir, 'cordis.patch.yml'), '- id: "@gluztm/dsh-guard"\n')
  writeFileSync(
    join(customDir, 'package.json'),
    JSON.stringify({
      dependencies: { '@gluztm/dsh-guard': '^2.4.3', 'custom-private-plugin': '^1.0.0' },
      dsh: { bundles: { '@gluztm/dsh-guard': {}, 'custom-private-plugin': {} } },
    }),
  )
  writeFileSync(join(customDir, 'cordis.patch.yml'), '- id: custom-loader\n  apiKeyEnv: CUSTOM_MISSING_KEY\n')

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

  // 1. Verify single-profile host auto-detection & zero noise from headless/custom-profile/cross-profile drift
  const strictProxyTarget = {
    get(svc) {
      if (svc === 'loader') throw new Error('cannot get property "loader" without inject')
      return null
    },
    inject() {},
    effect() {},
  }
  const strictCordisProxy = new Proxy(strictProxyTarget, {
    get(target, prop) {
      if (prop === 'get' || prop === 'inject' || prop === 'effect') return target[prop]
      throw new Error(`cannot get property "${String(prop)}" without inject`)
    },
  })
  assert.equal(
    detectActiveProfile({
      ctx: strictCordisProxy,
      argv: ['node', 'C:\\app\\@deepseek-ai\\dsh-desktop-host\\lib\\index.js'],
    }),
    'desktop',
  )
  assert.equal(detectActiveProfile({ versions: { electron: '32.0.0' } }), 'desktop')
  assert.equal(detectActiveProfile({ argv: ['node', 'C:\\app\\@deepseek-ai\\dsh-desktop-host\\lib\\index.js'] }), 'desktop')
  assert.equal(detectActiveProfile({ argv: ['node', 'C:\\u\\.dsh\\profiles\\desktop\\index.js'] }), 'desktop')
  assert.equal(detectActiveProfile({ ctx: { baseDir: 'C:\\u\\.dsh\\profiles\\desktop' } }), 'desktop')
  assert.equal(detectActiveProfile({ env: { DSH_PROFILE: 'desktop' } }), 'desktop')
  assert.equal(detectActiveProfile({ env: {}, argv: ['node'], ctx: strictCordisProxy, versions: {} }), 'web')

  assert.deepEqual(discoverProfiles(dshHome), ['web', 'desktop'])
  const report = inspectHealth({ dshHome, guardHome, agentsHome, activeProfile: 'web' })
  assert.equal(report.activeProfile, 'web')
  assert.deepEqual(report.profileNames, ['web'])
  assert.equal(report.issues.some((x) => /配置漂移|依赖数量差异/.test(x)), false, 'Single-track health must never emit cross-profile drift warnings')
  assert.equal(report.healthy, false, 'Expected unhealthy due to bundle drift, broken links, and missing OPENAI_API_KEY')
  assert.ok(report.issues.some((x) => x.includes('dsh-broken-plugin')), 'Should detect bundle missing from dependencies')
  assert.equal(report.profiles.web.brokenLinkCount, 1)
  assert.deepEqual(report.credentials.missingReferencedKeys, ['OPENAI_API_KEY'])
  assert.equal(report.credentials.nonEmptyKeysCount, 2)
  assert.equal(report.providers.length, 1)
  assert.equal(report.providers[0].id, 'deepseek')
  assert.equal(report.providers[0].keyConfigured, true)
  assert.equal(report.contextAudit.profiles.length, 1)
  assert.ok(report.contextAudit.skills.total >= 3)
  assert.equal(report.contextAudit.skills.writableCount, 2)

  const desktopReport = inspectHealth({ dshHome, guardHome, agentsHome, activeProfile: 'desktop' })
  assert.equal(desktopReport.activeProfile, 'desktop')
  assert.deepEqual(desktopReport.profileNames, ['desktop'])
  assert.equal(desktopReport.issues.some((x) => /配置漂移|依赖数量差异/.test(x)), false, 'Desktop track must never emit cross-profile drift warnings')
  assert.equal(desktopReport.profiles.desktop.brokenLinkCount, 1)
  assert.equal(JSON.stringify(desktopReport).includes('dsh-broken-plugin'), false, 'Desktop track must ignore web issues')

  const communityExt = report.extensions.web.plugins.find((p) => p.name === '@scope/community-plugin')
  assert.ok(communityExt && communityExt.canToggle === true)
  assert.equal(communityExt.primaryId, '@scope/community-loader')
  assert.equal(communityExt.enabled, true)

  const mcpItem = report.extensions.web.mcpServers.find((m) => m.id === 'mcp-github')
  assert.ok(mcpItem && mcpItem.enabled === true && mcpItem.toolCallTimeoutMs === 60000)

  const reportJson = JSON.stringify(report)
  const reportText = formatHealthText(report)
  assert.equal(reportJson.includes('headless'), false, 'Ignored headless folder must not appear in report JSON')
  assert.equal(reportJson.includes('custom-profile'), false, 'Ignored custom profile must not appear in report JSON')
  assert.equal(reportJson.includes('CUSTOM_MISSING_KEY'), false, 'Ignored custom profile key must not appear in report JSON')
  assert.equal(reportJson.includes(secretToken), false, 'Secret token leaked into JSON output!')
  assert.equal(reportJson.includes(oauthSecret), false, 'OAuth secret leaked into JSON output!')
  assert.equal(reportJson.includes('998877'), false, 'Secret suffix leaked into JSON output!')
  assert.equal(reportText.includes(secretToken), false, 'Secret token leaked into text output!')
  assert.equal(report.guard.installed, true)
  assert.ok(String(report.guard.installerUrl || '').endsWith('/DSHGuard-Setup-2.4.3.exe'))

  // 1b. Missing DSHGuard.exe keeps status summary concise
  const missingGuardReport = inspectHealth({ dshHome, guardHome: join(sandbox, 'nonexistent-guard'), agentsHome, activeProfile: 'web' })
  assert.equal(missingGuardReport.guard.installed, false)
  const missingGuardText = formatHealthText(missingGuardReport)
  assert.equal(missingGuardText.includes('DSHGuard-Setup-'), false)
  assert.equal(missingGuardText.includes(secretToken), false)
  const missingOpen = openGuard({ guardHome: join(sandbox, 'nonexistent-guard'), launch: false })
  assert.equal(missingOpen.installed, false)
  assert.ok(String(missingOpen.installerUrl || '').endsWith('/DSHGuard-Setup-2.4.3.exe'))

  // 1c. Verify SSRF-safe local MCP probing (reachable loopback, dead loopback, remote skipped, missing stdio script)
  const mcpProbe = await probeMcpServers(dshHome, report.extensions, 'web')
  assert.equal(mcpProbe.total, 5)
  assert.equal(mcpProbe.results.find((r) => r.id === 'mcp-local-ok')?.status, 'reachable')
  assert.equal(mcpProbe.results.find((r) => r.id === 'mcp-local-dead')?.status, 'unreachable')
  assert.equal(mcpProbe.results.find((r) => r.id === 'mcp-remote')?.status, 'remote_skipped')
  assert.equal(mcpProbe.results.find((r) => r.id === 'mcp-remote')?.healthy, true)
  assert.equal(mcpProbe.results.find((r) => r.id === 'mcp-missing-script')?.status, 'missing_script')
  assert.ok(formatMcpProbeText(mcpProbe).includes('remote_skipped') || formatMcpProbeText(mcpProbe).includes('远端'))

  // 1d. Verify lossless MCP timeout update (both insert new key and update existing key while keeping !!js intact)
  const outOfRange = updateMcpTimeout(dshHome, 'web', 'mcp-github', 1000)
  assert.equal(outOfRange.ok, false)
  const tmAdd = updateMcpTimeout(dshHome, 'web', 'mcp-github', 120000)
  assert.equal(tmAdd.ok, true)
  assert.equal(tmAdd.toolCallTimeoutMs, 120000)
  const tmUpd = updateMcpTimeout(dshHome, 'web', 'mcp-local-ok', 90000)
  assert.equal(tmUpd.ok, true)
  assert.equal(inspectHealth({ dshHome, guardHome, activeProfile: 'web' }).extensions.web.mcpServers.find((m) => m.id === 'mcp-github')?.toolCallTimeoutMs, 120000)
  assert.equal(inspectHealth({ dshHome, guardHome, activeProfile: 'web' }).extensions.web.mcpServers.find((m) => m.id === 'mcp-local-ok')?.toolCallTimeoutMs, 90000)
  assert.ok(readFileSync(join(webDir, 'cordis.patch.yml'), 'utf8').includes('disabled: !!js false'), 'Existing !!js tag must stay untouched')

  // 1e. Verify Skills scanning, lossless visibility toggles, fingerprint conflict detection, and plugin skill read-only guard
  const skillsBefore = inspectSkills(dshHome, ['web'], { agentsHome })
  const mySkill = skillsBefore.skills.find((s) => s.id === 'user-dsh:my-user-skill')
  const roSkill = skillsBefore.skills.find((s) => s.packageName === '@scope/community-plugin')
  assert.ok(mySkill && mySkill.writable === true && mySkill.modelVisible === true && mySkill.userInvocable === true)
  assert.ok(roSkill && roSkill.writable === false)

  const roReject = toggleSkillVisibility(dshHome, roSkill.id, { modelVisible: false }, { agentsHome })
  assert.equal(roReject.ok, false)

  const skOff = toggleSkillVisibility(
    dshHome,
    mySkill.id,
    { modelVisible: false, userInvocable: false },
    { expectedFingerprint: mySkill.fingerprint, agentsHome },
  )
  assert.equal(skOff.ok, true)
  assert.equal(skOff.skill.modelVisible, false)
  assert.equal(skOff.skill.userInvocable, false)
  const rawAfterSkillOff = readFileSync(userSkillPath, 'utf8')
  assert.ok(rawAfterSkillOff.includes('\r\n'), 'CRLF line endings must be preserved')
  assert.ok(rawAfterSkillOff.includes('author: keep-this-field'))
  assert.ok(rawAfterSkillOff.includes('disable-model-invocation: true'))
  assert.ok(rawAfterSkillOff.includes('user-invocable: false'))
  assert.ok(rawAfterSkillOff.includes('Original instructions must stay byte-exact.'))

  const staleFpReject = toggleSkillVisibility(
    dshHome,
    mySkill.id,
    { modelVisible: true },
    { expectedFingerprint: mySkill.fingerprint, agentsHome },
  )
  assert.equal(staleFpReject.ok, false, 'Stale fingerprint must be rejected')

  const skOn = toggleSkillVisibility(
    dshHome,
    mySkill.id,
    { modelVisible: true, userInvocable: true },
    { expectedFingerprint: skOff.skill.fingerprint, agentsHome },
  )
  assert.equal(skOn.ok, true)
  assert.equal(skOn.skill.modelVisible, true)
  assert.equal(skOn.skill.userInvocable, true)

  // 2. Inspect snapshots baseline & snapshot pinning + auto-prune immunity
  const snapReport = inspectSnapshots({ guardHome, target: 'all', limit: 10 })
  assert.ok(snapReport.total >= 2)
  assert.ok(formatSnapshotsText(snapReport).includes('snap-web-20261114-120000'))

  const pinnedAuto = createSnapshot(dshHome, 'web', 'auto-toggle', 'pinned auto baseline')
  const pinRes = pinSnapshot(dshHome, pinnedAuto.snapshot.id, true, '2.4.3 基线')
  assert.equal(pinRes.ok, true)
  assert.equal(pinRes.pinned, true)
  const deskSnapReject = pinSnapshot(dshHome, 'snap-web-20261114-120000', true)
  assert.equal(deskSnapReject.ok, false)

  // Create 42 dummy old auto-toggle snapshots to trigger retention cleanup (keep 40 unpinned auto-*)
  for (let idx = 0; idx < 42; idx += 1) {
    const oldId = `20200101-0000${String(idx).padStart(2, '0')}-000-auto-toggle-web`
    const dir = join(dshHome, 'guard-snapshots', oldId)
    mkdirSync(dir, { recursive: true })
    writeFileSync(
      join(dir, 'manifest.json'),
      JSON.stringify({
        id: oldId,
        source: 'plugin',
        target: 'web',
        kind: 'auto-toggle',
        reason: 'old auto',
        createdAt: `2020-01-01T00:00:${String(idx).padStart(2, '0')}.000Z`,
        pinned: false,
        itemCount: 0,
        items: [],
      }),
    )
  }
  createSnapshot(dshHome, 'web', 'auto-toggle', 'trigger retention prune')
  assert.equal(existsSync(join(dshHome, 'guard-snapshots', pinnedAuto.snapshot.id, 'manifest.json')), true, 'Pinned auto snapshot must survive auto pruning')

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

  // 2c. Verify togglePatchEntry + auto pre-snapshot + self/core protection (web & desktop)
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
    inspectHealth({ dshHome, guardHome, activeProfile: 'web' }).extensions.web.plugins.find((p) => p.name === '@scope/community-plugin').enabled,
    false,
  )
  const reenableRes = togglePatchEntry(dshHome, 'web', '@scope/community-loader', false)
  assert.equal(reenableRes.ok, true)
  assert.equal(
    inspectHealth({ dshHome, guardHome, activeProfile: 'web' }).extensions.web.plugins.find((p) => p.name === '@scope/community-plugin').enabled,
    true,
  )

  const desktopSnap = createSnapshot(dshHome, 'desktop', 'manual', 'desktop checkpoint')
  assert.equal(desktopSnap.ok, true)
  assert.equal(desktopSnap.snapshot.target, 'desktop')

  // 2d. Verify broken module link repair (resolvable on web, unresolvable graceful handling on desktop)
  const repairWeb = repairBrokenModuleLinks(dshHome, 'web')
  assert.equal(repairWeb.ok, true)
  assert.equal(repairWeb.allRepaired, true)
  assert.equal(repairWeb.repairedCount, 1)
  assert.equal(repairWeb.unresolvableCount, 0)
  assert.equal(existsSync(join(webDir, 'node_modules', 'broken-helper', 'package.json')), true)
  assert.equal(inspectHealth({ dshHome, guardHome, activeProfile: 'web' }).profiles.web.brokenLinkCount, 0)

  const repairDesktop = repairBrokenModuleLinks(dshHome, 'desktop')
  assert.equal(repairDesktop.ok, true)
  assert.equal(repairDesktop.allRepaired, false)
  assert.equal(repairDesktop.repairedCount, 0)
  assert.equal(repairDesktop.unresolvableCount, 1)
  assert.deepEqual(repairDesktop.unresolvedNames, ['orphan-unresolvable'])
  assert.ok(String(repairDesktop.message || '').includes('orphan-unresolvable'))

  // Verify redacted audit log never contains secret tokens
  const auditEntries = readAuditLog(dshHome, 50)
  assert.ok(auditEntries.some((a) => a.action === 'mcp.timeout'))
  assert.ok(auditEntries.some((a) => a.action === 'skill.toggle'))
  assert.ok(auditEntries.some((a) => a.action === 'snapshot.pin'))
  assert.ok(auditEntries.some((a) => a.action === 'credential.upsert'))
  const auditStr = JSON.stringify(auditEntries)
  assert.equal(auditStr.includes(secretToken), false)
  assert.equal(auditStr.includes(newKeyValue), false)

  // 3. Verify Cordis apply(ctx) plugin wiring (6 tools, /guard command, 11 webServer HTTP routes)
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
  const mockCtxBase = {
    get(svc) {
      if (svc === 'connection') {
        return { requestRejection: () => mockRejection }
      }
      if (svc === 'loader' || svc === 'root') {
        throw new Error(`cannot get service "${svc}" without inject`)
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
  const mockCtx = new Proxy(mockCtxBase, {
    get(target, prop) {
      if (prop === 'get' || prop === 'inject' || prop === 'effect') return target[prop]
      throw new Error(`cannot get property "${String(prop)}" without inject`)
    },
  })
  apply(mockCtx)
  assert.deepEqual(
    registeredTools.map((t) => t.name).sort(),
    [
      'dsh_guard_backup',
      'dsh_guard_context_audit',
      'dsh_guard_mcp_probe',
      'dsh_guard_open',
      'dsh_guard_snapshots',
      'dsh_guard_status',
    ],
  )
  assert.deepEqual(registeredCommands.map((c) => c.name), ['guard'])
  assert.deepEqual(
    registeredRoutes.map((r) => r.path).sort(),
    [
      '/api/dsh-guard/credential-key',
      '/api/dsh-guard/mcp/probe',
      '/api/dsh-guard/mcp/timeout',
      '/api/dsh-guard/open',
      '/api/dsh-guard/overview',
      '/api/dsh-guard/repair-links',
      '/api/dsh-guard/skills/toggle',
      '/api/dsh-guard/snapshots/create',
      '/api/dsh-guard/snapshots/pin',
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
    const toolCtxAudit = await registeredTools.find((t) => t.name === 'dsh_guard_context_audit').execute({ target: 'web' })
    assert.ok(toolCtxAudit.estimatedBaseTokens > 0 && toolCtxAudit.summary.includes('上下文体检'))
    const toolMcpProbe = await registeredTools.find((t) => t.name === 'dsh_guard_mcp_probe').execute({ target: 'web' })
    assert.equal(toolMcpProbe.total, 5)
    assert.ok(toolMcpProbe.summary.includes('MCP 探活'))

    const toolBackup = await registeredTools.find((t) => t.name === 'dsh_guard_backup').execute({ target: 'web', reason: 'tool test' })
    assert.equal(toolBackup.ok, true)
    assert.equal(existsSync(join(dshHome, 'guard-snapshots', toolBackup.snapshotId, '.credentials.yaml')), false, 'Snapshots MUST NEVER copy .credentials.yaml!')

    const toolSnaps = await registeredTools.find((t) => t.name === 'dsh_guard_snapshots').execute({ limit: 15 })
    assert.ok(toolSnaps.total >= 3)
    const toolOpen = await registeredTools.find((t) => t.name === 'dsh_guard_open').execute({ launch: false })
    assert.equal(toolOpen.installed, true)
    assert.equal(toolOpen.launched, false)

    // Test slash command subcommands
    const guardCmd = registeredCommands[0]
    const cmdCtxOut = await guardCmd.handler({ rawInput: 'context' })
    assert.equal(cmdCtxOut.kind, 'success')
    const cmdMcpOut = await guardCmd.handler({ rawInput: 'mcp web' })
    assert.equal(cmdMcpOut.kind, 'success')

    // Mutate cordis.patch.yml then verify HTTP snapshot diff + pin + config restore!
    const customMarker = '# modified-after-snapshot'
    writeFileSync(join(webDir, 'cordis.patch.yml'), `${readFileSync(join(webDir, 'cordis.patch.yml'), 'utf8')}${customMarker}\n`)

    const httpOverview = await invokeMockHttp('/api/dsh-guard/overview', 'GET')
    assert.equal(httpOverview.statusCode, 200)
    assert.equal(httpOverview.json.ok, true)
    assert.equal(httpOverview.json.version, '2.4.3')
    assert.equal(httpOverview.json.activeProfile, 'web')
    assert.deepEqual(httpOverview.json.health.profileNames, ['web'])
    assert.equal(httpOverview.body.includes('headless'), false, 'Overview must not expose ignored headless folder')
    assert.equal(httpOverview.body.includes('custom-profile'), false, 'Overview must not expose ignored custom profile')
    assert.equal(httpOverview.body.includes(secretToken), false, 'Secret leaked via HTTP overview!')
    assert.equal(httpOverview.body.includes(newKeyValue), false, 'Written key leaked via HTTP overview!')
    const modifiedSnap = httpOverview.json.snapshots.snapshots.find((s) => s.id === toolBackup.snapshotId)
    assert.ok(modifiedSnap && modifiedSnap.changedCount >= 1, 'Live diff should detect modified cordis.patch.yml')

    // Repair-links HTTP endpoint MUST return HTTP 200 (never HTTP 400) even when unresolvable links remain
    const httpRepairDesktop = await invokeMockHttp('/api/dsh-guard/repair-links', 'POST', { target: 'desktop' })
    assert.equal(httpRepairDesktop.statusCode, 200)
    assert.equal(httpRepairDesktop.json.ok, true)
    assert.equal(httpRepairDesktop.json.allRepaired, false)
    assert.equal(httpRepairDesktop.json.unresolvableCount, 1)
    assert.ok(Array.isArray(httpRepairDesktop.json.unresolvedNames) && httpRepairDesktop.json.unresolvedNames.includes('orphan-unresolvable'))
    assert.ok(String(httpRepairDesktop.json.message || '').length > 0)

    const httpPin = await invokeMockHttp('/api/dsh-guard/snapshots/pin', 'POST', { snapshotId: toolBackup.snapshotId, pinned: true, pinLabel: 'HTTP置顶' })
    assert.equal(httpPin.statusCode, 200)
    assert.equal(httpPin.json.pinned, true)

    const httpProbe = await invokeMockHttp('/api/dsh-guard/mcp/probe', 'POST', { target: 'web' })
    assert.equal(httpProbe.statusCode, 200)
    assert.equal(httpProbe.json.total, 5)

    const httpTimeout = await invokeMockHttp('/api/dsh-guard/mcp/timeout', 'POST', { target: 'web', id: 'mcp-github', timeoutMs: 75000 })
    assert.equal(httpTimeout.statusCode, 200)
    assert.equal(httpTimeout.json.toolCallTimeoutMs, 75000)

    const httpSkillToggle = await invokeMockHttp('/api/dsh-guard/skills/toggle', 'POST', {
      skillId: mySkill.id,
      modelVisible: false,
    })
    assert.equal(httpSkillToggle.statusCode, 200)
    assert.equal(httpSkillToggle.json.skill.modelVisible, false)
    await invokeMockHttp('/api/dsh-guard/skills/toggle', 'POST', { skillId: mySkill.id, modelVisible: true })

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
    for (const protectedPath of [
      '/api/dsh-guard/overview',
      '/api/dsh-guard/toggle-entry',
      '/api/dsh-guard/credential-key',
      '/api/dsh-guard/snapshots/restore-config',
      '/api/dsh-guard/snapshots/pin',
      '/api/dsh-guard/mcp/probe',
      '/api/dsh-guard/mcp/timeout',
      '/api/dsh-guard/skills/toggle',
    ]) {
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

  // 4. Verify Web UI client entry registration via ModuleLoader stub (no React.Fragment!)
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

  const clientSrc = readFileSync(join(pkgRoot, 'lib', 'client.js'), 'utf8')
  assert.equal(clientSrc.includes('React.Fragment'), false, 'Client UI must NEVER use React.Fragment')
  assert.equal(clientSrc.includes('dshg-hero'), false, 'Client UI should use the unified KPI header instead of legacy dshg-hero banner')
  assert.ok(clientSrc.includes('dshg-boundary'), 'Client UI should render collapsible desktop boundary disclosures')

  // 5. Verify CLI execution in subprocess (status, status --target desktop, context, mcp-probe, audit, backup desktop)
  const cliOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'status', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliJson = JSON.parse(cliOut)
  assert.equal(cliJson.companionVersion, '2.4.3')
  assert.equal(cliOut.includes(secretToken), false, 'Secret leaked via CLI!')

  const cliDesktopOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'status', '--target', 'desktop', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliDesktopJson = JSON.parse(cliDesktopOut)
  assert.equal(cliDesktopJson.activeProfile, 'desktop')
  assert.deepEqual(cliDesktopJson.profileNames, ['desktop'])
  assert.equal(cliDesktopOut.includes('headless'), false)
  assert.equal(cliDesktopOut.includes('custom-profile'), false)

  const cliCtxOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'context', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  assert.ok(Array.isArray(JSON.parse(cliCtxOut).profiles))

  const cliProbeOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'mcp-probe', '--target', 'web', '--dsh-home', dshHome, '--guard-home', guardHome, '--json'],
    { encoding: 'utf8' },
  )
  assert.equal(JSON.parse(cliProbeOut).total, 5)

  const cliAuditOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'audit', '--dsh-home', dshHome, '--json'],
    { encoding: 'utf8' },
  )
  assert.ok(JSON.parse(cliAuditOut).total >= 1)

  const cliBackupOut = execFileSync(
    process.execPath,
    [join(pkgRoot, 'bin', 'dsh-guard.mjs'), 'backup', 'desktop', '--dsh-home', dshHome, '--json'],
    { encoding: 'utf8' },
  )
  const cliBackupJson = JSON.parse(cliBackupOut)
  assert.equal(cliBackupJson.ok, true)
  assert.equal(cliBackupJson.snapshot.target, 'desktop')

  console.log('dsh-guard v2.4.3 smoke test PASSED (single-track auto-detection + repair HTTP 200 + MCP probe/timeout + Skills/context audit + snapshot pin + audit log + blind-write + 11 HTTP routes + 6 tools + CLI checks)')
} finally {
  if (probeServer) {
    await new Promise((r) => probeServer.close(() => r()))
  }
  rmSync(sandbox, { recursive: true, force: true })
}
