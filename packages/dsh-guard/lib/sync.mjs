import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import {
  appendAuditEntry,
  atomicWriteText,
  detectActiveProfile,
  discoverProfiles,
  normalizeProfileTarget,
  resolveDshHome,
} from './governance.mjs';
import { createSnapshot } from './inspector.mjs';

export const SYNC_SECTIONS = Object.freeze(['llm-providers', 'mcp-servers', 'permissions']);

const SECTION_LABELS = Object.freeze({
  'llm-providers': '模型提供商 (llm-pi-ai)',
  'mcp-servers': 'MCP 工具服务器',
  permissions: '权限守卫规则',
});

const SECRET_KEY_RE = /(?:api[-_]?key|secret|token|password|passwd|auth)/i;
const SAFE_SECRET_REF_RE = /^\$\{env:[A-Z0-9_]+\}$/i;

function safeReadText(filePath) {
  try {
    return fs.existsSync(filePath) ? fs.readFileSync(filePath, 'utf8') : '';
  } catch {
    return '';
  }
}

function sha256Text(text) {
  return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
}

function unquoteScalar(raw) {
  const trimmed = String(raw || '').trim();
  if (!trimmed) return '';
  if (
    (trimmed.startsWith('"') && trimmed.endsWith('"') && trimmed.length >= 2) ||
    (trimmed.startsWith("'") && trimmed.endsWith("'") && trimmed.length >= 2)
  ) {
    return trimmed.slice(1, -1);
  }
  return trimmed;
}

function stripInlineComment(line) {
  let inSingle = false;
  let inDouble = false;
  for (let i = 0; i < line.length; i += 1) {
    const ch = line[i];
    if (ch === "'" && !inDouble) inSingle = !inSingle;
    else if (ch === '"' && !inSingle && line[i - 1] !== '\\') inDouble = !inDouble;
    else if (ch === '#' && !inSingle && !inDouble && (i === 0 || /\s/.test(line[i - 1]))) {
      return line.slice(0, i);
    }
  }
  return line;
}

function splitKeyVal(lineText) {
  const idx = lineText.indexOf(':');
  if (idx <= 0) return null;
  const key = unquoteScalar(lineText.slice(0, idx));
  if (!key) return null;
  const val = lineText.slice(idx + 1).trim();
  return { key, val };
}

function classifySection(meta) {
  const id = String(meta.id || '').toLowerCase();
  const name = String(meta.name || '').toLowerCase();
  if (
    name === '@deepseek-ai/dsh-mcp-client' ||
    (meta.kind === 'insert' && id.startsWith('mcp-')) ||
    id.startsWith('mcp-')
  ) {
    return 'mcp-servers';
  }
  if (
    id === 'llm-pi-ai' ||
    name === '@deepseek-ai/dsh-llm-pi-ai' ||
    name.includes('llm-pi-ai')
  ) {
    return 'llm-providers';
  }
  if (
    id === 'permission' ||
    id === 'permissions' ||
    id.startsWith('permission') ||
    name.includes('permission')
  ) {
    return 'permissions';
  }
  return 'other';
}

function extractBlockMeta(rawLines) {
  let kind = 'override';
  let id = null;
  let name = null;
  let disabled = false;
  const allIds = [];
  const nestedHints = [];
  const subKeys = [];
  let inProviders = false;
  let providersIndent = -1;
  const providerNames = [];

  for (let i = 0; i < rawLines.length; i += 1) {
    const cleaned = stripInlineComment(rawLines[i]).trimEnd();
    if (!cleaned.trim()) continue;
    const indent = cleaned.length - cleaned.trimStart().length;
    let body = cleaned.trimStart();
    if (i === 0 && (body === '-' || body.startsWith('- '))) {
      body = body === '-' ? '' : body.slice(2).trimStart();
      if (!body) continue;
    } else if (i > 0 && kind === 'insert' && indent <= 6 && (body === '-' || body.startsWith('- '))) {
      body = body === '-' ? '' : body.slice(2).trimStart();
      if (!body) continue;
    }

    if (inProviders && indent <= providersIndent) {
      inProviders = false;
    }

    const kv = splitKeyVal(body);
    if (!kv) continue;

    if (kv.key === 'insert' && !kv.val && i <= 2) {
      kind = 'insert';
      continue;
    }
    if (kv.key === 'id' && kv.val && indent <= 6) {
      const parsedId = unquoteScalar(kv.val);
      if (parsedId) {
        if (!allIds.includes(parsedId)) allIds.push(parsedId);
        if (id === null) id = parsedId;
      }
      continue;
    }
    if (kv.key === 'name' && name === null && kv.val && indent <= 6) {
      name = unquoteScalar(kv.val);
      continue;
    }
    if (kv.key === 'disabled' && indent <= 6) {
      const bare = kv.val.replace(/^!![a-z]+\s+/i, '').trim().toLowerCase();
      if (bare === 'true') disabled = true;
      else if (bare === 'false') disabled = false;
      continue;
    }

    if (kv.key === 'providers') {
      inProviders = true;
      providersIndent = indent;
      continue;
    }
    if (inProviders && indent === providersIndent + 2) {
      providerNames.push(kv.key);
    }

    if (kv.key === 'serverName' || kv.key === 'transport' || kv.key === 'command' || kv.key === 'url') {
      const safeVal = unquoteScalar(kv.val);
      if (safeVal && !SECRET_KEY_RE.test(kv.key)) {
        nestedHints.push(`${kv.key}=${safeVal}`);
      }
    } else if (!SECRET_KEY_RE.test(kv.key)) {
      if (kv.val && !SAFE_SECRET_REF_RE.test(unquoteScalar(kv.val)) && kv.key.length <= 24) {
        subKeys.push(kv.key);
      }
    }
  }

  const section = classifySection({ kind, id, name });
  const key =
    allIds.length > 1
      ? `${kind}:${allIds.join('+')}`
      : id
        ? `${kind}:${id}`
        : `anon:${sha256Text(rawLines.join('\n')).slice(0, 12)}`;

  let summary = '';
  if (section === 'llm-providers') {
    summary = providerNames.length > 0 ? `Providers: ${providerNames.join(', ')}` : '模型提供商配置块';
  } else if (section === 'mcp-servers') {
    const hints = nestedHints.slice(0, 2).join(', ');
    const prefix = allIds.length > 1 ? `[${allIds.length} 项] ` : '';
    summary = `${disabled ? '[已禁用] ' : ''}${prefix}${hints || id || 'MCP 条目'}`;
  } else if (section === 'permissions') {
    summary = `${disabled ? '[已禁用] ' : ''}权限策略配置 (${rawLines.length} 行)`;
  }

  return {
    key,
    kind,
    id: id || key,
    name,
    disabled,
    section,
    summary,
    providerNames,
    lineCount: rawLines.length,
  };
}

/**
 * Losslessly split a `cordis.patch.yml` document into top-level raw YAML item blocks.
 * Preserves comments, YAML tags (`!!js`), indentation, and whitespace untouched.
 */
export function parseRawPatchDocument(text) {
  if (!text || !text.trim()) {
    return { preludeLines: [], listIndent: 0, wrapperHeader: null, blocks: [], trailingLines: [] };
  }
  const lines = text.replace(/\r\n/g, '\n').split('\n');
  while (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();

  let wrapperHeader = null;
  let listIndent = 0;
  let firstItemIdx = -1;

  for (let i = 0; i < lines.length; i += 1) {
    const cleaned = stripInlineComment(lines[i]);
    if (!cleaned.trim()) continue;
    const trimmed = cleaned.trimStart();
    const indent = cleaned.length - trimmed.length;
    if (trimmed === '-' || trimmed.startsWith('- ')) {
      listIndent = indent;
      firstItemIdx = i;
      break;
    }
    if (!wrapperHeader && indent === 0 && trimmed.startsWith('plugins:')) {
      wrapperHeader = lines[i];
    }
  }

  if (firstItemIdx < 0) {
    return { preludeLines: lines, listIndent: 0, wrapperHeader, blocks: [], trailingLines: [] };
  }

  const preludeLines = lines.slice(0, firstItemIdx);
  const itemHeaderRe = new RegExp(`^ {${listIndent}}-(?:\\s|$)`);
  const itemStarts = [];
  for (let i = firstItemIdx; i < lines.length; i += 1) {
    const cleaned = stripInlineComment(lines[i]);
    if (cleaned.trim() && itemHeaderRe.test(cleaned)) {
      itemStarts.push(i);
    }
  }

  const blocks = [];
  for (let idx = 0; idx < itemStarts.length; idx += 1) {
    const start = itemStarts[idx];
    const nextStart = idx + 1 < itemStarts.length ? itemStarts[idx + 1] : lines.length;
    let sliceEnd = nextStart;
    while (sliceEnd > start + 1 && !lines[sliceEnd - 1].trim()) {
      sliceEnd -= 1;
    }
    const rawLines = lines.slice(start, sliceEnd);
    const trimmedTrailing = lines.slice(sliceEnd, nextStart);
    const normalizedText = rawLines.map((l) => l.replace(/\s+$/, '')).join('\n');
    const meta = extractBlockMeta(rawLines);
    blocks.push({
      ...meta,
      rawLines,
      trailingBlankLines: trimmedTrailing,
      normalizedHash: sha256Text(normalizedText),
    });
  }

  return {
    preludeLines,
    listIndent,
    wrapperHeader,
    blocks,
    trailingLines: [],
  };
}

function reindentBlockLines(rawLines, fromIndent, toIndent) {
  const delta = toIndent - fromIndent;
  if (delta === 0) return [...rawLines];
  return rawLines.map((line) => {
    if (!line.trim()) return '';
    if (delta > 0) return `${' '.repeat(delta)}${line}`;
    const lead = line.length - line.trimStart().length;
    const cut = Math.min(lead, Math.abs(delta));
    return line.slice(cut);
  });
}

function serializeRawPatchDocument(doc) {
  const out = [];
  for (const l of doc.preludeLines || []) out.push(l);
  for (let i = 0; i < doc.blocks.length; i += 1) {
    const blk = doc.blocks[i];
    out.push(...blk.rawLines);
    if (i < doc.blocks.length - 1) {
      if (Array.isArray(blk.trailingBlankLines) && blk.trailingBlankLines.length > 0) {
        out.push(...blk.trailingBlankLines);
      }
    }
  }
  const body = out.join('\n').replace(/\n+$/, '');
  return body ? `${body}\n` : '';
}

function redactSummarySecrets(text) {
  return String(text || '')
    .replace(/\bsk-[a-zA-Z0-9_-]{8,}\b/g, 'sk-<REDACTED>')
    .replace(/(Bearer\s+)[A-Za-z0-9._~+/=-]{6,}/gi, '$1<REDACTED>')
    .replace(/((?:api[-_]?key|secret|token|password|passwd)\s*[:=]\s*["']?)([^\s"',;]{4,})/gi, '$1<REDACTED>');
}

/**
 * Explicit on-demand L0 diff between two DSH profiles (`web` <-> `desktop`)
 * scoped strictly to `SYNC_SECTIONS`. Never runs silently during startup.
 */
export function inspectProfileDiff(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const available = discoverProfiles(home);
  const active = detectActiveProfile({ ctx: options.ctx, dshHome: home });
  const source = normalizeProfileTarget(options.source, active);
  const target = normalizeProfileTarget(options.target, source === 'desktop' ? 'web' : 'desktop');

  if (!source || !target) {
    return { ok: false, error: '仅支持在 web 与 desktop 标准档案之间执行差异比对' };
  }
  if (source === target) {
    return { ok: false, error: '源档案与目标档案不能相同' };
  }

  const requestedSections = Array.isArray(options.sections) && options.sections.length > 0
    ? options.sections.filter((s) => SYNC_SECTIONS.includes(s))
    : [...SYNC_SECTIONS];
  const sections = requestedSections.length > 0 ? requestedSections : [...SYNC_SECTIONS];

  const sourcePath = path.join(home, 'profiles', source, 'cordis.patch.yml');
  const targetPath = path.join(home, 'profiles', target, 'cordis.patch.yml');
  const sourceExists = fs.existsSync(sourcePath);
  const targetExists = fs.existsSync(targetPath);

  const sourceDoc = parseRawPatchDocument(safeReadText(sourcePath));
  const targetDoc = parseRawPatchDocument(safeReadText(targetPath));

  const sectionReports = [];
  let totalDiffs = 0;

  for (const sec of sections) {
    const srcBlocks = sourceDoc.blocks.filter((b) => b.section === sec);
    const dstBlocks = targetDoc.blocks.filter((b) => b.section === sec);
    const srcMap = new Map(srcBlocks.map((b) => [b.key, b]));
    const dstMap = new Map(dstBlocks.map((b) => [b.key, b]));

    const keys = [];
    for (const b of srcBlocks) {
      if (!keys.includes(b.key)) keys.push(b.key);
    }
    for (const b of dstBlocks) {
      if (!keys.includes(b.key)) keys.push(b.key);
    }

    const items = [];
    for (const k of keys) {
      const sb = srcMap.get(k);
      const db = dstMap.get(k);
      let status = 'equal';
      if (sb && !db) status = 'missing-in-target';
      else if (!sb && db) status = 'only-in-target';
      else if (sb.normalizedHash !== db.normalizedHash) status = 'different';

      if (status !== 'equal') totalDiffs += 1;

      items.push({
        key: k,
        id: (sb || db).id,
        kind: (sb || db).kind,
        status,
        syncable: Boolean(sb) && (status === 'missing-in-target' || status === 'different'),
        sourceSummary: sb ? redactSummarySecrets(sb.summary) : null,
        targetSummary: db ? redactSummarySecrets(db.summary) : null,
        sourceDisabled: sb ? sb.disabled : null,
        targetDisabled: db ? db.disabled : null,
      });
    }

    const diffCount = items.filter((i) => i.status !== 'equal').length;
    const syncableCount = items.filter((i) => i.syncable).length;
    sectionReports.push({
      section: sec,
      label: SECTION_LABELS[sec] || sec,
      sourceCount: srcBlocks.length,
      targetCount: dstBlocks.length,
      diffCount,
      syncableCount,
      items,
    });
  }

  return {
    ok: true,
    checkedAt: new Date().toISOString(),
    source,
    target,
    availableProfiles: available,
    sourceExists,
    targetExists,
    totalDiffs,
    inSync: totalDiffs === 0,
    sections: sectionReports,
  };
}

/**
 * Losslessly merge selected YAML block sections from `source` profile into `target` profile.
 * Preserves YAML tags (`!!js`), comments, `${env:...}` references, and target-only custom entries.
 */
export function syncProfileSections(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const diff = inspectProfileDiff(home, options);
  if (!diff.ok) return diff;

  const { source, target } = diff;
  const sourcePath = path.join(home, 'profiles', source, 'cordis.patch.yml');
  const targetDir = path.join(home, 'profiles', target);
  const targetPath = path.join(targetDir, 'cordis.patch.yml');

  if (!fs.existsSync(sourcePath)) {
    return { ok: false, error: `源档案配置文件不存在: ${sourcePath}` };
  }

  const selectedSections = Array.isArray(options.sections) && options.sections.length > 0
    ? options.sections.filter((s) => SYNC_SECTIONS.includes(s))
    : [...SYNC_SECTIONS];
  if (selectedSections.length === 0) {
    return { ok: false, error: `sections 必须包含以下合法段落之一: ${SYNC_SECTIONS.join(', ')}` };
  }
  const itemKeyFilter = Array.isArray(options.keys) && options.keys.length > 0 ? new Set(options.keys) : null;

  const sourceDoc = parseRawPatchDocument(safeReadText(sourcePath));
  const targetText = safeReadText(targetPath);
  const targetDoc = parseRawPatchDocument(targetText);
  const targetBlocks = [...targetDoc.blocks];

  const sectionSet = new Set(selectedSections);
  const sourceCandidates = sourceDoc.blocks.filter(
    (b) => sectionSet.has(b.section) && (!itemKeyFilter || itemKeyFilter.has(b.key)),
  );

  const planned = [];
  for (const sb of sourceCandidates) {
    const existingIdx = targetBlocks.findIndex((tb) => tb.key === sb.key);
    if (existingIdx >= 0) {
      const tb = targetBlocks[existingIdx];
      if (tb.normalizedHash === sb.normalizedHash) continue;
      planned.push({ action: 'update', key: sb.key, id: sb.id, section: sb.section, summary: redactSummarySecrets(sb.summary) });
      targetBlocks[existingIdx] = {
        ...sb,
        rawLines: reindentBlockLines(sb.rawLines, sourceDoc.listIndent, targetDoc.listIndent),
        trailingBlankLines: tb.trailingBlankLines,
      };
    } else {
      planned.push({ action: 'insert', key: sb.key, id: sb.id, section: sb.section, summary: redactSummarySecrets(sb.summary) });
      targetBlocks.push({
        ...sb,
        rawLines: reindentBlockLines(sb.rawLines, sourceDoc.listIndent, targetDoc.listIndent),
        trailingBlankLines: [],
      });
    }
  }

  if (options.dryRun === true || planned.length === 0) {
    return {
      ok: true,
      dryRun: Boolean(options.dryRun),
      source,
      target,
      sections: selectedSections,
      changeCount: planned.length,
      changes: planned,
      alreadyAligned: planned.length === 0,
      snapshotId: null,
    };
  }

  fs.mkdirSync(targetDir, { recursive: true });
  const preSnap = createSnapshot(
    home,
    target,
    'auto-sync',
    `从 ${source} 同步 ${selectedSections.join('/')} 至 ${target} 前自动快照`,
  );

  const nextText = serializeRawPatchDocument({
    ...targetDoc,
    blocks: targetBlocks,
  });
  atomicWriteText(targetPath, nextText);

  appendAuditEntry(home, {
    action: 'profile.sync',
    target,
    summary: `${source} -> ${target} 同步 [${selectedSections.join(', ')}]（${planned.length} 项）`,
    snapshotId: preSnap.snapshot.id,
    ok: true,
  });

  return {
    ok: true,
    dryRun: false,
    source,
    target,
    sections: selectedSections,
    changeCount: planned.length,
    changes: planned,
    alreadyAligned: false,
    snapshotId: preSnap.snapshot.id,
    targetPath,
    restartHint: `已将 ${planned.length} 项配置从 ${source} 合并到 ${target}（目标端独有配置已保留），重启 ${target} 端后生效。`,
  };
}

export function formatProfileDiffText(report) {
  if (!report?.ok) return `[dsh-guard] 双端比对失败: ${report?.error || '未知错误'}`;
  const lines = [
    `[dsh-guard] 按需配置比对 (${report.source} -> ${report.target}) · 差异总计 ${report.totalDiffs} 项`,
  ];
  for (const sec of report.sections || []) {
    lines.push(`- ${sec.label}: 源 ${sec.sourceCount} / 目标 ${sec.targetCount} · 差异 ${sec.diffCount}（可同步 ${sec.syncableCount}）`);
    for (const item of sec.items) {
      if (item.status === 'equal') continue;
      lines.push(`  * [${item.status}] ${item.key}: ${item.sourceSummary || item.targetSummary || ''}`);
    }
  }
  return lines.join('\n');
}
