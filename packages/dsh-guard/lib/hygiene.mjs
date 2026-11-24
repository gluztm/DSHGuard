import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  appendAuditEntry,
  atomicWriteText,
  resolveDshHome,
} from './governance.mjs';

const CRED_SKIP_RE = /(?:cred|secret|token|auth|\.pem$|\.key$)/i;
const BAK_FILE_RE = /\.bak(?:$|[._-])/i;
const LOG_FILE_RE = /\.(?:log|txt|jsonl)$/i;

const MAX_WALK_DEPTH = 6;
const MAX_WALK_ENTRIES = 50000;
const RECYCLE_PREVIEW_CAP = 200;
const LOG_TAIL_BYTES = 256 * 1024;
const LOG_MAX_FILES = 40;
const DEFAULT_HOURS_WINDOW = 168;

const BUCKET_DEFS = [
  {
    id: 'cache',
    label: '本地缓存 (cache)',
    description: '临时缓存与索引数据，可安全一键清理（移入回收站）。',
    relPath: 'cache',
    safetyLevel: 'safe',
    defaultSelected: true,
    canRecycle: true,
  },
  {
    id: 'profile-bak',
    label: '配置备份 (*.bak*)',
    description: '档案目录下的配置备份文件（已自动跳过所有凭据与密钥备份）。',
    relPath: 'profiles/*/*.bak*',
    safetyLevel: 'safe',
    defaultSelected: true,
    canRecycle: true,
  },
  {
    id: 'attachments',
    label: '对话附件 (attachments)',
    description: '会话上传或生成的图片、文件附件。',
    relPath: 'attachments',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'sessions-offline',
    label: '离线会话 (sessions-offline)',
    description: '离线同步缓存的会话数据。',
    relPath: 'sessions-offline',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'sessions',
    label: '历史会话 (sessions)',
    description: '已保存的本地对话历史记录，清理前建议确认过期天数。',
    relPath: 'sessions',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'rewind-snapshots',
    label: '回溯快照 (rewind-snapshots)',
    description: 'DSH 会话级代码回溯快照目录。',
    relPath: 'rewind-snapshots',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'undo-snapshots',
    label: '撤销快照 (undo-snapshots)',
    description: '编辑器局部撤销历史快照。',
    relPath: 'undo-snapshots',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'guard-snapshots',
    label: '守卫配置快照 (guard-snapshots)',
    description: '未置顶的历史配置快照（置顶快照将自动受保护跳过）。',
    relPath: 'guard-snapshots',
    safetyLevel: 'caution',
    defaultSelected: false,
    canRecycle: true,
  },
  {
    id: 'dsh-runtimes',
    label: '内置运行时 (dsh-runtimes)',
    description: 'DSH 托管运行时目录，仅统计占用，禁止回收清理。',
    relPath: 'dsh-runtimes',
    safetyLevel: 'protected',
    defaultSelected: false,
    canRecycle: false,
  },
  {
    id: 'guard-recycle',
    label: '守卫回收站 (guard-recycle)',
    description: '已移入回收站的可逆清理批次，可在下方一键还原。',
    relPath: 'guard-recycle',
    safetyLevel: 'protected',
    defaultSelected: false,
    canRecycle: false,
  },
];

const BUCKET_MAP = new Map(BUCKET_DEFS.map((b) => [b.id, b]));

function isInsideDir(parentDir, childPath) {
  const rel = path.relative(path.resolve(parentDir), path.resolve(childPath));
  return Boolean(rel) && !rel.startsWith('..') && !path.isAbsolute(rel);
}

function toPosixRel(rootDir, absPath) {
  return path.relative(rootDir, absPath).replace(/\\/g, '/');
}

function formatBytesHuman(bytes) {
  const n = Number(bytes) || 0;
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / (1024 * 1024)).toFixed(1)} MB`;
  return `${(n / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

function isPinnedSnapshotRoot(absDir) {
  const manifestPath = path.join(absDir, 'manifest.json');
  if (!fs.existsSync(manifestPath)) return false;
  try {
    const data = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
    return Boolean(data && data.pinned === true);
  } catch {
    return false;
  }
}

function walkDirectoryStats(dshHome, rootDir, options = {}) {
  const result = {
    exists: fs.existsSync(rootDir),
    totalBytes: 0,
    fileCount: 0,
    dirCount: 0,
    oldestMtimeMs: null,
    newestMtimeMs: null,
    skippedSensitiveCount: 0,
    files: [],
    truncated: false,
  };
  if (!result.exists) return result;

  const checkPinnedTopDir = options.protectPinnedSnapshots === true;
  const collectFiles = options.collectFiles === true;
  let walkedEntries = 0;

  function visit(currentDir, depth, pinnedAncestor) {
    if (depth > MAX_WALK_DEPTH || walkedEntries >= MAX_WALK_ENTRIES) {
      result.truncated = true;
      return;
    }

    let entries = [];
    try {
      entries = fs.readdirSync(currentDir, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      if (walkedEntries >= MAX_WALK_ENTRIES) {
        result.truncated = true;
        return;
      }
      walkedEntries += 1;

      if (entry.isSymbolicLink()) continue;

      const absPath = path.join(currentDir, entry.name);
      const relPath = toPosixRel(dshHome, absPath);
      if (CRED_SKIP_RE.test(entry.name) || CRED_SKIP_RE.test(relPath)) {
        result.skippedSensitiveCount += 1;
        continue;
      }

      if (entry.isDirectory()) {
        if (entry.name === 'node_modules') continue;
        result.dirCount += 1;
        const nextPinned =
          pinnedAncestor || (checkPinnedTopDir && depth === 0 && isPinnedSnapshotRoot(absPath));
        visit(absPath, depth + 1, nextPinned);
      } else if (entry.isFile()) {
        try {
          const st = fs.lstatSync(absPath);
          if (st.isSymbolicLink()) continue;
          result.fileCount += 1;
          result.totalBytes += st.size;
          if (result.oldestMtimeMs === null || st.mtimeMs < result.oldestMtimeMs) {
            result.oldestMtimeMs = st.mtimeMs;
          }
          if (result.newestMtimeMs === null || st.mtimeMs > result.newestMtimeMs) {
            result.newestMtimeMs = st.mtimeMs;
          }
          if (collectFiles && !pinnedAncestor) {
            result.files.push({
              absPath,
              relPath,
              size: st.size,
              mtimeMs: st.mtimeMs,
            });
          }
        } catch {
          // Ignore disappeared or locked files.
        }
      }
    }
  }

  visit(rootDir, 0, false);
  return result;
}

function scanProfileBakFiles(dshHome, collectFiles = false) {
  const dirsToScan = [
    dshHome,
    path.join(dshHome, 'profiles', 'web'),
    path.join(dshHome, 'profiles', 'desktop'),
  ];
  const result = {
    exists: dirsToScan.some((d) => fs.existsSync(d)),
    totalBytes: 0,
    fileCount: 0,
    dirCount: 0,
    oldestMtimeMs: null,
    newestMtimeMs: null,
    skippedSensitiveCount: 0,
    files: [],
    truncated: false,
  };

  for (const dir of dirsToScan) {
    if (!fs.existsSync(dir)) continue;
    let entries = [];
    try {
      entries = fs.readdirSync(dir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const entry of entries) {
      if (entry.isSymbolicLink() || !entry.isFile()) continue;
      if (!BAK_FILE_RE.test(entry.name)) continue;

      const absPath = path.join(dir, entry.name);
      const relPath = toPosixRel(dshHome, absPath);
      if (CRED_SKIP_RE.test(entry.name) || CRED_SKIP_RE.test(relPath)) {
        result.skippedSensitiveCount += 1;
        continue;
      }

      try {
        const st = fs.lstatSync(absPath);
        if (st.isSymbolicLink()) continue;
        result.fileCount += 1;
        result.totalBytes += st.size;
        if (result.oldestMtimeMs === null || st.mtimeMs < result.oldestMtimeMs) {
          result.oldestMtimeMs = st.mtimeMs;
        }
        if (result.newestMtimeMs === null || st.mtimeMs > result.newestMtimeMs) {
          result.newestMtimeMs = st.mtimeMs;
        }
        if (collectFiles) {
          result.files.push({
            absPath,
            relPath,
            size: st.size,
            mtimeMs: st.mtimeMs,
          });
        }
      } catch {
        // Ignore transient stat error.
      }
    }
  }
  return result;
}

function scanBucket(dshHome, def, collectFiles = false) {
  if (def.id === 'profile-bak') {
    return scanProfileBakFiles(dshHome, collectFiles);
  }
  const absDir = path.join(dshHome, def.relPath);
  return walkDirectoryStats(dshHome, absDir, {
    protectPinnedSnapshots: def.id === 'guard-snapshots',
    collectFiles,
  });
}

/**
 * List reversible cleanup batches stored in `<dshHome>/guard-recycle`.
 */
export function listRecycleBatches(dshHome, limit = 20) {
  const home = resolveDshHome(dshHome);
  const root = path.join(home, 'guard-recycle');
  if (!fs.existsSync(root)) return [];

  let dirs = [];
  try {
    dirs = fs.readdirSync(root, { withFileTypes: true });
  } catch {
    return [];
  }

  const max = Number.isFinite(Number(limit)) ? Math.max(1, Math.min(50, Number(limit))) : 20;
  const batches = [];
  for (const d of dirs) {
    if (!d.isDirectory() || d.isSymbolicLink()) continue;
    const manifestPath = path.join(root, d.name, 'manifest.json');
    if (!fs.existsSync(manifestPath)) continue;
    try {
      const m = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
      if (!m || !m.id) continue;
      batches.push({
        id: String(m.id),
        createdAt: m.createdAt || null,
        buckets: Array.isArray(m.buckets) ? m.buckets : [],
        minAgeDays: Number(m.minAgeDays) || 0,
        itemCount: Number(m.itemCount) || 0,
        totalBytes: Number(m.totalBytes) || 0,
        restoredAt: m.restoredAt || null,
        restoredCount: Number(m.restoredCount) || 0,
      });
    } catch {
      // Skip malformed manifest.
    }
  }

  batches.sort((a, b) => String(b.createdAt || b.id).localeCompare(String(a.createdAt || a.id)));
  return batches.slice(0, max);
}

/**
 * L0 read-only inventory of `~/.dsh` storage usage by standard bucket.
 * Strictly ignores symlinks and never reads or exposes credential/secret backup files.
 */
export function inspectStorageHygiene(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const buckets = [];
  let totalBytes = 0;
  let reclaimableBytes = 0;
  let safeReclaimableBytes = 0;
  let skippedSensitiveCount = 0;

  for (const def of BUCKET_DEFS) {
    const stat = scanBucket(home, def, false);
    totalBytes += stat.totalBytes;
    skippedSensitiveCount += stat.skippedSensitiveCount;
    if (def.canRecycle) {
      reclaimableBytes += stat.totalBytes;
      if (def.defaultSelected) safeReclaimableBytes += stat.totalBytes;
    }
    buckets.push({
      id: def.id,
      label: def.label,
      description: def.description,
      relPath: def.relPath,
      safetyLevel: def.safetyLevel,
      defaultSelected: def.defaultSelected,
      canRecycle: def.canRecycle,
      exists: stat.exists,
      totalBytes: stat.totalBytes,
      humanSize: formatBytesHuman(stat.totalBytes),
      fileCount: stat.fileCount,
      dirCount: stat.dirCount,
      oldestMtime: stat.oldestMtimeMs ? new Date(stat.oldestMtimeMs).toISOString() : null,
      newestMtime: stat.newestMtimeMs ? new Date(stat.newestMtimeMs).toISOString() : null,
      truncated: stat.truncated,
    });
  }

  const recycleBatches = listRecycleBatches(home, 15);
  return {
    ok: true,
    checkedAt: new Date().toISOString(),
    dshHome: home,
    totalBytes,
    humanTotalSize: formatBytesHuman(totalBytes),
    reclaimableBytes,
    humanReclaimableSize: formatBytesHuman(reclaimableBytes),
    safeReclaimableBytes,
    humanSafeReclaimableSize: formatBytesHuman(safeReclaimableBytes),
    skippedSensitiveCount,
    bucketCount: buckets.length,
    buckets,
    recycleBatches,
  };
}

/**
 * L0 Dry-Run planner: collects eligible files for selected buckets and age threshold without moving anything.
 */
export function planStorageCleanup(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const requested = Array.isArray(options.buckets) && options.buckets.length > 0
    ? options.buckets.map((b) => String(b || '').trim())
    : BUCKET_DEFS.filter((b) => b.defaultSelected).map((b) => b.id);

  const selectedDefs = [];
  const rejectedBuckets = [];
  for (const id of requested) {
    const def = BUCKET_MAP.get(id);
    if (!def) {
      rejectedBuckets.push({ id, reason: '未知存储分区' });
      continue;
    }
    if (!def.canRecycle) {
      rejectedBuckets.push({ id, reason: '受保护分区不可移入回收站' });
      continue;
    }
    if (!selectedDefs.includes(def)) selectedDefs.push(def);
  }

  if (selectedDefs.length === 0) {
    return {
      ok: false,
      error: rejectedBuckets[0]?.reason || '未选择任何可清理的合法存储分区',
      rejectedBuckets,
    };
  }

  const minAgeDays = Math.max(0, Number.isFinite(Number(options.minAgeDays)) ? Number(options.minAgeDays) : 0);
  const cutoffMs = minAgeDays > 0 ? Date.now() - minAgeDays * 86400000 : Infinity;

  const candidates = [];
  let totalBytes = 0;
  let skippedSensitiveCount = 0;

  for (const def of selectedDefs) {
    const scanned = scanBucket(home, def, true);
    skippedSensitiveCount += scanned.skippedSensitiveCount;
    for (const file of scanned.files) {
      if (CRED_SKIP_RE.test(path.basename(file.relPath)) || CRED_SKIP_RE.test(file.relPath)) {
        skippedSensitiveCount += 1;
        continue;
      }
      if (minAgeDays > 0 && file.mtimeMs > cutoffMs) continue;
      if (!isInsideDir(home, file.absPath)) continue;
      totalBytes += file.size;
      candidates.push({
        bucket: def.id,
        relPath: file.relPath,
        absPath: file.absPath,
        size: file.size,
        mtimeMs: file.mtimeMs,
      });
    }
  }

  return {
    ok: true,
    dryRun: true,
    dshHome: home,
    buckets: selectedDefs.map((d) => d.id),
    rejectedBuckets,
    minAgeDays,
    itemCount: candidates.length,
    totalBytes,
    humanTotalSize: formatBytesHuman(totalBytes),
    skippedSensitiveCount,
    truncatedCount: Math.max(0, candidates.length - RECYCLE_PREVIEW_CAP),
    items: candidates.slice(0, RECYCLE_PREVIEW_CAP).map(({ bucket, relPath, size, mtimeMs }) => ({
      bucket,
      relPath,
      size,
      mtime: new Date(mtimeMs).toISOString(),
    })),
    _candidates: options._includeInternalCandidates ? candidates : undefined,
  };
}

/**
 * L2 reversible cleanup: moves eligible files into `<dshHome>/guard-recycle/<batchId>/items/...`
 * with a structured manifest and audit log entry. Never permanently deletes files.
 */
export function executeStorageCleanup(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  if (options.dryRun === true) {
    return planStorageCleanup(home, options);
  }

  const plan = planStorageCleanup(home, { ...options, _includeInternalCandidates: true });
  if (!plan.ok) return plan;

  const candidates = plan._candidates || [];
  if (candidates.length === 0) {
    return {
      ok: true,
      dryRun: false,
      batchId: null,
      buckets: plan.buckets,
      minAgeDays: plan.minAgeDays,
      movedCount: 0,
      movedBytes: 0,
      humanMovedSize: '0 B',
      skippedSensitiveCount: plan.skippedSensitiveCount,
      message: '当前所选分区与时间条件下没有符合清理条件的文件。',
    };
  }

  const now = new Date();
  const stamp = [
    now.getUTCFullYear(),
    String(now.getUTCMonth() + 1).padStart(2, '0'),
    String(now.getUTCDate()).padStart(2, '0'),
    '-',
    String(now.getUTCHours()).padStart(2, '0'),
    String(now.getUTCMinutes()).padStart(2, '0'),
    String(now.getUTCSeconds()).padStart(2, '0'),
    '-',
    String(now.getUTCMilliseconds()).padStart(3, '0'),
  ].join('');
  const batchId = `${stamp}-${crypto.randomBytes(3).toString('hex')}`;
  const batchDir = path.join(home, 'guard-recycle', batchId);
  const itemsRoot = path.join(batchDir, 'items');
  fs.mkdirSync(itemsRoot, { recursive: true });

  const movedItems = [];
  const errors = [];
  let movedBytes = 0;

  for (const cand of candidates) {
    if (CRED_SKIP_RE.test(cand.relPath)) continue;
    const destPath = path.join(itemsRoot, cand.relPath);
    if (!isInsideDir(itemsRoot, destPath) || !isInsideDir(home, cand.absPath)) continue;
    try {
      fs.mkdirSync(path.dirname(destPath), { recursive: true });
      fs.renameSync(cand.absPath, destPath);
      movedBytes += cand.size;
      movedItems.push({
        bucket: cand.bucket,
        relPath: cand.relPath,
        recycleRelPath: path.join('items', cand.relPath).replace(/\\/g, '/'),
        size: cand.size,
        mtimeMs: cand.mtimeMs,
      });
    } catch (err) {
      errors.push(`${cand.relPath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  const manifest = {
    id: batchId,
    createdAt: now.toISOString(),
    buckets: plan.buckets,
    minAgeDays: plan.minAgeDays,
    itemCount: movedItems.length,
    totalBytes: movedBytes,
    restoredAt: null,
    restoredCount: 0,
    items: movedItems,
  };
  atomicWriteText(path.join(batchDir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);

  appendAuditEntry(home, {
    action: 'storage.recycle',
    target: 'global',
    summary: `回收 ${movedItems.length} 项 (${formatBytesHuman(movedBytes)}) [${plan.buckets.join(', ')}]`,
    snapshotId: batchId,
    ok: errors.length === 0,
  });

  return {
    ok: errors.length === 0,
    dryRun: false,
    batchId,
    buckets: plan.buckets,
    minAgeDays: plan.minAgeDays,
    movedCount: movedItems.length,
    movedBytes,
    humanMovedSize: formatBytesHuman(movedBytes),
    skippedSensitiveCount: plan.skippedSensitiveCount,
    errors,
    message: `已将 ${movedItems.length} 个文件 (${formatBytesHuman(movedBytes)}) 移入可逆回收站批次 ${batchId}`,
  };
}

/**
 * Restore all eligible files from a `<dshHome>/guard-recycle/<batchId>` batch back to their original paths.
 */
export function restoreRecycleBatch(dshHome, batchId, options = {}) {
  const home = resolveDshHome(dshHome);
  const id = String(batchId || '').trim();
  if (!/^[a-z0-9._-]{4,64}$/i.test(id)) {
    return { ok: false, error: '无效的回收批次 ID' };
  }

  const batchDir = path.join(home, 'guard-recycle', id);
  const manifestPath = path.join(batchDir, 'manifest.json');
  if (!isInsideDir(path.join(home, 'guard-recycle'), batchDir) || !fs.existsSync(manifestPath)) {
    return { ok: false, error: `未找到回收批次 ${id}` };
  }

  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  } catch {
    return { ok: false, error: `回收清单损坏: ${id}` };
  }

  let restoredCount = 0;
  let restoredBytes = 0;
  const conflicts = [];
  const missing = [];
  const errors = [];

  for (const item of manifest.items || []) {
    const relPath = String(item.relPath || '');
    const recycleRel = String(item.recycleRelPath || '');
    if (!relPath || !recycleRel || CRED_SKIP_RE.test(relPath)) continue;

    const srcPath = path.join(batchDir, recycleRel);
    const dstPath = path.join(home, relPath);
    if (!isInsideDir(batchDir, srcPath) || !isInsideDir(home, dstPath)) continue;

    if (!fs.existsSync(srcPath)) {
      missing.push(relPath);
      continue;
    }
    if (fs.existsSync(dstPath) && options.overwrite !== true) {
      conflicts.push(relPath);
      continue;
    }

    try {
      fs.mkdirSync(path.dirname(dstPath), { recursive: true });
      fs.renameSync(srcPath, dstPath);
      restoredCount += 1;
      restoredBytes += Number(item.size) || 0;
    } catch (err) {
      errors.push(`${relPath}: ${err instanceof Error ? err.message : String(err)}`);
    }
  }

  manifest.restoredAt = new Date().toISOString();
  manifest.restoredCount = (Number(manifest.restoredCount) || 0) + restoredCount;
  atomicWriteText(manifestPath, `${JSON.stringify(manifest, null, 2)}\n`);

  const allOk = errors.length === 0;
  appendAuditEntry(home, {
    action: 'storage.restore',
    target: 'global',
    summary: `还原回收批次 ${id}: 恢复 ${restoredCount} 项 (${formatBytesHuman(restoredBytes)})`,
    snapshotId: id,
    ok: allOk,
  });

  return {
    ok: allOk,
    batchId: id,
    restoredCount,
    restoredBytes,
    humanRestoredSize: formatBytesHuman(restoredBytes),
    conflicts,
    missing,
    errors,
    message: `已还原 ${restoredCount} 个文件 (${formatBytesHuman(restoredBytes)})${conflicts.length > 0 ? `，另有 ${conflicts.length} 个目标已存在而跳过` : ''}`,
  };
}

export function formatStorageHygieneText(report) {
  if (!report?.ok) return `[dsh-guard] 存储审计失败: ${report?.error || '未知错误'}`;
  const lines = [
    `[dsh-guard] 存储占用热点 · DSH Home: ${report.dshHome} · 总占用 ${report.humanTotalSize}（可逆安全清理 ${report.humanSafeReclaimableSize}）`,
  ];
  for (const b of report.buckets || []) {
    if (!b.exists && b.fileCount === 0) continue;
    lines.push(`- ${b.label}: ${b.humanSize} · ${b.fileCount} 文件 [${b.safetyLevel}]`);
  }
  if ((report.recycleBatches || []).length > 0) {
    lines.push(`回收站历史批次: ${report.recycleBatches.length} 个`);
  }
  return lines.join('\n');
}

function getDefaultDesktopLogsDir() {
  if (process.platform === 'win32') {
    const appData = process.env.APPDATA || path.join(os.homedir(), 'AppData', 'Roaming');
    return path.join(appData, '@deepseek-ai', 'dsh-desktop', 'logs');
  }
  if (process.platform === 'darwin') {
    return path.join(os.homedir(), 'Library', 'Application Support', '@deepseek-ai', 'dsh-desktop', 'logs');
  }
  const configHome = process.env.XDG_CONFIG_HOME || path.join(os.homedir(), '.config');
  return path.join(configHome, '@deepseek-ai', 'dsh-desktop', 'logs');
}

function escapeRegExp(str) {
  return String(str).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Redact home directory paths, tokens, Bearer headers, and secret key=val literals from log text.
 */
export function redactLogLine(text) {
  let out = String(text ?? '').replace(/[\r\n\t]+/g, ' ').trim();
  const home = os.homedir();
  if (home) {
    const homeForward = home.replace(/\\/g, '/');
    out = out.replace(new RegExp(escapeRegExp(home), 'gi'), '<HOME>');
    out = out.replace(new RegExp(escapeRegExp(homeForward), 'gi'), '<HOME>');
  }
  out = out
    .replace(/[A-Za-z]:\\Users\\[^\\/\s"']+/gi, '<HOME>')
    .replace(/(?:\/Users|\/home)\/[^/\s"']+/g, '<HOME>')
    .replace(/\bsk-[a-zA-Z0-9_-]{8,}\b/g, 'sk-<REDACTED>')
    .replace(/(Bearer\s+)[A-Za-z0-9._~+/=-]{6,}/gi, '$1<REDACTED>')
    .replace(/\b(?:ghp_|gho_|ghu_|ghs_|ghr_|github_pat_|npm_|xox[baprs]-|AIza)[A-Za-z0-9_-]{6,}\b/g, '<TOKEN_REDACTED>')
    .replace(/((?:api[-_]?key|secret|token|password|passwd|auth(?:orization)?)\s*[:=]\s*["']?)([^\s"',;]{4,})/gi, '$1<REDACTED>')
    .replace(/-----BEGIN [A-Z0-9 ]+-----[\s\S]*$/i, '<PEM_REDACTED>');
  return out;
}

function readTailText(absPath, maxBytes = LOG_TAIL_BYTES) {
  const st = fs.lstatSync(absPath);
  const start = Math.max(0, st.size - maxBytes);
  const length = st.size - start;
  if (length <= 0) return { text: '', truncated: false, readBytes: 0, mtimeMs: st.mtimeMs };
  const buf = Buffer.alloc(length);
  const fd = fs.openSync(absPath, 'r');
  try {
    fs.readSync(fd, buf, 0, length, start);
  } finally {
    fs.closeSync(fd);
  }
  let text = buf.toString('utf8');
  if (start > 0) {
    const firstNl = text.indexOf('\n');
    if (firstNl >= 0) text = text.slice(firstNl + 1);
  }
  return { text, truncated: start > 0, readBytes: length, mtimeMs: st.mtimeMs };
}

function parseLineTimestamp(line) {
  const iso = /\b(\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?)\b/.exec(line);
  if (iso) {
    const ms = Date.parse(iso[1].includes('T') ? iso[1] : iso[1].replace(' ', 'T'));
    if (Number.isFinite(ms)) return ms;
  }
  return null;
}

const ERROR_LINE_RE = /(?:\b(?:error|fatal|uncaught|unhandled|econnrefused|enoent|etimedout|eacces|crash(?:ed)?)\b|\[err(?:or)?\]|❌)/i;
const WARN_LINE_RE = /(?:\b(?:warn(?:ing)?|deprecated|retry(?:ing)?|timeout|timed out|fallback)\b|\[warn\]|⚠)/i;
const STACK_FRAME_RE = /^\s*at\s+/;
const BOILERPLATE_SKIP_RE = /^(?:-{3,}.*-{3,}|\(no error-level .*captured\)|\(Use `.*--trace-warnings|\{"journal":)/i;

function inferLogComponent(line, fileName) {
  const bracket = /\[([a-z0-9_.:/@-]{2,36})\]/i.exec(line);
  if (bracket && !/^(?:error|err|warn|warning|info|debug|\d+)$/i.test(bracket[1])) {
    return bracket[1];
  }
  if (fileName.startsWith('crash-')) return 'dsh-host';
  return path.basename(fileName, path.extname(fileName)) || 'dsh';
}

function normalizeSignatureKey(redactedLine, component) {
  const stripped = redactedLine
    .replace(/^\[?\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\]?\s*/g, '')
    .replace(/\bexited with \d+\b/gi, 'exited with <N>')
    .replace(/\b0x[0-9a-f]+\b/gi, '<HEX>')
    .replace(/\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi, '<UUID>')
    .replace(/\bpid[=: ]*\d+\b/gi, 'pid=<N>')
    .replace(/\b\d{2,}\b/g, '<N>')
    .slice(0, 140);
  return `${component}:${stripped}`;
}

function buildDiagnosticSuggestion(text) {
  if (/declares no dsh\.bundle/i.test(text)) {
    return '检测到 package.json 声明了不包含 dsh.bundle 入口的包，建议在「总览」检查依赖与配置对齐。';
  }
  if (/no adapter registered|LlmError/i.test(text)) {
    return '检测到模型提供商适配器配置异常，建议检查 llm-pi-ai 配置或通过「快照」页签恢复最近健康配置。';
  }
  if (/econnrefused|etimedout|timed out|timeout|mcp/i.test(text)) {
    return '建议在「扩展」页签运行 MCP 回路连通性探测，或适当调高对应 MCP 服务器的 toolCallTimeoutMs。';
  }
  if (/enoent|cannot find module|module_not_found/i.test(text)) {
    return '建议检查「总览」中的断链诊断（可执行断链修复），并确认插件路径或工作区路径未被移动。';
  }
  if (/401|403|unauthorized|invalid.*key|api[-_ ]?key/i.test(text)) {
    return '建议在「凭据与模型」页签确认对应环境变量钥匙已注入（盲写更新后重启生效）。';
  }
  if (/yaml|syntaxerror|parse/i.test(text)) {
    return '建议核查配置文件格式，必要时可在「快照」页签一键恢复最近可用配置快照。';
  }
  if (/eacces|eperm|permission/i.test(text)) {
    return '检测到文件权限或占用冲突，建议检查目标目录访问权限或避免多进程并发写入。';
  }
  return '建议结合工作流切换至 Safe Mode 或查看最近配置变更与审计记录定位根因。';
}

/**
 * L0 bounded Log Radar across `<dshHome>/logs` and desktop host crash logs.
 * Caps reads to newest 40 files and last 256 KB per file; redacts secrets and homedir paths;
 * returns deduplicated signatures for low-token diagnostics.
 */
export function inspectLogRadar(dshHome, options = {}) {
  const home = resolveDshHome(dshHome);
  const hoursWindow = Math.max(
    1,
    Math.min(720, Number.isFinite(Number(options.hours)) ? Number(options.hours) : DEFAULT_HOURS_WINDOW),
  );
  const cutoffMs = Date.now() - hoursWindow * 3600000;
  const maxGroups = Math.max(1, Math.min(100, Number(options.maxGroups) || 25));

  const dshLogsDir = options.dshLogsDir ? path.resolve(String(options.dshLogsDir)) : path.join(home, 'logs');
  const desktopLogsDir = options.desktopLogsDir
    ? path.resolve(String(options.desktopLogsDir))
    : getDefaultDesktopLogsDir();

  const sourceDirs = [
    { name: 'dsh-logs', dir: dshLogsDir },
    { name: 'desktop-logs', dir: desktopLogsDir },
  ];

  const filePool = [];
  const sourceStats = [];

  for (const src of sourceDirs) {
    const exists = fs.existsSync(src.dir);
    let candidateCount = 0;
    if (exists) {
      try {
        const entries = fs.readdirSync(src.dir, { withFileTypes: true });
        for (const entry of entries) {
          if (entry.isSymbolicLink() || !entry.isFile()) continue;
          if (!LOG_FILE_RE.test(entry.name) || CRED_SKIP_RE.test(entry.name)) continue;
          const absPath = path.join(src.dir, entry.name);
          const st = fs.lstatSync(absPath);
          if (st.isSymbolicLink() || st.mtimeMs < cutoffMs) continue;
          candidateCount += 1;
          filePool.push({
            source: src.name,
            name: entry.name,
            absPath,
            mtimeMs: st.mtimeMs,
          });
        }
      } catch {
        // Ignore unreadable log directory.
      }
    }
    sourceStats.push({
      name: src.name,
      dir: redactLogLine(src.dir),
      exists,
      matchedFiles: candidateCount,
      scannedFiles: 0,
    });
  }

  filePool.sort((a, b) => b.mtimeMs - a.mtimeMs);
  const selectedFiles = filePool.slice(0, LOG_MAX_FILES);

  let totalScannedBytes = 0;
  let truncatedFiles = 0;
  let errorCount = 0;
  let warnCount = 0;
  const groups = new Map();

  for (const file of selectedFiles) {
    const srcStat = sourceStats.find((s) => s.name === file.source);
    if (srcStat) srcStat.scannedFiles += 1;

    let readRes;
    try {
      readRes = readTailText(file.absPath, LOG_TAIL_BYTES);
    } catch {
      continue;
    }
    totalScannedBytes += readRes.readBytes;
    if (readRes.truncated) truncatedFiles += 1;

    const lines = readRes.text.split(/\r?\n/);
    for (const rawLine of lines) {
      if (!rawLine || STACK_FRAME_RE.test(rawLine) || BOILERPLATE_SKIP_RE.test(rawLine.trim())) continue;
      const isErr = ERROR_LINE_RE.test(rawLine);
      const isWarn = !isErr && WARN_LINE_RE.test(rawLine);
      if (!isErr && !isWarn) continue;

      const tsMs = parseLineTimestamp(rawLine) ?? readRes.mtimeMs;
      if (tsMs < cutoffMs) continue;

      const severity = isErr ? 'error' : 'warn';
      if (isErr) errorCount += 1;
      else warnCount += 1;

      const redacted = redactLogLine(rawLine).slice(0, 240);
      const component = inferLogComponent(redacted, file.name);
      const sigKey = `${severity}:${normalizeSignatureKey(redacted, component)}`;
      const tsIso = new Date(tsMs).toISOString();

      const existing = groups.get(sigKey);
      if (existing) {
        existing.count += 1;
        if (tsIso < existing.firstSeen) existing.firstSeen = tsIso;
        if (tsIso > existing.lastSeen) existing.lastSeen = tsIso;
        if (!existing.files.includes(file.name) && existing.files.length < 3) {
          existing.files.push(file.name);
        }
      } else {
        groups.set(sigKey, {
          signature: sigKey,
          severity,
          component,
          count: 1,
          firstSeen: tsIso,
          lastSeen: tsIso,
          files: [file.name],
          sample: redacted,
          suggestion: buildDiagnosticSuggestion(redacted),
        });
      }
    }
  }

  const signatures = Array.from(groups.values())
    .sort((a, b) => {
      if (a.severity !== b.severity) return a.severity === 'error' ? -1 : 1;
      if (b.count !== a.count) return b.count - a.count;
      return String(b.lastSeen).localeCompare(String(a.lastSeen));
    })
    .slice(0, maxGroups);

  return {
    ok: true,
    checkedAt: new Date().toISOString(),
    hoursWindow,
    scannedFiles: selectedFiles.length,
    totalScannedBytes,
    humanScannedSize: formatBytesHuman(totalScannedBytes),
    truncatedFiles,
    errorCount,
    warnCount,
    uniqueCount: groups.size,
    sources: sourceStats,
    signatures,
  };
}

export function formatLogRadarText(report) {
  if (!report?.ok) return `[dsh-guard] 日志雷达分析失败: ${report?.error || '未知错误'}`;
  const lines = [
    `[dsh-guard] 日志异常雷达 · 窗口 ${report.hoursWindow}h · 扫描 ${report.scannedFiles} 文件 (${report.humanScannedSize}) · 错误 ${report.errorCount} / 警告 ${report.warnCount}（去重 ${report.uniqueCount} 类）`,
  ];
  if (!report.signatures || report.signatures.length === 0) {
    lines.push('近期窗口内未发现异常错误或警告信号。');
    return lines.join('\n');
  }
  for (const sig of report.signatures.slice(0, 10)) {
    lines.push(`- [${sig.severity.toUpperCase()} x${sig.count}] (${sig.component}) ${sig.sample}`);
    lines.push(`  建议: ${sig.suggestion}`);
  }
  return lines.join('\n');
}
