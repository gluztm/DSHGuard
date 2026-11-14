import {
  RELEASE_URL,
  formatHealthText,
  formatSnapshotsText,
  inspectHealth,
  inspectSnapshots,
  locateGuardInstallation,
  openGuard,
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
          // Server-side env vars only (DSH_HOME / DSHGUARD_HOME); never accept path traversal params from HTTP requests.
          const health = inspectHealth({ target: 'all' })
          const snapshots = inspectSnapshots({ target: 'all', limit: 15 })
          sendJson(res, 200, {
            ok: true,
            platform: process.platform,
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
    {
      path: `${API_BASE}/open`,
      handler: async (req, res) => {
        if (!isRequestAllowed(ctx, req, res)) return
        const method = String((req && req.method) || '').toUpperCase()
        if (method !== 'POST') {
          sendJson(res, 405, { ok: false, error: 'method_not_allowed' })
          return
        }
        try {
          const result = openGuard({ launch: true })
          sendJson(res, 200, {
            ok: true,
            platform: process.platform,
            ...result,
          })
        } catch (err) {
          sendJson(res, 500, {
            ok: false,
            error: err && err.message ? String(err.message) : 'internal_error',
          })
        }
      },
    },
  ]
}

function buildToolDefinitions() {
  return [
    defineTool({
      name: 'dsh_guard_status',
      description:
        'Read-only health check for DSH Web and Desktop profiles: checks package.json bundle/dependency drift, '
        + 'cordis.patch.yml structural integrity, masked .credentials.yaml key readiness (never reads or exposes secret values), '
        + 'and local Windows DSHGuard supervisor installation status.',
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
        'Read-only list of pre-change DSHGuard configuration and plugin snapshots stored on this Windows machine '
        + '(separated by Web and Desktop tracks).',
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
      name: 'dsh_guard_open',
      description:
        'Locate the local Windows out-of-band DSHGuard desktop supervisor executable or return its official release download link. '
        + 'Pass launch=true only when the user asks to open the DSHGuard GUI window for snapshot rollback, credential vault, or CC Switch import.',
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
          },
        },
        render: (_args, value) => [{ type: 'text', text: value.message + (value.installed ? '' : `\n下载链接：${value.releaseUrl}`) }],
      },
      async execute(args = {}) {
        const res = openGuard({ launch: Boolean(args.launch) })
        return {
          installed: res.installed,
          launched: res.launched,
          message: res.message,
          releaseUrl: res.releaseUrl,
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
      description: 'DSH 守护壳伴生命令：/guard [status|snapshots|open]',
      input: { hint: '[status|snapshots|open]' },
      async handler(invocation) {
        const sub = String((invocation && invocation.rawInput) || '').trim().toLowerCase()
        if (sub === 'snapshots' || sub === 'snap') {
          return { kind: 'success', text: formatSnapshotsText(inspectSnapshots({})) }
        }
        if (sub === 'open') {
          const res = openGuard({ launch: true })
          return {
            kind: 'success',
            text: res.message + (res.installed ? '' : `\n下载地址：${res.releaseUrl}`),
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
  inspectHealth,
  inspectSnapshots,
  locateGuardInstallation,
  openGuard,
  formatHealthText,
  formatSnapshotsText,
}
