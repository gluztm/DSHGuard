import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs'
import { execFileSync, spawn } from 'node:child_process'
import { homedir } from 'node:os'
import { basename, join, resolve } from 'node:path'

export const RELEASE_URL = 'https://github.com/gluztm/DSHGuard/releases/latest'

const REG_POINTER_KEY = 'HKCU\\Software\\DSHGuard'
const REG_UNINSTALL_KEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{9E1F7C34-5B2A-4D18-9C77-6A0E5D3B21F4}_is1'

export function resolveDshHome(overrideHome) {
  if (overrideHome && String(overrideHome).trim()) {
    return resolve(String(overrideHome).trim())
  }
  if (process.env.DSH_HOME && process.env.DSH_HOME.trim()) {
    return resolve(process.env.DSH_HOME.trim())
  }
  return join(homedir(), '.dsh')
}

function queryRegInstallLocation(keyPath, valueName) {
  if (process.platform !== 'win32') return null
  try {
    const out = execFileSync('reg.exe', ['query', keyPath, '/v', valueName], {
      encoding: 'utf8',
      timeout: 2000,
      windowsHide: true,
      stdio: ['ignore', 'pipe', 'ignore'],
    })
    for (const rawLine of out.split(/\r?\n/)) {
      const m = rawLine.trim().match(new RegExp(`^${valueName}\\s+REG_\\w+\\s+(.+)$`, 'i'))
      if (m && m[1]) return m[1].trim()
    }
  } catch {
    return null
  }
  return null
}

/**
 * Locate local DSHGuard out-of-band installation (strictly read-only).
 */
export function locateGuardInstallation(options = {}) {
  const candidates = []
  if (options.guardHome && String(options.guardHome).trim()) {
    candidates.push(resolve(String(options.guardHome).trim()))
  } else {
    if (process.env.DSHGUARD_HOME && process.env.DSHGUARD_HOME.trim()) {
      candidates.push(resolve(process.env.DSHGUARD_HOME.trim()))
    }
    const ptrDir = queryRegInstallLocation(REG_POINTER_KEY, 'InstallLocation')
      || queryRegInstallLocation(REG_POINTER_KEY, 'InstallDir')
    if (ptrDir) candidates.push(resolve(ptrDir))

    const uninstDir = queryRegInstallLocation(REG_UNINSTALL_KEY, 'InstallLocation')
    if (uninstDir) candidates.push(resolve(uninstDir))

    const localAppData = process.env.LOCALAPPDATA
    if (localAppData) {
      candidates.push(join(localAppData, 'Programs', 'DSHGuard'))
    }
  }

  let matchedHome = null
  let matchedExe = null
  let matchedSnapshots = null

  for (const dir of candidates) {
    if (!dir || !existsSync(dir)) continue
    const exeCandidate = join(dir, 'DSHGuard.exe')
    const snapCandidate = options.snapshotsDir ? resolve(String(options.snapshotsDir)) : join(dir, 'Snapshots')
    if (existsSync(exeCandidate) || existsSync(snapCandidate)) {
      matchedHome = dir
      if (existsSync(exeCandidate)) matchedExe = exeCandidate
      if (existsSync(snapCandidate)) matchedSnapshots = snapCandidate
      break
    }
  }

  if (!matchedSnapshots && options.snapshotsDir && existsSync(resolve(String(options.snapshotsDir)))) {
    matchedSnapshots = resolve(String(options.snapshotsDir))
  }

  return {
    installed: Boolean(matchedExe),
    guardHome: matchedHome,
    exePath: matchedExe,
    snapshotsDir: matchedSnapshots,
    releaseUrl: RELEASE_URL,
  }
}

function readJsonSafe(filePath) {
  try {
    const raw = readFileSync(filePath, 'utf8')
    return { ok: true, data: JSON.parse(raw), error: null }
  } catch (err) {
    return { ok: false, data: null, error: err && err.message ? err.message : String(err) }
  }
}

function extractBundleIds(bundlesField) {
  if (Array.isArray(bundlesField)) {
    return bundlesField.map((item) => String(item || '').trim()).filter(Boolean)
  }
  if (bundlesField && typeof bundlesField === 'object') {
    return Object.keys(bundlesField).map((k) => k.trim()).filter(Boolean)
  }
  return []
}

function scanCordisPatch(patchPath) {
  if (!existsSync(patchPath)) {
    return {
      exists: false,
      readable: false,
      syntaxOk: true,
      hasTabIndent: false,
      disabledCount: 0,
      apiKeyEnvKeys: [],
      mcpEntriesCount: 0,
      error: null,
    }
  }
  try {
    const raw = readFileSync(patchPath, 'utf8')
    const lines = raw.split(/\r?\n/)
    let hasTabIndent = false
    let disabledCount = 0
    let mcpEntriesCount = 0
    const envSet = new Set()
    let hasStructuredToken = false

    for (const line of lines) {
      if (/^\t+/.test(line)) hasTabIndent = true
      const trimmed = line.trim()
      if (!trimmed || trimmed.startsWith('#')) continue
      if (trimmed.startsWith('- ') || /^[A-Za-z0-9_.-]+\s*:/.test(trimmed)) {
        hasStructuredToken = true
      }
      if (/^enabled\s*:\s*false\b/i.test(trimmed)) {
        disabledCount += 1
      }
      const envMatch = trimmed.match(/^apiKeyEnv\s*:\s*['"]?([A-Za-z0-9_]+)['"]?/i)
      if (envMatch && envMatch[1]) {
        envSet.add(envMatch[1])
      }
      if (/\bmcpServers\b|id\s*:\s*['"]?mcp\b/i.test(trimmed)) {
        mcpEntriesCount += 1
      }
    }

    const nonEmpty = lines.some((l) => l.trim() && !l.trim().startsWith('#'))
    const syntaxOk = !hasTabIndent && (!nonEmpty || hasStructuredToken)

    return {
      exists: true,
      readable: true,
      syntaxOk,
      hasTabIndent,
      disabledCount,
      apiKeyEnvKeys: Array.from(envSet).sort(),
      mcpEntriesCount,
      error: syntaxOk ? null : (hasTabIndent ? 'Tab indentation detected in YAML' : 'Unrecognized YAML structure'),
    }
  } catch (err) {
    return {
      exists: true,
      readable: false,
      syntaxOk: false,
      hasTabIndent: false,
      disabledCount: 0,
      apiKeyEnvKeys: [],
      mcpEntriesCount: 0,
      error: err && err.message ? err.message : String(err),
    }
  }
}

/**
 * Read-only health inspection of a single DSH profile ('web' or 'desktop').
 */
export function inspectProfile(profileDir, targetName) {
  const exists = existsSync(profileDir)
  const pkgPath = join(profileDir, 'package.json')
  const patchPath = join(profileDir, 'cordis.patch.yml')

  if (!exists) {
    return {
      target: targetName,
      profileDir,
      exists: false,
      packageJsonOk: false,
      dependenciesCount: 0,
      bundlesCount: 0,
      missingInDeps: [],
      unregisteredDeps: [],
      patch: scanCordisPatch(patchPath),
      issues: [],
    }
  }

  const pkgRes = existsSync(pkgPath)
    ? readJsonSafe(pkgPath)
    : { ok: false, data: null, error: 'package.json not found' }

  const depsObj = pkgRes.ok && pkgRes.data && typeof pkgRes.data.dependencies === 'object' && pkgRes.data.dependencies
    ? pkgRes.data.dependencies
    : {}
  const depNames = Object.keys(depsObj)
  const depSet = new Set(depNames)

  const bundles = pkgRes.ok && pkgRes.data && pkgRes.data.dsh
    ? extractBundleIds(pkgRes.data.dsh.bundles ?? (pkgRes.data.dsh.profile && pkgRes.data.dsh.profile.bundles))
    : []
  const bundleSet = new Set(bundles)

  const missingInDeps = bundles.filter((b) => !depSet.has(b))
  const unregisteredDeps = depNames.filter((d) => {
    if (bundleSet.has(d)) return false
    return d.startsWith('dsh-') || d.includes('/dsh-')
  })

  const patch = scanCordisPatch(patchPath)
  const issues = []
  if (!pkgRes.ok) {
    issues.push(`${targetName} package.json error: ${pkgRes.error}`)
  }
  if (missingInDeps.length > 0) {
    issues.push(`${targetName} manifest drift: ${missingInDeps.length} bundle(s) missing in dependencies (${missingInDeps.join(', ')})`)
  }
  if (unregisteredDeps.length > 0) {
    issues.push(`${targetName} manifest drift: ${unregisteredDeps.length} plugin dep(s) not listed in bundles (${unregisteredDeps.join(', ')})`)
  }
  if (patch.exists && !patch.syntaxOk) {
    issues.push(`${targetName} cordis.patch.yml warning: ${patch.error}`)
  }

  return {
    target: targetName,
    profileDir,
    exists: true,
    packageJsonOk: pkgRes.ok,
    dependenciesCount: depNames.length,
    bundlesCount: bundles.length,
    missingInDeps,
    unregisteredDeps,
    patch,
    issues,
  }
}

/**
 * Strictly masked credential readiness check: NEVER reads, slices, or returns secret values.
 */
export function inspectCredentialKeysMasked(dshHome, referencedEnvKeys = []) {
  const credPath = join(dshHome, '.credentials.yaml')
  const nonEmptyKeys = new Set()
  let totalKeysCount = 0
  let fileExists = false

  if (existsSync(credPath)) {
    fileExists = true
    try {
      const raw = readFileSync(credPath, 'utf8')
      for (const rawLine of raw.split(/\r?\n/)) {
        const line = rawLine.trimEnd()
        if (!line || line.trim().startsWith('#') || /^\s/.test(line)) continue
        const m = line.match(/^([A-Za-z0-9_.-]+)\s*:\s*(.*)$/)
        if (!m) continue
        totalKeysCount += 1
        const keyName = m[1].trim()
        const rawVal = m[2].trim()
        const stripped = rawVal.replace(/^(['"])(.*)\1$/, '$2').trim()
        const isEmpty = !stripped || stripped === '~' || stripped.toLowerCase() === 'null'
        if (!isEmpty) {
          nonEmptyKeys.add(keyName)
        }
      }
    } catch {
      // Unreadable file stays nonEmptyKeys empty; never leak raw error internals with secrets.
    }
  }

  const uniqueRefs = Array.from(new Set(referencedEnvKeys.filter(Boolean))).sort()
  const referencedKeys = uniqueRefs.map((key) => ({
    key,
    ready: nonEmptyKeys.has(key),
  }))
  const missingReferencedKeys = referencedKeys.filter((item) => !item.ready).map((item) => item.key)

  return {
    fileExists,
    totalKeysCount,
    nonEmptyKeysCount: nonEmptyKeys.size,
    referencedKeys,
    missingReferencedKeys,
  }
}

/**
 * Full read-only health report across Web & Desktop profiles, masked credentials, and DSHGuard.
 */
export function inspectHealth(options = {}) {
  const dshHome = resolveDshHome(options.dshHome)
  const target = options.target && ['all', 'web', 'desktop'].includes(options.target)
    ? options.target
    : 'all'

  const profiles = []
  if (target === 'all' || target === 'web') {
    profiles.push(inspectProfile(join(dshHome, 'profiles', 'web'), 'web'))
  }
  if (target === 'all' || target === 'desktop') {
    profiles.push(inspectProfile(join(dshHome, 'profiles', 'desktop'), 'desktop'))
  }

  const referencedEnvKeys = profiles.flatMap((p) => (p.patch ? p.patch.apiKeyEnvKeys : []))
  const credentials = inspectCredentialKeysMasked(dshHome, referencedEnvKeys)
  const guard = locateGuardInstallation(options)

  const issues = profiles.flatMap((p) => p.issues)
  if (credentials.missingReferencedKeys.length > 0) {
    issues.push(`Missing credential key(s) in .credentials.yaml: ${credentials.missingReferencedKeys.join(', ')}`)
  }

  const healthy = issues.length === 0

  return {
    dshHome,
    target,
    healthy,
    issuesCount: issues.length,
    issues,
    profiles,
    credentials,
    guard,
  }
}

function classifySnapshotEntry(dirPath, name) {
  let target = 'web'
  let reason = ''
  let itemCount = 0
  let createdAt = ''

  const manifestPath = join(dirPath, 'manifest.json')
  if (existsSync(manifestPath)) {
    const res = readJsonSafe(manifestPath)
    if (res.ok && res.data) {
      if (typeof res.data.target === 'string' && res.data.target.toLowerCase().includes('desktop')) {
        target = 'desktop'
      } else if (typeof res.data.scope === 'string' && res.data.scope.toLowerCase().includes('desktop')) {
        target = 'desktop'
      }
      if (typeof res.data.reason === 'string') reason = res.data.reason
      if (typeof res.data.createdAt === 'string') createdAt = res.data.createdAt
      if (Array.isArray(res.data.files)) itemCount = res.data.files.length
    }
  }

  if (target === 'web' && /desktop/i.test(name)) {
    target = 'desktop'
  }

  try {
    const entries = readdirSync(dirPath)
    if (itemCount === 0) {
      itemCount = entries.filter((e) => e !== 'manifest.json').length
    }
    if (!createdAt) {
      createdAt = new Date(statSync(dirPath).mtimeMs).toISOString()
    }
  } catch {
    // Ignore stat errors on individual directories
  }

  return {
    id: name,
    target,
    reason,
    itemCount,
    createdAt,
  }
}

/**
 * List saved DSHGuard snapshots (strictly read-only).
 */
export function inspectSnapshots(options = {}) {
  const guard = locateGuardInstallation(options)
  const limit = Math.max(1, Math.min(50, Number(options.limit) || 20))
  const targetFilter = options.target && ['all', 'web', 'desktop'].includes(options.target)
    ? options.target
    : 'all'

  if (!guard.snapshotsDir || !existsSync(guard.snapshotsDir)) {
    return {
      snapshotsDir: guard.snapshotsDir,
      guardInstalled: guard.installed,
      total: 0,
      snapshots: [],
      releaseUrl: RELEASE_URL,
    }
  }

  let items = []
  try {
    const entries = readdirSync(guard.snapshotsDir, { withFileTypes: true })
    for (const ent of entries) {
      if (!ent.isDirectory()) continue
      const full = join(guard.snapshotsDir, ent.name)
      const parsed = classifySnapshotEntry(full, ent.name)
      if (targetFilter === 'all' || parsed.target === targetFilter) {
        items.push(parsed)
      }
    }
  } catch {
    items = []
  }

  items.sort((a, b) => String(b.createdAt).localeCompare(String(a.createdAt)))

  return {
    snapshotsDir: guard.snapshotsDir,
    guardInstalled: guard.installed,
    total: items.length,
    snapshots: items.slice(0, limit),
    releaseUrl: RELEASE_URL,
  }
}

/**
 * Locate or optionally launch the local Windows DSHGuard desktop supervisor.
 */
export function openGuard(options = {}) {
  const guard = locateGuardInstallation(options)
  if (!guard.installed || !guard.exePath) {
    return {
      installed: false,
      launched: false,
      exePath: null,
      guardHome: guard.guardHome,
      releaseUrl: RELEASE_URL,
      message: '未检测到本地安装的 DSHGuard 守护壳，可前往 GitHub Releases 下载官方 Windows 安装包。',
    }
  }

  if (options.launch === true) {
    if (process.platform !== 'win32') {
      return {
        installed: true,
        launched: false,
        exePath: guard.exePath,
        guardHome: guard.guardHome,
        releaseUrl: RELEASE_URL,
        message: '当前非 Windows 环境，无法直接拉起桌面端 DSHGuard.exe。',
      }
    }
    const child = spawn(guard.exePath, [], {
      detached: true,
      stdio: 'ignore',
      windowsHide: false,
    })
    child.unref()
    return {
      installed: true,
      launched: true,
      exePath: guard.exePath,
      guardHome: guard.guardHome,
      releaseUrl: RELEASE_URL,
      message: `已在桌面拉起 DSHGuard 守护壳（${basename(guard.exePath)}）。`,
    }
  }

  return {
    installed: true,
    launched: false,
    exePath: guard.exePath,
    guardHome: guard.guardHome,
    releaseUrl: RELEASE_URL,
    message: `已定位本机 DSHGuard 守护壳：${guard.exePath}`,
  }
}

export function formatHealthText(report) {
  const lines = []
  lines.push(`DSH 守护壳伴生体检（状态：${report.healthy ? '正常' : `发现 ${report.issuesCount} 项异常`}）`)
  for (const p of report.profiles) {
    if (!p.exists) {
      lines.push(`- [${p.target}] 未检测到配置目录`)
      continue
    }
    const patchNote = p.patch.exists
      ? (p.patch.syntaxOk ? `patch 正常（禁用 ${p.patch.disabledCount} 项）` : `patch 异常（${p.patch.error}）`)
      : '无 cordis.patch.yml'
    lines.push(
      `- [${p.target}] bundles=${p.bundlesCount} / deps=${p.dependenciesCount} | ${patchNote}`
      + (p.missingInDeps.length ? ` | 缺依赖: ${p.missingInDeps.join(', ')}` : '')
      + (p.unregisteredDeps.length ? ` | 未登记: ${p.unregisteredDeps.join(', ')}` : '')
    )
  }
  const cred = report.credentials
  const refTotal = cred.referencedKeys.length
  const refReady = cred.referencedKeys.filter((r) => r.ready).length
  lines.push(
    `- [凭据脱敏对账] .credentials.yaml ${cred.fileExists ? `已存在（非空键 ${cred.nonEmptyKeysCount}/${cred.totalKeysCount}）` : '未创建'}`
    + (refTotal > 0 ? ` | patch 引用键就绪 ${refReady}/${refTotal}` : '')
    + (cred.missingReferencedKeys.length ? ` | 缺失: ${cred.missingReferencedKeys.join(', ')}` : '')
  )
  lines.push(
    report.guard.installed
      ? `- [本机 DSHGuard] 已安装（${report.guard.exePath}）`
      : `- [本机 DSHGuard] 未安装，官方下载：${report.guard.releaseUrl}`
  )
  if (report.issues.length > 0) {
    lines.push('诊断建议：')
    for (const issue of report.issues) {
      lines.push(`  ! ${issue}`)
    }
  }
  return lines.join('\n')
}

export function formatSnapshotsText(report) {
  if (!report.snapshotsDir || report.total === 0) {
    return report.guardInstalled
      ? '本机 DSHGuard 尚无快照存档（Snapshots 目录为空或不存在）。'
      : `未定位到本机 DSHGuard 快照目录；可在 Windows 安装 DSHGuard 后自动存档：${report.releaseUrl}`
  }
  const lines = [`已找到 ${report.total} 份守护壳快照（展示最近 ${report.snapshots.length} 份）：`]
  for (const snap of report.snapshots) {
    const meta = [snap.target, `${snap.itemCount} 项`, snap.createdAt].filter(Boolean).join(' · ')
    lines.push(`- ${snap.id}（${meta}）${snap.reason ? ` — ${snap.reason}` : ''}`)
  }
  return lines.join('\n')
}
