import {
  INSTALLER_URL,
  PACKAGE_VERSION,
  RELEASE_URL,
  REPO_URL,
  createSnapshot,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  locateGuardInstallation,
  openGuard,
  repairBrokenModuleLinks,
  resolveDshHome,
  restoreSnapshotConfig,
  togglePatchEntry,
  upsertCredentialKey,
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

function buildRouteTable(ctx) {
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
          const health = inspectHealth({ target: 'all' })
          const snapshots = inspectSnapshots({ target: 'all', limit: 25 })
          sendJson(res, 200, {
            ok: true,
            version: PACKAGE_VERSION,
            platform: process.platform,
            installerUrl: INSTALLER_URL,
            releaseUrl: RELEASE_URL,
            repoUrl: REPO_URL,
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
      const target = body.target === 'desktop' || body.target === 'all' ? body.target : 'web'
      const reason = typeof body.reason === 'string' ? body.reason.slice(0, 120) : ''
      return createSnapshot(resolveDshHome(), target, 'manual', reason)
    }),

    makePostRoute(ctx, '/snapshots/restore-config', async (body) => {
      const snapshotId = typeof body.snapshotId === 'string' ? body.snapshotId.trim() : ''
      return restoreSnapshotConfig(resolveDshHome(), locateGuardInstallation(), snapshotId)
    }),

    makePostRoute(ctx, '/toggle-entry', async (body) => {
      const target = body.target === 'desktop' ? 'desktop' : 'web'
      const id = typeof body.id === 'string' ? body.id.trim() : ''
      const disabled = Boolean(body.disabled)
      return togglePatchEntry(resolveDshHome(), target, id, disabled, locateGuardInstallation())
    }),

    makePostRoute(ctx, '/repair-links', async (body) => {
      const target = body.target === 'desktop' ? 'desktop' : 'web'
      return repairBrokenModuleLinks(resolveDshHome(), target)
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
  ]
}

function buildToolDefinitions() {
  return [
    defineTool({
      name: 'dsh_guard_status',
      description:
        'Check DSH Web and Desktop profiles in-process: checks package.json dependency drift, broken module links, '
        + 'cordis.patch.yml plugins/MCP/llm-pi-ai readiness, masked .credentials.yaml key readiness (never exposes secret values), '
        + 'and local snapshot availability.',
      parameters: {
        target: {
          type: 'string',
          enum: ['all', 'web', 'desktop'],
          description: 'Which DSH profile track to inspect (default: all).',
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
        const report = inspectHealth({ target: args.target })
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
      name: 'dsh_guard_snapshots',
      description:
        'List plugin and desktop DSH configuration snapshots stored locally (separated by Web and Desktop tracks, with live file diff status).',
      parameters: {
        target: {
          type: 'string',
          enum: ['all', 'web', 'desktop'],
          description: 'Filter snapshots by track (default: all).',
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
        const report = inspectSnapshots({ target: args.target, limit: args.limit })
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
          enum: ['web', 'desktop', 'all'],
          description: 'Which profile config files to back up (default: web).',
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
        const target = args.target === 'desktop' || args.target === 'all' ? args.target : 'web'
        const res = createSnapshot(resolveDshHome(), target, 'manual', args.reason || 'Agent 手动配置备份')
        return {
          ok: Boolean(res.ok),
          snapshotId: res.snapshot.id,
          itemCount: res.snapshot.itemCount,
          summary: `已生成配置快照 ${res.snapshot.id}（包含 ${res.snapshot.itemCount} 个配置文件，已自动排除 .credentials.yaml）。`,
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
  ]
}

export function apply(ctx) {
  ctx.inject(['tools'], (toolsCtx) => {
    const definitions = buildToolDefinitions()
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
      description: 'DSH 守护伴生命令：/guard [status|snapshots|backup [web|desktop|all]|open]',
      input: { hint: '[status|snapshots|backup|open]' },
      async handler(invocation) {
        const raw = String((invocation && invocation.rawInput) || '').trim()
        const parts = raw ? raw.toLowerCase().split(/\s+/) : []
        const sub = parts[0] || ''
        if (sub === 'snapshots' || sub === 'snap') {
          return { kind: 'success', text: formatSnapshotsText(inspectSnapshots({})) }
        }
        if (sub === 'backup') {
          const t = parts[1] === 'desktop' || parts[1] === 'all' ? parts[1] : 'web'
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
        return { kind: 'success', text: formatHealthText(inspectHealth({})) }
      },
    })
    if (typeof cmdCtx.effect === 'function' && typeof dispose === 'function') {
      cmdCtx.effect(() => dispose, 'dsh-guard: command /guard')
    }
  })

  ctx.inject(['webServer'], (webCtx) => {
    if (!webCtx || !webCtx.webServer || typeof webCtx.webServer.register !== 'function') return
    for (const route of buildRouteTable(ctx)) {
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
  createSnapshot,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  locateGuardInstallation,
  openGuard,
  repairBrokenModuleLinks,
  restoreSnapshotConfig,
  togglePatchEntry,
  upsertCredentialKey,
}
