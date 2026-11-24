import {
  BUILTIN_SCENES,
  INSTALLER_URL,
  PACKAGE_VERSION,
  RELEASE_URL,
  REPO_URL,
  SYNC_SECTIONS,
  applyScene,
  createSnapshot,
  deleteCustomScene,
  detectActiveProfile,
  discoverProfiles,
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
  inspectSkills,
  inspectSnapshots,
  inspectStorageHygiene,
  listRecycleBatches,
  listScenes,
  loadCustomScenes,
  locateGuardInstallation,
  normalizeProfileTarget,
  openGuard,
  pinSnapshot,
  planStorageCleanup,
  previewSceneApply,
  probeMcpServers,
  readAuditLog,
  redactLogLine,
  repairBrokenModuleLinks,
  resolveDshHome,
  restoreRecycleBatch,
  restoreSnapshotConfig,
  saveCustomScene,
  syncProfileSections,
  togglePatchEntry,
  toggleSkillVisibility,
  updateMcpTimeout,
  upsertCredentialKey,
  validateProfileName,
} from './inspector.mjs'

let defineTool = (def) => def
try {
  const mod = await import('@deepseek-ai/dsh-tools')
  if (mod && typeof mod.defineTool === 'function') {
    defineTool = mod.defineTool
  }
} catch {
  // Standalone CLI / unit-test execution without @deepseek-ai/dsh-tools falls back cleanly.
}

const name = 'dsh-guard'
const API_BASE = '/api/dsh-guard'
const MAX_JSON_BYTES = 32768

function sendJson(res, status, value) {
  const body = JSON.stringify(value)
  res.statusCode = status
  res.setHeader('content-type', 'application/json; charset=utf-8')
  res.setHeader('cache-control', 'no-store')
  res.end(body)
}

function isRequestAllowed(ctx, req, res) {
  try {
    const connection = typeof ctx.get === 'function' ? ctx.get('connection') : null
    if (connection && typeof connection.requestRejection === 'function') {
      const rejection = connection.requestRejection({ headers: (req && req.headers) || {} })
      if (rejection) {
        sendJson(res, rejection, {
          ok: false,
          error: rejection === 401 ? 'unauthorized' : 'forbidden',
        })
        return false
      }
    }
  } catch {
    // Fallback to permissive when connection service is unavailable in local unit mocks.
  }
  return true
}

async function readJsonBody(req, maxBytes = MAX_JSON_BYTES) {
  if (req && req.body && typeof req.body === 'object' && !Buffer.isBuffer(req.body)) {
    return req.body
  }
  if (!req || typeof req.on !== 'function') {
    return {}
  }
  return await new Promise((resolve, reject) => {
    const chunks = []
    let total = 0
    let finished = false
    req.on('data', (chunk) => {
      if (finished) return
      const buf = Buffer.isBuffer(chunk) ? chunk : Buffer.from(String(chunk), 'utf8')
      total += buf.length
      if (total > maxBytes) {
        finished = true
        reject(Object.assign(new Error('payload_too_large'), { status: 413 }))
        return
      }
      chunks.push(buf)
    })
    req.on('end', () => {
      if (finished) return
      finished = true
      const raw = Buffer.concat(chunks).toString('utf8').trim()
      if (!raw) {
        resolve({})
        return
      }
      try {
        const parsed = JSON.parse(raw)
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
          reject(Object.assign(new Error('invalid_json_object'), { status: 400 }))
          return
        }
        resolve(parsed)
      } catch {
        reject(Object.assign(new Error('invalid_json'), { status: 400 }))
      }
    })
    req.on('error', (err) => {
      if (finished) return
      finished = true
      reject(err)
    })
  })
}

function makeGetRoute(ctx, routePath, handler) {
  return {
    path: `${API_BASE}${routePath}`,
    handler: async (req, res) => {
      if (!isRequestAllowed(ctx, req, res)) return
      const method = String((req && req.method) || 'GET').toUpperCase()
      if (method !== 'GET' && method !== 'HEAD') {
        sendJson(res, 405, { ok: false, error: 'method_not_allowed' })
        return
      }
      try {
        const parsedUrl = new URL(String((req && req.url) || routePath), 'http://127.0.0.1')
        const result = await handler(parsedUrl.searchParams, req)
        if (result && result.ok === false) {
          sendJson(res, 400, result)
          return
        }
        sendJson(res, 200, { ok: true, ...(result || {}) })
      } catch (err) {
        sendJson(res, 500, {
          ok: false,
          error: err && err.message ? String(err.message) : 'internal_error',
        })
      }
    },
  }
}

function makePostRoute(ctx, routePath, handler) {
  return {
    path: `${API_BASE}${routePath}`,
    handler: async (req, res) => {
      if (!isRequestAllowed(ctx, req, res)) return
      const method = String((req && req.method) || '').toUpperCase()
      if (method !== 'POST') {
        sendJson(res, 405, { ok: false, error: 'method_not_allowed' })
        return
      }
      let body
      try {
        body = await readJsonBody(req)
      } catch (err) {
        sendJson(res, err && err.status ? err.status : 400, {
          ok: false,
          error: err && err.message ? String(err.message) : 'invalid_request_body',
        })
        return
      }
      try {
        const result = await handler(body)
        if (result && result.ok === false) {
          sendJson(res, 400, result)
          return
        }
        sendJson(res, 200, { ok: true, ...(result || {}) })
      } catch (err) {
        sendJson(res, 500, {
          ok: false,
          error: err && err.message ? String(err.message) : 'internal_error',
        })
      }
    },
  }
}

function resolveRouteTarget(rawTarget, defaultProfile) {
  if (rawTarget === undefined || rawTarget === null || rawTarget === '' || rawTarget === 'all') {
    return defaultProfile
  }
  return normalizeProfileTarget(String(rawTarget), null)
}

function buildRouteTable(ctx, activeProfile = detectActiveProfile({ ctx })) {
  const hostProfile = normalizeProfileTarget(activeProfile) || 'web'
  return [
    {
      path: `${API_BASE}/overview`,
      handler: async (req, res) => {
        if (!isRequestAllowed(ctx, req, res)) return
        const method = String((req && req.method) || 'GET').toUpperCase()
        if (method !== 'GET' && method !== 'HEAD') {
          sendJson(res, 405, { ok: false, error: 'method_not_allowed' })
          return
        }
        try {
          // Server-side env vars only (DSH_HOME / DSHGUARD_HOME); never accept path params from HTTP requests.
          const dshHome = resolveDshHome()
          const health = inspectHealth({ dshHome, activeProfile: hostProfile, ctx })
          const snapshots = inspectSnapshots({ dshHome, target: hostProfile, limit: 25 })
          const customScenes = loadCustomScenes(dshHome)
          const radar = inspectLogRadar(dshHome, { hours: 24, maxGroups: 5 })
          sendJson(res, 200, {
            ok: true,
            version: PACKAGE_VERSION,
            activeProfile: health.activeProfile,
            platform: process.platform,
            installerUrl: INSTALLER_URL,
            releaseUrl: RELEASE_URL,
            repoUrl: REPO_URL,
            sceneCount: BUILTIN_SCENES.length + customScenes.length,
            logRadarSummary: {
              hoursWindow: radar.hoursWindow,
              scannedFiles: radar.scannedFiles,
              errorCount: radar.errorCount,
              warnCount: radar.warnCount,
              uniqueCount: radar.uniqueCount,
            },
            health,
            snapshots,
            checkedAt: Date.now(),
          })
        } catch (err) {
          sendJson(res, 500, {
            ok: false,
            error: err && err.message ? String(err.message) : 'internal_error',
          })
        }
      },
    },

    makePostRoute(ctx, '/open', async () => {
      const res = openGuard({ launch: true })
      return {
        ok: true,
        platform: process.platform,
        ...res,
      }
    }),

    makePostRoute(ctx, '/snapshots/create', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const reason = typeof body.reason === 'string' ? body.reason.slice(0, 120) : ''
      return createSnapshot(resolveDshHome(), target, 'manual', reason)
    }),

    makePostRoute(ctx, '/snapshots/restore-config', async (body) => {
      const snapshotId = typeof body.snapshotId === 'string' ? body.snapshotId.trim() : ''
      return restoreSnapshotConfig(resolveDshHome(), locateGuardInstallation(), snapshotId)
    }),

    makePostRoute(ctx, '/snapshots/pin', async (body) => {
      const snapshotId = typeof body.snapshotId === 'string' ? body.snapshotId.trim() : ''
      const pinned = Boolean(body.pinned)
      const pinLabel = typeof body.pinLabel === 'string' ? body.pinLabel : ''
      return pinSnapshot(resolveDshHome(), snapshotId, pinned, pinLabel)
    }),

    makePostRoute(ctx, '/toggle-entry', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const id = typeof body.id === 'string' ? body.id.trim() : ''
      const disabled = Boolean(body.disabled)
      return togglePatchEntry(resolveDshHome(), target, id, disabled, locateGuardInstallation())
    }),

    makePostRoute(ctx, '/repair-links', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      return repairBrokenModuleLinks(resolveDshHome(), target)
    }),

    makePostRoute(ctx, '/mcp/probe', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const health = inspectHealth({ activeProfile: target, ctx })
      return await probeMcpServers(resolveDshHome(), health.extensions, target)
    }),

    makePostRoute(ctx, '/mcp/timeout', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const id = typeof body.id === 'string' ? body.id.trim() : ''
      return updateMcpTimeout(resolveDshHome(), target, id, body.timeoutMs, locateGuardInstallation())
    }),

    makePostRoute(ctx, '/skills/toggle', async (body) => {
      const skillId = typeof body.skillId === 'string' ? body.skillId.trim() : ''
      const changes = {}
      if (typeof body.modelVisible === 'boolean') changes.modelVisible = body.modelVisible
      if (typeof body.userInvocable === 'boolean') changes.userInvocable = body.userInvocable
      const expectedFingerprint = typeof body.fingerprint === 'string' ? body.fingerprint : undefined
      return toggleSkillVisibility(resolveDshHome(), skillId, changes, { expectedFingerprint, activeProfile: hostProfile })
    }),

    makePostRoute(ctx, '/credential-key', async (body) => {
      const key = typeof body.key === 'string' ? body.key : ''
      const value = typeof body.value === 'string' ? body.value : ''
      const res = upsertCredentialKey(resolveDshHome(), key, value)
      if (!res.ok) return res
      // Strict one-way write: never echo secret value or partial characters back over HTTP.
      return {
        ok: true,
        key: res.key,
        path: res.path,
        created: Boolean(res.created),
      }
    }),

    makeGetRoute(ctx, '/scenes', async (searchParams) => {
      const target = resolveRouteTarget(searchParams.get('target'), hostProfile) || hostProfile
      return listScenes(resolveDshHome(), { target, guard: locateGuardInstallation(), ctx })
    }),

    makePostRoute(ctx, '/scenes/preview', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const sceneId = typeof body.sceneId === 'string' ? body.sceneId.trim() : ''
      return previewSceneApply(resolveDshHome(), sceneId, { target, guard: locateGuardInstallation(), ctx })
    }),

    makePostRoute(ctx, '/scenes/apply', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      const sceneId = typeof body.sceneId === 'string' ? body.sceneId.trim() : ''
      return applyScene(resolveDshHome(), sceneId, {
        target,
        dryRun: body.dryRun === true,
        guard: locateGuardInstallation(),
        ctx,
      })
    }),

    makePostRoute(ctx, '/scenes/save', async (body) => {
      const target = resolveRouteTarget(body.target, hostProfile)
      if (!target) return { ok: false, error: '仅支持 web 或 desktop 档案' }
      return saveCustomScene(resolveDshHome(), body, { target, guard: locateGuardInstallation(), ctx })
    }),

    makePostRoute(ctx, '/scenes/delete', async (body) => {
      const sceneId = typeof body.sceneId === 'string' ? body.sceneId.trim() : ''
      return deleteCustomScene(resolveDshHome(), sceneId)
    }),

    makeGetRoute(ctx, '/profile-diff', async (searchParams) => {
      const source = resolveRouteTarget(searchParams.get('source'), hostProfile)
      const defaultOther = source === 'desktop' ? 'web' : 'desktop'
      const target = resolveRouteTarget(searchParams.get('target'), defaultOther)
      const rawSections = String(searchParams.get('sections') || '')
        .split(',')
        .map((s) => s.trim())
        .filter((s) => SYNC_SECTIONS.includes(s))
      return inspectProfileDiff(resolveDshHome(), {
        source,
        target,
        sections: rawSections.length > 0 ? rawSections : undefined,
        ctx,
      })
    }),

    makePostRoute(ctx, '/profile-sync', async (body) => {
      const source = normalizeProfileTarget(body.source, null)
      const target = normalizeProfileTarget(body.target, null)
      if (!source || !target) return { ok: false, error: 'source 与 target 仅支持 web 或 desktop' }
      const sections = Array.isArray(body.sections)
        ? body.sections.filter((s) => SYNC_SECTIONS.includes(s))
        : undefined
      const keys = Array.isArray(body.keys)
        ? body.keys.map((k) => String(k || '').trim()).filter(Boolean)
        : undefined
      return syncProfileSections(resolveDshHome(), {
        source,
        target,
        sections,
        keys,
        dryRun: body.dryRun === true,
        ctx,
      })
    }),

    makeGetRoute(ctx, '/storage', async () => {
      return inspectStorageHygiene(resolveDshHome())
    }),

    makePostRoute(ctx, '/storage/recycle', async (body) => {
      const buckets = Array.isArray(body.buckets)
        ? body.buckets.map((b) => String(b || '').trim()).filter(Boolean)
        : undefined
      const minAgeDays = body.minAgeDays !== undefined ? Number(body.minAgeDays) : 0
      return executeStorageCleanup(resolveDshHome(), {
        buckets,
        minAgeDays,
        dryRun: body.dryRun === true,
      })
    }),

    makePostRoute(ctx, '/storage/restore', async (body) => {
      const batchId = typeof body.batchId === 'string' ? body.batchId.trim() : ''
      return restoreRecycleBatch(resolveDshHome(), batchId, {
        overwrite: body.overwrite === true,
      })
    }),

    makeGetRoute(ctx, '/logs/radar', async (searchParams) => {
      const hours = searchParams.get('hours') ? Number(searchParams.get('hours')) : 168
      const maxGroups = searchParams.get('maxGroups') ? Number(searchParams.get('maxGroups')) : 25
      return inspectLogRadar(resolveDshHome(), { hours, maxGroups })
    }),
  ]
}

function buildToolDefinitions(ctx, activeProfile = detectActiveProfile({ ctx })) {
  const hostProfile = normalizeProfileTarget(activeProfile) || 'web'
  return [
    defineTool({
      name: 'dsh_guard_status',
      description:
        'Check the active DSH profile (Desktop or Web) in-process: checks package.json dependency drift, broken module links, '
        + 'cordis.patch.yml plugins/MCP/llm-pi-ai readiness, masked .credentials.yaml key readiness (never exposes secret values), '
        + 'startup context load summary, and local snapshot availability.',
      parameters: {
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Optional profile track override ("web" or "desktop"). Defaults to the active runtime track.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            healthy: { type: 'boolean', required: true },
            issuesCount: { type: 'integer', required: true },
            issues: { type: 'array', required: true, items: { type: 'string' } },
            summary: { type: 'string', required: true },
            releaseUrl: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const t = normalizeProfileTarget(args.target) || hostProfile
        const report = inspectHealth({ activeProfile: t, ctx })
        return {
          healthy: report.healthy,
          issuesCount: report.issuesCount,
          issues: report.issues,
          summary: formatHealthText(report),
          releaseUrl: RELEASE_URL,
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_context_audit',
      description:
        'Read-only L0 audit of startup context footprint for the active DSH profile: counts active MCP servers, '
        + 'model-visible Skills (~/.dsh/skills, ~/.agents/skills, plugin skills), and permanent prompt file sizes.',
      parameters: {
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Optional profile override ("web" or "desktop"). Defaults to the active runtime track.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            level: { type: 'string', required: true },
            estimatedBaseTokens: { type: 'integer', required: true },
            recommendations: { type: 'array', required: true, items: { type: 'string' } },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const dshHome = resolveDshHome()
        const t = normalizeProfileTarget(args.target) || hostProfile
        const health = inspectHealth({ dshHome, activeProfile: t, ctx })
        const audit = inspectContextAudit(dshHome, health, t)
        const p = audit.profile || audit.profiles[0] || { level: 'light', estimatedBaseTokens: 0 }
        return {
          level: p.level,
          estimatedBaseTokens: p.estimatedBaseTokens,
          recommendations: audit.recommendations,
          summary: formatContextAuditText(audit),
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_mcp_probe',
      description:
        'Read-only L0 probe for configured MCP servers on the active DSH profile: checks loopback TCP reachability '
        + '(localhost/127.0.0.1/::1 only, SSRF-safe) for HTTP/SSE servers and verifies executable/script paths on disk for stdio servers.',
      parameters: {
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Optional profile override ("web" or "desktop"). Defaults to the active runtime track.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            total: { type: 'integer', required: true },
            activeCount: { type: 'integer', required: true },
            unhealthyCount: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const dshHome = resolveDshHome()
        const t = normalizeProfileTarget(args.target) || hostProfile
        const health = inspectHealth({ dshHome, activeProfile: t, ctx })
        const probe = await probeMcpServers(dshHome, health.extensions, t)
        return {
          total: probe.total,
          activeCount: probe.activeCount,
          unhealthyCount: probe.unhealthyCount,
          summary: formatMcpProbeText(probe),
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_snapshots',
      description:
        'List local DSH configuration snapshots for the active profile track with live diff and pin status.',
      parameters: {
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Optional profile filter ("web" or "desktop"). Defaults to the active runtime track.',
        },
        limit: {
          type: 'integer',
          description: 'Maximum snapshots to return (1-50, default: 20).',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            guardInstalled: { type: 'boolean', required: true },
            total: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
            releaseUrl: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const t = normalizeProfileTarget(args.target) || hostProfile
        const report = inspectSnapshots({ target: t, limit: args.limit })
        return {
          guardInstalled: report.guardInstalled,
          total: report.total,
          summary: formatSnapshotsText(report),
          releaseUrl: report.releaseUrl,
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_backup',
      description:
        'Create a safe configuration snapshot (cordis.patch.yml, cordis.yml, package.json, settings.yaml; never copies .credentials.yaml) directly inside the companion plugin.',
      parameters: {
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Which standard profile config files to back up ("web" or "desktop"). Defaults to active runtime track.',
        },
        reason: {
          type: 'string',
          description: 'Short label for this manual backup snapshot.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            ok: { type: 'boolean', required: true },
            snapshotId: { type: 'string', required: true },
            itemCount: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const rawTarget = typeof args.target === 'string' ? args.target.trim() : ''
        const target = rawTarget ? normalizeProfileTarget(rawTarget) : hostProfile
        if (!target) {
          return { ok: false, snapshotId: '', itemCount: 0, summary: '仅支持 web 或 desktop 标准档案，无法创建快照。' }
        }
        const res = createSnapshot(resolveDshHome(), target, 'manual', args.reason || 'Agent 手动配置备份')
        return {
          ok: Boolean(res.ok),
          snapshotId: res.snapshot.id,
          itemCount: res.snapshot.itemCount,
          summary: `已生成配置快照 ${res.snapshot.id}（${res.snapshot.target} · ${res.snapshot.itemCount} 个配置文件，已自动排除 .credentials.yaml）。`,
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_open',
      description:
        'Locate or launch the optional Windows out-of-band DSHGuard desktop supervisor when process isolation, Windows DPAPI vault, or running-bundle rollback is needed.',
      parameters: {
        launch: {
          type: 'boolean',
          description: 'Set true to spawn DSHGuard.exe on Windows if installed (default: false).',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            installed: { type: 'boolean', required: true },
            launched: { type: 'boolean', required: true },
            message: { type: 'string', required: true },
            releaseUrl: { type: 'string', required: true },
            installerUrl: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{
          type: 'text',
          text: value.message + (value.installed ? '' : `\n安装包：${value.installerUrl}\n发布页：${value.releaseUrl}`),
        }],
      },
      async execute(args = {}) {
        const res = openGuard({ launch: Boolean(args.launch) })
        return {
          installed: res.installed,
          launched: res.launched,
          message: res.message,
          releaseUrl: res.releaseUrl,
          installerUrl: res.installerUrl || INSTALLER_URL,
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_scenes',
      description:
        'List, dry-run preview, apply, save, or delete work scene loadouts (lean-coding, creative-full, safe-mode, or custom scenes) '
        + 'for the target DSH profile while protecting @deepseek-ai/* core and self.',
      parameters: {
        action: {
          type: 'string',
          enum: ['list', 'preview', 'apply', 'save', 'delete'],
          description: 'Action to execute (default: list).',
        },
        sceneId: {
          type: 'string',
          description: 'Target scene ID (e.g. lean-coding, creative-full, safe-mode, or custom ID).',
        },
        name: {
          type: 'string',
          description: 'Optional display name when saving a custom scene.',
        },
        description: {
          type: 'string',
          description: 'Optional description when saving a custom scene.',
        },
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Optional profile track override ("web" or "desktop"). Defaults to active runtime track.',
        },
        dryRun: {
          type: 'boolean',
          description: 'When action="apply", set true to preview changes without modifying disk.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            ok: { type: 'boolean', required: true },
            action: { type: 'string', required: true },
            target: { type: 'string', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const dshHome = resolveDshHome()
        const t = normalizeProfileTarget(args.target) || hostProfile
        const act = String(args.action || 'list').trim().toLowerCase()
        const guard = locateGuardInstallation()
        if (act === 'preview') {
          const res = previewSceneApply(dshHome, args.sceneId, { target: t, guard, ctx })
          if (!res.ok) return { ok: false, action: act, target: t, summary: res.error || '预览失败' }
          return {
            ok: true,
            action: act,
            target: t,
            summary: `场景「${res.scene.name}」Dry-Run (${t}): 插件变更 ${res.pluginChanges.length}，MCP变更 ${res.mcpChanges.length}，技能变更 ${res.skillChanges.length}`,
          }
        }
        if (act === 'apply') {
          const res = applyScene(dshHome, args.sceneId, { target: t, dryRun: Boolean(args.dryRun), guard, ctx })
          if (!res.ok) return { ok: false, action: act, target: t, summary: res.error || '应用场景失败' }
          return {
            ok: true,
            action: act,
            target: t,
            summary: res.restartHint || `已应用 ${args.sceneId}`,
          }
        }
        if (act === 'save') {
          const res = saveCustomScene(
            dshHome,
            { id: args.sceneId, name: args.name, description: args.description, captureCurrent: true },
            { target: t, guard, ctx },
          )
          return {
            ok: Boolean(res.ok),
            action: act,
            target: t,
            summary: res.ok ? `已保存自定义场景 ${res.scene.name} (${res.scene.id})` : (res.error || '保存失败'),
          }
        }
        if (act === 'delete') {
          const res = deleteCustomScene(dshHome, args.sceneId)
          return {
            ok: Boolean(res.ok),
            action: act,
            target: t,
            summary: res.ok ? `已删除自定义场景 ${res.deletedId}` : (res.error || '删除失败'),
          }
        }
        const list = listScenes(dshHome, { target: t, guard, ctx })
        return { ok: Boolean(list.ok), action: 'list', target: t, summary: formatScenesText(list) }
      },
    }),

    defineTool({
      name: 'dsh_guard_profile_diff',
      description:
        'On-demand explicit comparison between "web" and "desktop" profiles for llm-providers, mcp-servers, and permissions sections.',
      parameters: {
        source: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Source profile ("web" or "desktop"). Defaults to active track.',
        },
        target: {
          type: 'string',
          enum: ['web', 'desktop'],
          description: 'Target profile to compare against. Defaults to the opposite track.',
        },
        sections: {
          type: 'array',
          items: { type: 'string' },
          description: 'Optional filter sections from llm-providers, mcp-servers, permissions.',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            inSync: { type: 'boolean', required: true },
            totalDiffs: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const res = inspectProfileDiff(resolveDshHome(), {
          source: args.source || hostProfile,
          target: args.target,
          sections: Array.isArray(args.sections) ? args.sections : undefined,
          ctx,
        })
        if (!res.ok) return { inSync: false, totalDiffs: 0, summary: res.error || '双端比对失败' }
        return {
          inSync: Boolean(res.inSync),
          totalDiffs: Number(res.totalDiffs) || 0,
          summary: formatProfileDiffText(res),
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_storage_audit',
      description:
        'Audit ~/.dsh storage footprint by bucket (cache, profile backups, attachments, sessions, snapshots), '
        + 'run safe dry-run recycle planning, move non-credential items to guard-recycle, or restore a recycle batch.',
      parameters: {
        action: {
          type: 'string',
          enum: ['inspect', 'dry-run', 'recycle', 'restore'],
          description: 'Operation mode (default: inspect).',
        },
        buckets: {
          type: 'array',
          items: { type: 'string' },
          description: 'Target buckets for dry-run/recycle (defaults to cache and profile-bak; credential backups are always skipped).',
        },
        minAgeDays: {
          type: 'integer',
          description: 'Optional minimum age in days before items qualify for cleanup.',
        },
        batchId: {
          type: 'string',
          description: 'Recycle batch ID when action="restore".',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            ok: { type: 'boolean', required: true },
            totalBytes: { type: 'integer', required: true },
            reclaimableBytes: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const dshHome = resolveDshHome()
        const act = String(args.action || 'inspect').trim().toLowerCase()
        if (act === 'dry-run' || act === 'recycle') {
          const res = executeStorageCleanup(dshHome, {
            buckets: Array.isArray(args.buckets) ? args.buckets : undefined,
            minAgeDays: args.minAgeDays,
            dryRun: act === 'dry-run',
          })
          return {
            ok: Boolean(res.ok),
            totalBytes: Number(res.movedBytes ?? res.totalBytes ?? 0),
            reclaimableBytes: Number(res.movedBytes ?? res.totalBytes ?? 0),
            summary: res.ok
              ? (act === 'dry-run'
                  ? `Dry-Run 预计回收 ${res.itemCount} 文件 (${res.humanTotalSize})，已跳过敏感备份 ${res.skippedSensitiveCount} 项`
                  : res.message)
              : (res.error || '存储清理失败'),
          }
        }
        if (act === 'restore') {
          const res = restoreRecycleBatch(dshHome, args.batchId)
          return {
            ok: Boolean(res.ok),
            totalBytes: Number(res.restoredBytes || 0),
            reclaimableBytes: 0,
            summary: res.ok ? res.message : (res.error || '还原失败'),
          }
        }
        const report = inspectStorageHygiene(dshHome)
        return {
          ok: Boolean(report.ok),
          totalBytes: report.totalBytes,
          reclaimableBytes: report.reclaimableBytes,
          summary: formatStorageHygieneText(report),
        }
      },
    }),

    defineTool({
      name: 'dsh_guard_log_radar',
      description:
        'Bounded low-token log diagnostic radar scanning recent ~/.dsh/logs and desktop host logs with strict secret & home path redaction.',
      parameters: {
        hours: {
          type: 'integer',
          description: 'Lookback time window in hours (1-720, default: 168).',
        },
        maxGroups: {
          type: 'integer',
          description: 'Maximum deduplicated error/warn signatures to return (1-100, default: 25).',
        },
      },
      output: {
        schema: {
          type: 'object',
          additionalProperties: false,
          properties: {
            errorCount: { type: 'integer', required: true },
            warnCount: { type: 'integer', required: true },
            uniqueCount: { type: 'integer', required: true },
            scannedFiles: { type: 'integer', required: true },
            summary: { type: 'string', required: true },
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.summary }],
      },
      async execute(args = {}) {
        const report = inspectLogRadar(resolveDshHome(), {
          hours: args.hours,
          maxGroups: args.maxGroups,
        })
        return {
          errorCount: report.errorCount,
          warnCount: report.warnCount,
          uniqueCount: report.uniqueCount,
          scannedFiles: report.scannedFiles,
          summary: formatLogRadarText(report),
        }
      },
    }),
  ]
}

export function apply(ctx) {
  let activeProfile = 'web'
  try {
    activeProfile = detectActiveProfile({ ctx })
  } catch {
    activeProfile = detectActiveProfile()
  }

  ctx.inject(['tools'], (toolsCtx) => {
    const definitions = buildToolDefinitions(ctx, activeProfile)
    const disposers = definitions.map((def) => toolsCtx.tools.register(def))
    toolsCtx.effect(() => () => {
      for (const dispose of disposers) {
        if (typeof dispose === 'function') dispose()
      }
    })
  })

  ctx.inject(['commands'], (cmdCtx) => {
    const dispose = cmdCtx.commands.register({
      name: 'guard',
      description: 'DSH 守护伴生命令：/guard [status|context|mcp|scenes|diff|storage|logs|snapshots|backup|open]',
      input: { hint: '[status|context|mcp|scenes|diff|storage|logs|snapshots|backup|open]' },
      async handler(invocation) {
        const raw = String((invocation && invocation.rawInput) || '').trim()
        const parts = raw ? raw.split(/\s+/) : []
        const sub = (parts[0] || '').toLowerCase()
        if (sub === 'context' || sub === 'ctx') {
          const dshHome = resolveDshHome()
          const target = normalizeProfileTarget(parts[1]) || activeProfile
          const health = inspectHealth({ dshHome, activeProfile: target, ctx })
          return { kind: 'success', text: formatContextAuditText(inspectContextAudit(dshHome, health, target)) }
        }
        if (sub === 'mcp' || sub === 'probe') {
          const dshHome = resolveDshHome()
          const target = normalizeProfileTarget(parts[1]) || activeProfile
          const health = inspectHealth({ dshHome, activeProfile: target, ctx })
          const probe = await probeMcpServers(dshHome, health.extensions, target)
          return { kind: 'success', text: formatMcpProbeText(probe) }
        }
        if (sub === 'scenes' || sub === 'scene') {
          const dshHome = resolveDshHome()
          const act = (parts[1] || '').toLowerCase()
          if (act === 'apply' && parts[2]) {
            const res = applyScene(dshHome, parts[2], { target: normalizeProfileTarget(parts[3]) || activeProfile, ctx })
            return { kind: res.ok ? 'success' : 'error', text: res.ok ? (res.restartHint || `已应用 ${parts[2]}`) : res.error }
          }
          if (act === 'preview' && parts[2]) {
            const res = previewSceneApply(dshHome, parts[2], { target: normalizeProfileTarget(parts[3]) || activeProfile, ctx })
            return {
              kind: res.ok ? 'success' : 'error',
              text: res.ok
                ? `场景「${res.scene.name}」Dry-Run (${res.target}): 插件 ${res.pluginChanges.length}，MCP ${res.mcpChanges.length}，技能 ${res.skillChanges.length}`
                : res.error,
            }
          }
          const target = normalizeProfileTarget(parts[1]) || activeProfile
          return { kind: 'success', text: formatScenesText(listScenes(dshHome, { target, ctx })) }
        }
        if (sub === 'diff') {
          const src = normalizeProfileTarget(parts[1]) || activeProfile
          const dst = normalizeProfileTarget(parts[2]) || (src === 'desktop' ? 'web' : 'desktop')
          return { kind: 'success', text: formatProfileDiffText(inspectProfileDiff(resolveDshHome(), { source: src, target: dst, ctx })) }
        }
        if (sub === 'storage' || sub === 'disk') {
          return { kind: 'success', text: formatStorageHygieneText(inspectStorageHygiene(resolveDshHome())) }
        }
        if (sub === 'logs' || sub === 'radar') {
          const hours = parts[1] && Number.isFinite(Number(parts[1])) ? Number(parts[1]) : 168
          return { kind: 'success', text: formatLogRadarText(inspectLogRadar(resolveDshHome(), { hours })) }
        }
        if (sub === 'snapshots' || sub === 'snap') {
          return { kind: 'success', text: formatSnapshotsText(inspectSnapshots({ target: activeProfile })) }
        }
        if (sub === 'backup') {
          const t = normalizeProfileTarget(parts[1]) || activeProfile
          const res = createSnapshot(resolveDshHome(), t, 'manual', '命令 /guard backup 创建')
          return {
            kind: 'success',
            text: `已创建配置快照 ${res.snapshot.id}（${res.snapshot.target} · ${res.snapshot.itemCount} 个文件，已自动跳过 .credentials.yaml）`,
          }
        }
        if (sub === 'open') {
          const res = openGuard({ launch: true })
          return {
            kind: 'success',
            text: res.message + (res.installed ? '' : `\n安装包：${res.installerUrl || INSTALLER_URL}\n发布页：${res.releaseUrl}`),
          }
        }
        return { kind: 'success', text: formatHealthText(inspectHealth({ activeProfile, ctx })) }
      },
    })
    if (typeof cmdCtx.effect === 'function' && typeof dispose === 'function') {
      cmdCtx.effect(() => dispose, 'dsh-guard: command /guard')
    }
  })

  ctx.inject(['webServer'], (webCtx) => {
    if (!webCtx || !webCtx.webServer || typeof webCtx.webServer.register !== 'function') return
    for (const route of buildRouteTable(ctx, activeProfile)) {
      if (typeof webCtx.effect === 'function') {
        webCtx.effect(() => webCtx.webServer.register(route), `dsh-guard: route ${route.path}`)
      } else {
        webCtx.webServer.register(route)
      }
    }
  })
}

export {
  apply as default,
  name,
  BUILTIN_SCENES,
  SYNC_SECTIONS,
  applyScene,
  createSnapshot,
  deleteCustomScene,
  detectActiveProfile,
  discoverProfiles,
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
  inspectSkills,
  inspectSnapshots,
  inspectStorageHygiene,
  listRecycleBatches,
  listScenes,
  loadCustomScenes,
  locateGuardInstallation,
  normalizeProfileTarget,
  openGuard,
  pinSnapshot,
  planStorageCleanup,
  previewSceneApply,
  probeMcpServers,
  readAuditLog,
  redactLogLine,
  repairBrokenModuleLinks,
  restoreRecycleBatch,
  restoreSnapshotConfig,
  saveCustomScene,
  syncProfileSections,
  togglePatchEntry,
  toggleSkillVisibility,
  updateMcpTimeout,
  upsertCredentialKey,
  validateProfileName,
}
