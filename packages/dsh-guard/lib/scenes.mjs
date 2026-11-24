import fs from 'node:fs';
import path from 'node:path';
import {
  appendAuditEntry,
  atomicWriteText,
  detectActiveProfile,
  inspectSkills,
  normalizeProfileTarget,
  resolveDshHome,
  toggleSkillVisibility,
} from './governance.mjs';
import {
  createSnapshot,
  inspectExtensions,
  togglePatchEntry,
} from './inspector.mjs';

export const SCENES_REL_PATH = path.join('guard-state', 'scenes.json');
const SCENE_ID_RE = /^[a-z0-9][a-z0-9_-]{0,47}$/i;
const SELF_LOADER_IDS = new Set(['dsh-guard', '@gluztm/dsh-guard']);

const DEV_RE = /\b(?:git|github|gitlab|code|lsp|lint|eslint|prettier|test|jest|vitest|pytest|playwright|puppeteer|devtools|postgres|sqlite|mysql|redis|docker|k8s|kubectl|filesystem|fs|shell|terminal|bash|context7|tsserver|tsc)\b/i;
const MEMORY_RE = /\b(?:memory|mem|rag|vector|knowledge|recall|notes|obsidian|notion)\b/i;
const MEDIA_RE = /\b(?:image|img|video|audio|voice|tts|stt|speech|music|render|3d|blender|comfy|canvas|draw|svg|vision|ocr|chart|plot|diagram|media)\b/i;
const OFFICE_RE = /\b(?:office|excel|word|ppt|pptx|docx|xlsx|sheet|sheets|slide|slides|pdf|table|calendar|mail|gmail|slack|feishu|lark|dingtalk)\b/i;
const WEB_RE = /\b(?:browser|web|fetch|search|crawl|scrape|http)\b/i;

export const BUILTIN_SCENES = Object.freeze([
  {
    id: 'lean-coding',
    name: '轻量编码 (Lean Coding)',
    description: '保留核心组件、守卫插件及研发/记忆类扩展，停用重型媒体与办公文档扩展，降低上下文与工具干扰。',
    builtin: true,
    strategy: 'lean-coding',
  },
  {
    id: 'creative-full',
    name: '全量创作 (Creative Full)',
    description: '启用全部已安装的可切换第三方插件与 MCP 工具服务器，并恢复用户级技能的模型可见性。',
    builtin: true,
    strategy: 'creative-full',
  },
  {
    id: 'safe-mode',
    name: '安全极简 (Safe Mode)',
    description: '故障排查与低负载专用：停用全部第三方可切换插件与 MCP 服务器，并关闭用户级技能的自动模型唤醒。',
    builtin: true,
    strategy: 'safe-mode',
  },
]);

const BUILTIN_SCENE_IDS = new Set(BUILTIN_SCENES.map((s) => s.id));

/**
 * Dynamically classify a plugin or MCP server descriptor by generic ecosystem capability.
 */
export function classifyExtension(item = {}) {
  if (item.isCore) return 'core';
  if (item.isSelf || SELF_LOADER_IDS.has(item.name) || SELF_LOADER_IDS.has(item.id)) return 'guard';

  const haystack = [
    item.name,
    item.id,
    item.serverName,
    item.description,
    item.command,
    Array.isArray(item.args) ? item.args.join(' ') : '',
  ]
    .filter(Boolean)
    .join(' ');

  if (MEDIA_RE.test(haystack)) return 'media';
  if (OFFICE_RE.test(haystack)) return 'office';
  if (DEV_RE.test(haystack)) return 'dev';
  if (MEMORY_RE.test(haystack)) return 'memory';
  if (WEB_RE.test(haystack)) return 'web';
  return 'general';
}

function resolveScenesFilePath(dshHome) {
  return path.join(dshHome, SCENES_REL_PATH);
}

function sanitizeCustomScene(raw) {
  if (!raw || typeof raw !== 'object') return null;
  const id = String(raw.id || '').trim();
  if (!SCENE_ID_RE.test(id) || BUILTIN_SCENE_IDS.has(id)) return null;

  const normalizeMap = (obj) => {
    const out = {};
    if (!obj || typeof obj !== 'object') return out;
    for (const [k, v] of Object.entries(obj)) {
      const key = String(k || '').trim();
      if (!key || typeof v !== 'boolean') continue;
      out[key] = v;
    }
    return out;
  };

  return {
    id,
    name: String(raw.name || id).trim().slice(0, 64) || id,
    description: String(raw.description || '').trim().slice(0, 200),
    builtin: false,
    strategy: 'custom',
    createdAt: raw.createdAt || null,
    updatedAt: raw.updatedAt || null,
    plugins: normalizeMap(raw.plugins),
    mcpServers: normalizeMap(raw.mcpServers),
    writableSkills: normalizeMap(raw.writableSkills),
  };
}

/**
 * Load user-defined custom scenes from `<dshHome>/guard-state/scenes.json`.
 */
export function loadCustomScenes(dshHome) {
  const filePath = resolveScenesFilePath(dshHome);
  if (!fs.existsSync(filePath)) return [];
  try {
    const parsed = JSON.parse(fs.readFileSync(filePath, 'utf8'));
    const list = Array.isArray(parsed?.scenes) ? parsed.scenes : Array.isArray(parsed) ? parsed : [];
    const seen = new Set();
    const out = [];
    for (const item of list) {
      const clean = sanitizeCustomScene(item);
      if (!clean || seen.has(clean.id)) continue;
      seen.add(clean.id);
      out.push(clean);
    }
    return out;
  } catch {
    return [];
  }
}

function writeCustomScenes(dshHome, scenes) {
  const filePath = resolveScenesFilePath(dshHome);
  atomicWriteText(
    filePath,
    `${JSON.stringify({ version: 1, updatedAt: new Date().toISOString(), scenes }, null, 2)}\n`,
  );
  return filePath;
}

function computeTargetStateForPlugin(scene, plugin) {
  if (!plugin.canToggle || plugin.isCore || plugin.isSelf) return plugin.enabled;
  if (scene.builtin) {
    if (scene.strategy === 'creative-full') return true;
    if (scene.strategy === 'safe-mode') return false;
    if (scene.strategy === 'lean-coding') {
      const cat = classifyExtension(plugin);
      return cat === 'core' || cat === 'guard' || cat === 'dev' || cat === 'memory' || cat === 'web';
    }
  }
  if (Object.prototype.hasOwnProperty.call(scene.plugins || {}, plugin.name)) {
    return Boolean(scene.plugins[plugin.name]);
  }
  if (Object.prototype.hasOwnProperty.call(scene.plugins || {}, plugin.primaryId)) {
    return Boolean(scene.plugins[plugin.primaryId]);
  }
  return plugin.enabled;
}

function computeTargetStateForMcp(scene, mcp) {
  if (!mcp.canToggle) return mcp.enabled;
  if (scene.builtin) {
    if (scene.strategy === 'creative-full') return true;
    if (scene.strategy === 'safe-mode') return false;
    if (scene.strategy === 'lean-coding') {
      const cat = classifyExtension(mcp);
      return cat === 'dev' || cat === 'memory' || cat === 'web';
    }
  }
  if (Object.prototype.hasOwnProperty.call(scene.mcpServers || {}, mcp.id)) {
    return Boolean(scene.mcpServers[mcp.id]);
  }
  if (Object.prototype.hasOwnProperty.call(scene.mcpServers || {}, mcp.serverName)) {
    return Boolean(scene.mcpServers[mcp.serverName]);
  }
  return mcp.enabled;
}

function computeTargetStateForSkill(scene, skill) {
  if (!skill.writable) return skill.modelVisible;
  if (scene.builtin) {
    if (scene.strategy === 'safe-mode') return false;
    if (scene.strategy === 'creative-full') return true;
    return skill.modelVisible;
  }
  if (Object.prototype.hasOwnProperty.call(scene.writableSkills || {}, skill.id)) {
    return Boolean(scene.writableSkills[skill.id]);
  }
  return skill.modelVisible;
}

function resolveAllScenes(dshHome) {
  return [...BUILTIN_SCENES, ...loadCustomScenes(dshHome)];
}

function evaluateSceneDiff(scene, ext, skillsData) {
  const pluginChanges = [];
  const protectedSkipped = [];

  for (const p of ext.plugins || []) {
    if (!p.canToggle || p.isCore || p.isSelf) {
      protectedSkipped.push(p.name);
      continue;
    }
    const targetEnabled = computeTargetStateForPlugin(scene, p);
    if (p.enabled !== targetEnabled) {
      pluginChanges.push({
        id: p.primaryId,
        name: p.name,
        category: classifyExtension(p),
        currentEnabled: p.enabled,
        targetEnabled,
      });
    }
  }

  const mcpChanges = [];
  for (const m of ext.mcpServers || []) {
    if (!m.canToggle) continue;
    const targetEnabled = computeTargetStateForMcp(scene, m);
    if (m.enabled !== targetEnabled) {
      mcpChanges.push({
        id: m.id,
        serverName: m.serverName,
        category: classifyExtension(m),
        currentEnabled: m.enabled,
        targetEnabled,
      });
    }
  }

  const skillChanges = [];
  for (const s of skillsData?.skills || []) {
    if (!s.writable) continue;
    const targetModelVisible = computeTargetStateForSkill(scene, s);
    if (s.modelVisible !== targetModelVisible) {
      skillChanges.push({
        id: s.id,
        name: s.name,
        source: s.source,
        currentModelVisible: s.modelVisible,
        targetModelVisible,
      });
    }
  }

  const totalChanges = pluginChanges.length + mcpChanges.length + skillChanges.length;
  return {
    pluginChanges,
    mcpChanges,
    skillChanges,
    protectedSkipped,
    totalChanges,
    alreadyAligned: totalChanges === 0,
  };
}

/**
 * List built-in and custom scenes alongside pending delta count for the target profile.
 */
export function listScenes(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const target = normalizeProfileTarget(options.target) || detectActiveProfile({ ctx: options.ctx, dshHome: home });
  const ext = inspectExtensions(home, target, options.guard || null);
  const skillsData = inspectSkills(home, [target], { agentsHome: options.agentsHome });
  const all = resolveAllScenes(home);

  const scenes = all.map((scene) => {
    const diff = evaluateSceneDiff(scene, ext, skillsData);
    return {
      ...scene,
      pluginChangesCount: diff.pluginChanges.length,
      mcpChangesCount: diff.mcpChanges.length,
      skillChangesCount: diff.skillChanges.length,
      totalChanges: diff.totalChanges,
      alreadyAligned: diff.alreadyAligned,
    };
  });

  return {
    ok: true,
    target,
    scenesPath: resolveScenesFilePath(home),
    builtinCount: BUILTIN_SCENES.length,
    customCount: scenes.length - BUILTIN_SCENES.length,
    scenes,
  };
}

/**
 * Save or update a custom scene. When explicit maps are omitted, captures current target state.
 */
export function saveCustomScene(dshHome, input = {}, options = {}) {
  const home = resolveDshHome(dshHome);
  const target = normalizeProfileTarget(options.target || input.target) || detectActiveProfile({ ctx: options.ctx, dshHome: home });
  const id = String(input.id || '').trim();

  if (!id || !SCENE_ID_RE.test(id)) {
    return { ok: false, error: '场景 ID 仅支持 1-48 位字母、数字、连字符或下划线' };
  }
  if (BUILTIN_SCENE_IDS.has(id)) {
    return { ok: false, error: `内置场景 ${id} 为只读预设，请使用其他自定义 ID` };
  }

  const ext = inspectExtensions(home, target, options.guard || null);
  const skillsData = inspectSkills(home, [target], { agentsHome: options.agentsHome });
  const captureCurrent = input.captureCurrent === true || (!input.plugins && !input.mcpServers && !input.writableSkills);

  let pluginsMap = {};
  let mcpMap = {};
  let skillMap = {};

  if (captureCurrent) {
    for (const p of ext.plugins || []) {
      if (p.canToggle && !p.isCore && !p.isSelf) {
        pluginsMap[p.name] = Boolean(p.enabled);
      }
    }
    for (const m of ext.mcpServers || []) {
      if (m.canToggle) {
        mcpMap[m.id] = Boolean(m.enabled);
      }
    }
    for (const s of skillsData.skills || []) {
      if (s.writable) {
        skillMap[s.id] = Boolean(s.modelVisible);
      }
    }
  } else {
    const protectedSet = new Set(
      (ext.plugins || []).filter((p) => !p.canToggle || p.isCore || p.isSelf).flatMap((p) => [p.name, ...p.loaderIds]),
    );
    for (const [k, v] of Object.entries(input.plugins || {})) {
      if (protectedSet.has(k) || k.startsWith('@deepseek-ai/') || SELF_LOADER_IDS.has(k)) continue;
      if (typeof v === 'boolean') pluginsMap[k] = v;
    }
    for (const [k, v] of Object.entries(input.mcpServers || {})) {
      if (typeof v === 'boolean') mcpMap[k] = v;
    }
    for (const [k, v] of Object.entries(input.writableSkills || {})) {
      if (typeof v === 'boolean') skillMap[k] = v;
    }
  }

  const existing = loadCustomScenes(home);
  const nowIso = new Date().toISOString();
  const prev = existing.find((s) => s.id === id);
  const clean = sanitizeCustomScene({
    id,
    name: input.name || prev?.name || id,
    description: input.description ?? prev?.description ?? `基于 ${target} 档案捕获的自定义场景`,
    createdAt: prev?.createdAt || nowIso,
    updatedAt: nowIso,
    plugins: pluginsMap,
    mcpServers: mcpMap,
    writableSkills: skillMap,
  });

  const nextList = [...existing.filter((s) => s.id !== id), clean];
  const scenesPath = writeCustomScenes(home, nextList);

  if (!options.skipAudit) {
    appendAuditEntry(home, {
      action: 'scene.save',
      target,
      summary: `保存场景 ${clean.name} (${clean.id})`,
      ok: true,
    });
  }

  return {
    ok: true,
    target,
    scene: clean,
    scenesPath,
  };
}

/**
 * Delete a user-defined custom scene (built-in scenes cannot be removed).
 */
export function deleteCustomScene(dshHome, sceneId, options = {}) {
  const home = resolveDshHome(dshHome);
  const id = String(sceneId || '').trim();
  if (!id) return { ok: false, error: '缺少场景 ID' };
  if (BUILTIN_SCENE_IDS.has(id)) {
    return { ok: false, error: `内置场景 ${id} 受保护，不可删除` };
  }

  const existing = loadCustomScenes(home);
  const targetScene = existing.find((s) => s.id === id);
  if (!targetScene) {
    return { ok: false, error: `未找到自定义场景 ${id}` };
  }

  const nextList = existing.filter((s) => s.id !== id);
  const scenesPath = writeCustomScenes(home, nextList);

  if (!options.skipAudit) {
    appendAuditEntry(home, {
      action: 'scene.delete',
      target: 'global',
      summary: `删除自定义场景 ${targetScene.name} (${id})`,
      ok: true,
    });
  }

  return {
    ok: true,
    deletedId: id,
    scenesPath,
  };
}

/**
 * L0 Dry-Run preview of switching the target profile to a scene without mutating disk files.
 */
export function previewSceneApply(dshHome, sceneId, options = {}) {
  const home = resolveDshHome(dshHome);
  const target = normalizeProfileTarget(options.target) || detectActiveProfile({ ctx: options.ctx, dshHome: home });
  const id = String(sceneId || '').trim();
  if (!id) return { ok: false, error: '缺少场景 ID' };

  const scene = resolveAllScenes(home).find((s) => s.id === id);
  if (!scene) {
    return { ok: false, error: `未找到场景 ${id}` };
  }

  const ext = inspectExtensions(home, target, options.guard || null);
  const skillsData = inspectSkills(home, [target], { agentsHome: options.agentsHome });
  const diff = evaluateSceneDiff(scene, ext, skillsData);

  return {
    ok: true,
    dryRun: true,
    target,
    scene: {
      id: scene.id,
      name: scene.name,
      description: scene.description,
      builtin: scene.builtin,
      strategy: scene.strategy,
    },
    ...diff,
  };
}

/**
 * L2 4-step transactional scene switch:
 * 1. Auto-snapshot before mutation
 * 2. Apply plugin & MCP toggles via lossless patch editor (skip per-item snapshot/audit)
 * 3. Apply writable user skill visibility updates (skip per-item audit)
 * 4. Write a single aggregated `scene.apply` audit entry
 */
export function applyScene(dshHome, sceneId, options = {}) {
  const home = resolveDshHome(dshHome);
  if (options.dryRun === true) {
    return previewSceneApply(home, sceneId, options);
  }

  const preview = previewSceneApply(home, sceneId, options);
  if (!preview.ok) return preview;

  const { target, scene, pluginChanges, mcpChanges, skillChanges, protectedSkipped, totalChanges } = preview;
  if (totalChanges === 0) {
    return {
      ok: true,
      dryRun: false,
      target,
      scene,
      alreadyAligned: true,
      totalChanges: 0,
      applied: 0,
      pluginChanges,
      mcpChanges,
      skillChanges,
      protectedSkipped,
      snapshotId: null,
      restartHint: `当前 ${target} 档案已与场景「${scene.name}」完全一致，无需变更。`,
    };
  }

  const preSnap = createSnapshot(
    home,
    target,
    'auto-scene',
    `切换场景「${scene.name}」(${scene.id}) 前自动快照`,
  );

  const errors = [];
  let appliedCount = 0;

  for (const item of [...pluginChanges, ...mcpChanges]) {
    const res = togglePatchEntry(home, target, item.id, !item.targetEnabled, options.guard || null, {
      skipAutoSnapshot: true,
      skipAudit: true,
    });
    if (res.ok) appliedCount += 1;
    else errors.push(`${item.id}: ${res.error}`);
  }

  for (const sk of skillChanges) {
    const res = toggleSkillVisibility(
      home,
      sk.id,
      { modelVisible: sk.targetModelVisible },
      { target, agentsHome: options.agentsHome, skipAudit: true },
    );
    if (res.ok) appliedCount += 1;
    else errors.push(`${sk.id}: ${res.error}`);
  }

  const allOk = errors.length === 0;
  appendAuditEntry(home, {
    action: 'scene.apply',
    target,
    summary: `切换场景 ${scene.name} (${scene.id}) · 插件${pluginChanges.length}/MCP${mcpChanges.length}/技能${skillChanges.length}`,
    snapshotId: preSnap.snapshot.id,
    ok: allOk,
  });

  return {
    ok: allOk,
    dryRun: false,
    target,
    scene,
    alreadyAligned: false,
    totalChanges,
    applied: appliedCount,
    errors,
    pluginChanges,
    mcpChanges,
    skillChanges,
    protectedSkipped,
    snapshotId: preSnap.snapshot.id,
    restartHint:
      pluginChanges.length + mcpChanges.length > 0
        ? target === 'web'
          ? '已应用场景并创建自动快照，重载 DSH 服务后插件与 MCP 变更生效。'
          : `已应用场景并创建自动快照，重启 ${target} 端后插件与 MCP 变更生效。`
        : '已完成用户技能可见性切换（已在变更前创建自动快照）。',
  };
}

export function formatScenesText(report) {
  if (!report?.ok) return `[dsh-guard] 场景查询失败: ${report?.error || '未知错误'}`;
  const lines = [
    `[dsh-guard] 场景管理 · 目标端: ${report.target} · 共 ${report.scenes.length} 个场景（内置 ${report.builtinCount} / 自定义 ${report.customCount}）`,
  ];
  for (const s of report.scenes) {
    const tag = s.builtin ? '内置' : '自定义';
    const state = s.alreadyAligned ? '已对齐' : `待变更 ${s.totalChanges} 项`;
    lines.push(`- ${s.id} [${tag}] · ${s.name} · ${state}`);
  }
  return lines.join('\n');
}
