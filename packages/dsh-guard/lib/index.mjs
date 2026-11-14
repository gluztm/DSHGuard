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
    cmdCtx.commands.register({
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
