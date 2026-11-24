import crypto from 'node:crypto';
import fs from 'node:fs';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';

export const STANDARD_PROFILES = new Set(['web', 'desktop']);
export const PROFILE_NAME_RE = /^(?:web|desktop)$/i;

export function resolveDshHome(overrideHome) {
  if (overrideHome && String(overrideHome).trim()) {
    return path.resolve(String(overrideHome).trim());
  }
  if (process.env.DSH_HOME && process.env.DSH_HOME.trim()) {
    return path.resolve(process.env.DSH_HOME.trim());
  }
  return path.join(os.homedir(), '.dsh');
}

const PROFILE_DIR_HINT_RE = /[\\/]profiles[\\/](desktop|web)(?:[\\/]|$)/i;
const LOOPBACK_HOSTS = new Set(['127.0.0.1', 'localhost', '::1', '[::1]']);
const SCRIPT_EXT_RE = /\.(?:m?[jt]s|cjs|py|sh|ps1)$/i;
const URL_LIKE_RE = /^[a-z][a-z0-9+.-]*:\/\//i;
const CRED_NAME_SKIP_RE = /(?:cred|secret|token|auth|\.pem$|\.key$)/i;
const PRESET_SKIP_DIRS = new Set(['.system', 'node_modules']);
const AUDIT_REL_DIR = 'guard-state';
const AUDIT_FILE_NAME = 'audit.jsonl';
const AUDIT_MAX_LINES = 500;
const AUDIT_KEEP_LINES = 300;
const SKILL_DESC_LIMIT = 140;

export function atomicWriteText(filePath, content, secureMode = false) {
  const dir = path.dirname(filePath);
  fs.mkdirSync(dir, { recursive: true });
  const tmpPath = `${filePath}.tmp-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`;
  const opts = secureMode && process.platform !== 'win32' ? { encoding: 'utf8', mode: 0o600 } : 'utf8';
  fs.writeFileSync(tmpPath, content, opts);
  fs.renameSync(tmpPath, filePath);
  if (secureMode && process.platform !== 'win32') {
    try {
      fs.chmodSync(filePath, 0o600);
    } catch {
      // Ignore chmod failures on restricted filesystems.
    }
  }
}

function sha256Text(text) {
  return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
}

function estimateTokensFromBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n <= 0) return 0;
  return Math.ceil(n / 3);
}

/**
 * Normalize a requested DSH profile target strictly to official 'web' or 'desktop'.
 * Custom/private or unrecognized profile names are intentionally ignored.
 */
export function normalizeProfileTarget(target, fallback = null) {
  if (typeof target !== 'string') return fallback;
  const trimmed = target.trim().toLowerCase();
  if (trimmed === 'web' || trimmed === 'desktop') return trimmed;
  return fallback;
}

/**
 * Strict profile validator: companion management scopes strictly to standard 'web' or 'desktop'.
 */
export function validateProfileName(name) {
  return normalizeProfileTarget(name, null);
}

function collectCtxPathHints(ctx) {
  const hints = [];
  if (!ctx || typeof ctx !== 'object') return hints;
  const push = (fn) => {
    try {
      const val = fn();
      if (typeof val === 'string' && val) hints.push(val);
    } catch {
      // Cordis strict service Proxy throws on undeclared properties/services.
    }
  };
  if (typeof ctx.get === 'function') {
    push(() => ctx.get('loader')?.baseDir);
    push(() => ctx.get('root')?.baseDir);
  }
  push(() => ctx.baseDir);
  push(() => ctx.root?.baseDir);
  push(() => ctx.loader?.baseDir);
  return hints;
}

/**
 * Automatically detect whether the current plugin runtime lives inside DSH Desktop or DSH Web.
 */
export function detectActiveProfile(options = {}) {
  const explicit = normalizeProfileTarget(options.target || options.activeProfile);
  if (explicit) return explicit;

  const env = options.env || process.env || {};
  const fromEnv = normalizeProfileTarget(env.DSH_PROFILE);
  if (fromEnv) return fromEnv;

  const ctx = options.ctx || null;
  const pathHints = [
    ...collectCtxPathHints(ctx),
    env.DSH_PROFILE_DIR,
  ];
  for (const hint of pathHints) {
    if (typeof hint === 'string' && hint) {
      const m = PROFILE_DIR_HINT_RE.exec(hint);
      if (m && m[1]) return m[1].toLowerCase();
    }
  }

  const argv = Array.isArray(options.argv) ? options.argv : process.argv;
  if (Array.isArray(argv)) {
    for (let i = 0; i < argv.length; i += 1) {
      const arg = String(argv[i] || '');
      if (arg === '--profile' && i + 1 < argv.length) {
        const val = normalizeProfileTarget(argv[i + 1]);
        if (val) return val;
      } else if (arg.startsWith('--profile=')) {
        const val = normalizeProfileTarget(arg.slice('--profile='.length));
        if (val) return val;
      }
      if (/dsh-desktop-host/i.test(arg)) return 'desktop';
      const dirMatch = PROFILE_DIR_HINT_RE.exec(arg);
      if (dirMatch && dirMatch[1]) return dirMatch[1].toLowerCase();
    }
  }

  const cwd = typeof options.cwd === 'string' ? options.cwd : process.cwd();
  if (cwd) {
    const cwdMatch = PROFILE_DIR_HINT_RE.exec(cwd);
    if (cwdMatch && cwdMatch[1]) return cwdMatch[1].toLowerCase();
  }

  const versions = options.versions || process.versions || {};
  if (versions.electron) return 'desktop';

  if (options.preferExistingOnCli && options.dshHome) {
    const hasPkg = (p) => {
      try {
        const raw = fs.readFileSync(path.join(options.dshHome, 'profiles', p, 'package.json'), 'utf8');
        const pkg = JSON.parse(raw);
        return pkg?.dependencies && Object.keys(pkg.dependencies).length > 0;
      } catch {
        return false;
      }
    };
    if (!hasPkg('web') && hasPkg('desktop')) return 'desktop';
  }

  return 'web';
}

/**
 * Probe only official standard DSH profiles ('web' / 'desktop') that exist on disk.
 * Never scans custom/headless/private profile directories.
 */
export function discoverProfiles(dshHome) {
  const found = [];
  for (const name of ['web', 'desktop']) {
    const dir = path.join(dshHome, 'profiles', name);
    const hasMarker =
      fs.existsSync(path.join(dir, 'package.json')) ||
      fs.existsSync(path.join(dir, 'cordis.patch.yml')) ||
      fs.existsSync(path.join(dir, 'cordis.yml'));
    if (hasMarker) found.push(name);
  }
  return found;
}

function getAuditFilePath(dshHome) {
  return path.join(dshHome, AUDIT_REL_DIR, AUDIT_FILE_NAME);
}

function sanitizeAuditField(val, maxLen = 160) {
  return String(val ?? '')
    .replace(/[\r\n\t]+/g, ' ')
    .trim()
    .slice(0, maxLen);
}

/**
 * Append a redacted configuration mutation audit entry into `<dshHome>/guard-state/audit.jsonl`.
 * Accepts ONLY allowlisted structural fields (never stores secrets or raw payloads).
 */
export function appendAuditEntry(dshHome, entry = {}) {
  if (!dshHome) return null;
  try {
    const filePath = getAuditFilePath(dshHome);
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    const record = {
      ts: new Date().toISOString(),
      action: sanitizeAuditField(entry.action || 'unknown', 48),
      target: sanitizeAuditField(entry.target || 'global', 64),
      summary: sanitizeAuditField(entry.summary || '', 220),
      snapshotId: sanitizeAuditField(entry.snapshotId || '', 96),
      ok: entry.ok !== false,
    };
    fs.appendFileSync(filePath, `${JSON.stringify(record)}\n`, 'utf8');

    const raw = fs.readFileSync(filePath, 'utf8');
    const lines = raw.split('\n').filter((line) => line.trim());
    if (lines.length > AUDIT_MAX_LINES) {
      const trimmed = `${lines.slice(-AUDIT_KEEP_LINES).join('\n')}\n`;
      atomicWriteText(filePath, trimmed);
    }
    return record;
  } catch {
    return null;
  }
}

/**
 * Read the most recent redacted change audit entries in reverse chronological order.
 */
export function readAuditLog(dshHome, limit = 25) {
  const max = Number.isFinite(Number(limit)) ? Math.max(1, Math.min(100, Number(limit))) : 25;
  const filePath = getAuditFilePath(dshHome);
  if (!fs.existsSync(filePath)) return [];
  try {
    const raw = fs.readFileSync(filePath, 'utf8');
    const lines = raw.split('\n').filter((line) => line.trim());
    const recent = lines.slice(-max).reverse();
    const records = [];
    for (const line of recent) {
      try {
        const parsed = JSON.parse(line);
        if (parsed && typeof parsed === 'object') {
          records.push({
            ts: String(parsed.ts || ''),
            action: String(parsed.action || ''),
            target: String(parsed.target || ''),
            summary: String(parsed.summary || ''),
            snapshotId: String(parsed.snapshotId || ''),
            ok: parsed.ok !== false,
          });
        }
      } catch {
        // Ignore malformed single lines cleanly.
      }
    }
    return records;
  } catch {
    return [];
  }
}

function commandExistsInPath(cmd) {
  const raw = String(cmd || '').trim();
  if (!raw) return false;
  if (raw.includes('/') || raw.includes('\\') || path.isAbsolute(raw)) {
    if (fs.existsSync(raw)) return true;
    if (process.platform === 'win32' && !path.extname(raw)) {
      const exts = (process.env.PATHEXT || '.EXE;.CMD;.BAT;.COM').split(';').filter(Boolean);
      return exts.some((ext) => fs.existsSync(`${raw}${ext.toLowerCase()}`) || fs.existsSync(`${raw}${ext.toUpperCase()}`));
    }
    return false;
  }

  const pathEnv = process.env.PATH || process.env.Path || '';
  const dirs = pathEnv.split(path.delimiter).filter(Boolean);
  const exts = process.platform === 'win32'
    ? (path.extname(raw) ? [''] : (process.env.PATHEXT || '.EXE;.CMD;.BAT;.COM').split(';').filter(Boolean))
    : [''];

  for (const dir of dirs) {
    for (const ext of exts) {
      const candidate = path.join(dir, `${raw}${ext}`);
      try {
        if (fs.existsSync(candidate) && fs.statSync(candidate).isFile()) {
          return true;
        }
      } catch {
        // Continue scanning PATH entries.
      }
    }
  }
  return false;
}

function findFirstScriptArg(args) {
  if (!Array.isArray(args)) return null;
  for (const raw of args) {
    const arg = String(raw || '').trim();
    if (!arg || arg.startsWith('-') || URL_LIKE_RE.test(arg)) continue;
    const looksPath =
      path.isAbsolute(arg) ||
      arg.startsWith('./') ||
      arg.startsWith('.\\') ||
      arg.startsWith('../') ||
      arg.startsWith('..\\') ||
      arg.includes('/') ||
      arg.includes('\\') ||
      SCRIPT_EXT_RE.test(arg);
    if (looksPath) return arg;
  }
  return null;
}

function probeLocalTcpPort(host, port, timeoutMs = 1200) {
  return new Promise((resolve) => {
    const started = Date.now();
    let settled = false;
    const socket = net.createConnection({ host, port });

    const done = (result) => {
      if (settled) return;
      settled = true;
      try {
        socket.destroy();
      } catch {
        // Ignore socket cleanup errors.
      }
      resolve(result);
    };

    socket.setTimeout(timeoutMs);
    socket.once('connect', () => {
      done({ status: 'reachable', latencyMs: Math.max(0, Date.now() - started) });
    });
    socket.once('timeout', () => {
      done({ status: 'unreachable', errorCode: 'ETIMEDOUT' });
    });
    socket.once('error', (err) => {
      done({
        status: 'unreachable',
        errorCode: err && err.code ? String(err.code) : 'ERR_CONNECT',
      });
    });
  });
}

/**
 * Safely probe a single MCP server entry without executing third-party code or hitting remote URLs (SSRF-safe).
 */
export async function probeMcpServerEntry(entry, options = {}) {
  const timeoutMs = Number.isFinite(Number(options.timeoutMs)) ? Math.max(200, Math.min(5000, Number(options.timeoutMs))) : 1200;
  const profileDir = options.profileDir || '';
  const base = {
    id: String(entry?.id || ''),
    serverName: String(entry?.serverName || entry?.id || ''),
    transport: String(entry?.transport || (entry?.url ? 'sse' : 'stdio')),
    enabled: Boolean(entry?.enabled),
    toolCallTimeoutMs: Number(entry?.toolCallTimeoutMs || 60000),
  };

  if (entry?.disabled || !entry?.enabled) {
    return { ...base, status: 'disabled', healthy: true, detail: '已禁用，未激活' };
  }

  if (entry?.url) {
    let parsed;
    try {
      parsed = new URL(String(entry.url));
    } catch {
      return { ...base, status: 'invalid_url', healthy: false, detail: `URL 格式无效: ${entry.url}` };
    }
    const hostLower = parsed.hostname.toLowerCase();
    if (!LOOPBACK_HOSTS.has(hostLower)) {
      return {
        ...base,
        status: 'remote_skipped',
        healthy: true,
        detail: `远端地址跳过探测（仅探活本机回环以防 SSRF）：${parsed.host}`,
      };
    }
    const port = parsed.port
      ? Number(parsed.port)
      : parsed.protocol === 'https:'
        ? 443
        : 80;
    const probeHost = hostLower === '[::1]' ? '::1' : hostLower;
    const tcpRes = await probeLocalTcpPort(probeHost, port, timeoutMs);
    if (tcpRes.status === 'reachable') {
      return {
        ...base,
        status: 'reachable',
        healthy: true,
        latencyMs: tcpRes.latencyMs,
        detail: `本机端口可达 (${probeHost}:${port} · ${tcpRes.latencyMs}ms)`,
      };
    }
    return {
      ...base,
      status: 'unreachable',
      healthy: false,
      errorCode: tcpRes.errorCode,
      detail: `本机端口不可达 (${probeHost}:${port} · ${tcpRes.errorCode})`,
    };
  }

  const command = String(entry?.command || '').trim();
  if (!command) {
    return { ...base, status: 'incomplete', healthy: false, detail: 'stdio 缺少 command 配置' };
  }
  if (!commandExistsInPath(command)) {
    return { ...base, status: 'missing_command', healthy: false, detail: `未找到可执行命令: ${command}` };
  }

  const scriptArg = findFirstScriptArg(entry?.args);
  if (scriptArg) {
    const resolvedScript = path.isAbsolute(scriptArg)
      ? scriptArg
      : profileDir
        ? path.resolve(profileDir, scriptArg)
        : path.resolve(scriptArg);
    if (!fs.existsSync(resolvedScript)) {
      return {
        ...base,
        status: 'missing_script',
        healthy: false,
        detail: `命令存在 (${command})，但脚本路径不存在: ${scriptArg}`,
      };
    }
    return {
      ...base,
      status: 'ok',
      healthy: true,
      detail: `命令与脚本路径均就绪 (${command})`,
    };
  }

  return {
    ...base,
    status: 'ok',
    healthy: true,
    detail: `命令可用 (${command})`,
  };
}

/**
 * Probe configured MCP servers on the active profile safely without touching foreign profiles.
 */
export async function probeMcpServers(dshHome, extensionsByProfile, target = null, options = {}) {
  const availableKeys = Object.keys(extensionsByProfile || {})
    .map((k) => normalizeProfileTarget(k))
    .filter(Boolean);
  const resolvedTarget =
    normalizeProfileTarget(target) ||
    availableKeys[0] ||
    detectActiveProfile(options);

  const results = [];
  const ext = extensionsByProfile?.[resolvedTarget];
  const mcps = Array.isArray(ext?.mcpServers) ? ext.mcpServers : [];
  const profileDir = path.join(dshHome, 'profiles', resolvedTarget);
  for (const m of mcps) {
    const probed = await probeMcpServerEntry(m, { ...options, profileDir });
    results.push({ profile: resolvedTarget, ...probed });
  }

  const active = results.filter((r) => r.status !== 'disabled');
  const unhealthy = results.filter((r) => !r.healthy);
  return {
    target: resolvedTarget,
    checkedAt: new Date().toISOString(),
    total: results.length,
    activeCount: active.length,
    healthyCount: results.filter((r) => r.healthy && r.status !== 'disabled').length,
    unhealthyCount: unhealthy.length,
    results,
  };
}

export function formatMcpProbeText(report) {
  const lines = [
    `[dsh-guard MCP 探活] 当前端=${report.target} · 总计 ${report.total} 个（启用 ${report.activeCount} / 异常 ${report.unhealthyCount}）`,
  ];
  if (report.results.length === 0) {
    lines.push('当前档案未配置 MCP 服务。');
    return lines.join('\n');
  }
  for (const r of report.results) {
    const flag = r.status === 'disabled' ? 'OFF' : r.healthy ? 'OK' : 'WARN';
    lines.push(`- [${flag}] ${r.serverName} (${r.transport}, ${r.toolCallTimeoutMs}ms): ${r.detail}`);
  }
  return lines.join('\n');
}

function unquoteScalar(raw) {
  const s = String(raw || '').trim();
  if (!s) return '';
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    return s.slice(1, -1);
  }
  return s;
}

function parseMarkdownFrontmatter(rawText) {
  const eol = rawText.includes('\r\n') ? '\r\n' : '\n';
  const hasBom = rawText.startsWith('\uFEFF');
  const clean = hasBom ? rawText.slice(1) : rawText;
  const lines = clean.split(/\r?\n/);

  if (lines.length < 3 || lines[0].trim() !== '---') {
    return { hasFrontmatter: false, hasBom, eol, fmStart: -1, fmEnd: -1, fields: {} };
  }

  let fmEnd = -1;
  for (let i = 1; i < lines.length; i += 1) {
    if (lines[i].trim() === '---') {
      fmEnd = i;
      break;
    }
  }
  if (fmEnd === -1) {
    return { hasFrontmatter: false, hasBom, eol, fmStart: -1, fmEnd: -1, fields: {} };
  }

  const fields = {};
  for (let i = 1; i < fmEnd; i += 1) {
    const line = lines[i];
    if (!line.trim() || line.trim().startsWith('#') || /^\s/.test(line)) continue;
    const colonIdx = line.indexOf(':');
    if (colonIdx <= 0) continue;
    const key = line.slice(0, colonIdx).trim();
    const val = unquoteScalar(line.slice(colonIdx + 1));
    fields[key] = val;
  }
  return { hasFrontmatter: true, hasBom, eol, fmStart: 0, fmEnd, fields };
}

function rewriteFrontmatterKeys(rawText, updates) {
  const eol = rawText.includes('\r\n') ? '\r\n' : '\n';
  const hasBom = rawText.startsWith('\uFEFF');
  const clean = hasBom ? rawText.slice(1) : rawText;
  const lines = clean.split(/\r?\n/);
  const fm = parseMarkdownFrontmatter(rawText);

  if (!fm.hasFrontmatter) {
    const newKeys = [];
    for (const [k, v] of Object.entries(updates)) {
      if (v !== undefined && v !== null) newKeys.push(`${k}: ${v}`);
    }
    if (newKeys.length === 0) return rawText;
    const body = `${hasBom ? '\uFEFF' : ''}---${eol}${newKeys.join(eol)}${eol}---${eol}${clean}`;
    return body;
  }

  const fmLines = lines.slice(1, fm.fmEnd);
  const restLines = lines.slice(fm.fmEnd + 1);
  const nextFmLines = [];
  const handled = new Set();

  for (const line of fmLines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#') || /^\s/.test(line)) {
      nextFmLines.push(line);
      continue;
    }
    const colonIdx = line.indexOf(':');
    if (colonIdx <= 0) {
      nextFmLines.push(line);
      continue;
    }
    const key = line.slice(0, colonIdx).trim();
    if (Object.prototype.hasOwnProperty.call(updates, key)) {
      handled.add(key);
      const nextVal = updates[key];
      if (nextVal !== undefined && nextVal !== null) {
        nextFmLines.push(`${key}: ${nextVal}`);
      }
      // If nextVal === null, omit line (delete key cleanly).
    } else {
      nextFmLines.push(line);
    }
  }

  for (const [key, nextVal] of Object.entries(updates)) {
    if (!handled.has(key) && nextVal !== undefined && nextVal !== null) {
      nextFmLines.push(`${key}: ${nextVal}`);
    }
  }

  const rebuilt = `${hasBom ? '\uFEFF' : ''}---${eol}${nextFmLines.join(eol)}${eol}---${eol}${restLines.join(eol)}`;
  return rebuilt;
}

function readSkillDescriptor(filePath, opts) {
  let raw;
  try {
    raw = fs.readFileSync(filePath, 'utf8');
  } catch {
    return null;
  }
  const fm = parseMarkdownFrontmatter(raw);
  const fields = fm.fields || {};
  const rawName = String(fields.name || opts.fallbackName || path.basename(filePath, '.md')).trim();
  const rawDesc = String(fields.description || '').replace(/\s+/g, ' ').trim();
  const description = rawDesc.length > SKILL_DESC_LIMIT ? `${rawDesc.slice(0, SKILL_DESC_LIMIT - 1)}…` : rawDesc;
  const disableModel = String(fields['disable-model-invocation'] || '').toLowerCase() === 'true';
  const userInvocable = String(fields['user-invocable'] || '').toLowerCase() !== 'false';
  const bytes = Buffer.byteLength(raw, 'utf8');

  return {
    id: opts.id,
    name: rawName,
    description,
    source: opts.source,
    sourceLabel: opts.sourceLabel,
    packageName: opts.packageName || null,
    filePath,
    writable: Boolean(opts.writable),
    modelVisible: !disableModel,
    userInvocable,
    bytes,
    estTokens: estimateTokensFromBytes(bytes),
    fingerprint: sha256Text(raw),
  };
}

function scanUserSkillDir(rootDir, source, sourceLabel, results) {
  if (!rootDir || !fs.existsSync(rootDir)) return;
  let entries = [];
  try {
    entries = fs.readdirSync(rootDir, { withFileTypes: true });
  } catch {
    return;
  }

  for (const d of entries) {
    if (d.name.startsWith('.') || PRESET_SKIP_DIRS.has(d.name)) continue;
    const full = path.join(rootDir, d.name);
    if (d.isDirectory()) {
      const skillFile = path.join(full, 'SKILL.md');
      if (fs.existsSync(skillFile)) {
        const item = readSkillDescriptor(skillFile, {
          id: `${source}:${d.name}`,
          fallbackName: d.name,
          source,
          sourceLabel,
          writable: true,
        });
        if (item) results.push(item);
      }
    } else if (d.isFile() && d.name.toLowerCase().endsWith('.md')) {
      const base = d.name.slice(0, -3);
      const item = readSkillDescriptor(full, {
        id: `${source}:${d.name}`,
        fallbackName: base,
        source,
        sourceLabel,
        writable: true,
      });
      if (item) results.push(item);
    }
  }
}

function scanPluginSkillsInDir(pkgDir, pkgName, seenPluginSkills, results) {
  const rootSkill = path.join(pkgDir, 'SKILL.md');
  if (fs.existsSync(rootSkill)) {
    const dedupeKey = `${pkgName}::root`;
    if (!seenPluginSkills.has(dedupeKey)) {
      seenPluginSkills.add(dedupeKey);
      const shortPkg = pkgName.includes('/') ? pkgName.split('/').pop() : pkgName;
      const item = readSkillDescriptor(rootSkill, {
        id: `plugin:${pkgName}`,
        fallbackName: shortPkg,
        source: 'plugin',
        sourceLabel: `插件 · ${pkgName}`,
        packageName: pkgName,
        writable: false,
      });
      if (item) results.push(item);
    }
  }

  const skillsDir = path.join(pkgDir, 'skills');
  if (!fs.existsSync(skillsDir)) return;
  let subDirs = [];
  try {
    subDirs = fs.readdirSync(skillsDir, { withFileTypes: true });
  } catch {
    return;
  }

  for (const sub of subDirs) {
    if (!sub.isDirectory() || sub.name.startsWith('.')) continue;
    const skillFile = path.join(skillsDir, sub.name, 'SKILL.md');
    if (!fs.existsSync(skillFile)) continue;
    const dedupeKey = `${pkgName}::${sub.name}`;
    if (seenPluginSkills.has(dedupeKey)) continue;
    seenPluginSkills.add(dedupeKey);
    const item = readSkillDescriptor(skillFile, {
      id: `plugin:${pkgName}:${sub.name}`,
      fallbackName: sub.name,
      source: 'plugin',
      sourceLabel: `插件 · ${pkgName}`,
      packageName: pkgName,
      writable: false,
    });
    if (item) results.push(item);
  }
}

/**
 * Scan global user skills (~/.dsh/skills, ~/.agents/skills) and plugin-bundled SKILL.md files.
 * Returns metadata + byte/token estimates without exposing full prompt bodies.
 */
export function inspectSkills(dshHome, profileNames = ['web', 'desktop'], options = {}) {
  const skills = [];
  scanUserSkillDir(path.join(dshHome, 'skills'), 'user-dsh', '~/.dsh/skills', skills);

  const agentsHome = options.agentsHome
    ? path.resolve(String(options.agentsHome))
    : path.join(os.homedir(), '.agents');
  scanUserSkillDir(path.join(agentsHome, 'skills'), 'user-agents', '~/.agents/skills', skills);

  const seenPluginSkills = new Set();
  for (const pName of profileNames || []) {
    const valid = validateProfileName(pName);
    if (!valid) continue;
    const nmDir = path.join(dshHome, 'profiles', valid, 'node_modules');
    if (!fs.existsSync(nmDir)) continue;
    let entries = [];
    try {
      entries = fs.readdirSync(nmDir, { withFileTypes: true });
    } catch {
      continue;
    }
    for (const d of entries) {
      if (d.name.startsWith('.')) continue;
      if (d.name.startsWith('@') && d.isDirectory()) {
        try {
          const subs = fs.readdirSync(path.join(nmDir, d.name), { withFileTypes: true });
          for (const sub of subs) {
            if (!sub.isDirectory() && !sub.isSymbolicLink()) continue;
            scanPluginSkillsInDir(path.join(nmDir, d.name, sub.name), `${d.name}/${sub.name}`, seenPluginSkills, skills);
          }
        } catch {
          // Ignore unreadable scoped folder.
        }
      } else if (d.isDirectory() || d.isSymbolicLink()) {
        scanPluginSkillsInDir(path.join(nmDir, d.name), d.name, seenPluginSkills, skills);
      }
    }
  }

  skills.sort((a, b) => {
    if (a.writable !== b.writable) return a.writable ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  const modelVisibleCount = skills.filter((s) => s.modelVisible).length;
  const userInvocableCount = skills.filter((s) => s.userInvocable).length;
  const totalBytes = skills.reduce((sum, s) => sum + s.bytes, 0);

  return {
    total: skills.length,
    writableCount: skills.filter((s) => s.writable).length,
    modelVisibleCount,
    userInvocableCount,
    totalBytes,
    estTokens: estimateTokensFromBytes(totalBytes),
    skills,
  };
}

/**
 * Toggle disable-model-invocation / user-invocable keys losslessly in a writable user Skill markdown file.
 */
export function toggleSkillVisibility(dshHome, skillId, changes = {}, options = {}) {
  const id = String(skillId || '').trim();
  if (!id) return { ok: false, error: '缺少 Skill ID' };

  const activeProf = normalizeProfileTarget(options.target || options.activeProfile) || detectActiveProfile(options);
  const scanned = inspectSkills(dshHome, options.profileNames || [activeProf], options);
  const skill = scanned.skills.find((s) => s.id === id);
  if (!skill) {
    return { ok: false, error: `未找到技能 ${id}` };
  }
  if (!skill.writable) {
    return { ok: false, error: '插件内置技能为只读，请直接停用对应插件或调整用户技能' };
  }
  if (options.expectedFingerprint && skill.fingerprint !== options.expectedFingerprint) {
    return { ok: false, error: '该技能文件已在磁盘被修改，请刷新后再试' };
  }

  const raw = fs.readFileSync(skill.filePath, 'utf8');
  const updates = {};
  if (typeof changes.modelVisible === 'boolean') {
    updates['disable-model-invocation'] = changes.modelVisible ? null : 'true';
  }
  if (typeof changes.userInvocable === 'boolean') {
    updates['user-invocable'] = changes.userInvocable ? null : 'false';
  }
  if (Object.keys(updates).length === 0) {
    return { ok: false, error: '未指定需要修改的可见性参数' };
  }

  const nextContent = rewriteFrontmatterKeys(raw, updates);
  if (nextContent !== raw) {
    atomicWriteText(skill.filePath, nextContent);
  }

  const refreshed = readSkillDescriptor(skill.filePath, {
    id: skill.id,
    fallbackName: skill.name,
    source: skill.source,
    sourceLabel: skill.sourceLabel,
    writable: true,
  }) || skill;

  if (!options.skipAudit) {
    appendAuditEntry(dshHome, {
      action: 'skill.toggle',
      target: skill.source,
      summary: `${skill.name}: 模型可见=${refreshed.modelVisible ? '开' : '关'}, 命令可见=${refreshed.userInvocable ? '开' : '关'}`,
      ok: true,
    });
  }

  return {
    ok: true,
    skill: refreshed,
  };
}

/**
 * Collect metadata (byte sizes and token estimates only; never reads secret values or full content)
 * for permanent DSH prompt files, custom prompt files, and agent presets.
 */
export function inspectPromptsMetadata(dshHome, systemPromptChars = 0) {
  const files = [];
  const candidates = [
    { id: 'prompt-inject', label: 'prompt-inject.md (全局注入)', absPath: path.join(dshHome, 'prompt-inject.md') },
    { id: 'memory-md', label: 'MEMORY.md (全局长期记忆)', absPath: path.join(dshHome, 'MEMORY.md') },
  ];

  for (const c of candidates) {
    let bytes = 0;
    let exists = false;
    try {
      if (fs.existsSync(c.absPath)) {
        bytes = fs.statSync(c.absPath).size;
        exists = true;
      }
    } catch {
      exists = false;
    }
    files.push({
      id: c.id,
      label: c.label,
      exists,
      bytes,
      estTokens: estimateTokensFromBytes(bytes),
    });
  }

  const promptsDir = path.join(dshHome, 'prompts');
  let customPromptsCount = 0;
  let customPromptsBytes = 0;
  if (fs.existsSync(promptsDir)) {
    try {
      for (const f of fs.readdirSync(promptsDir, { withFileTypes: true })) {
        if (!f.isFile() || !f.name.toLowerCase().endsWith('.md') || CRED_NAME_SKIP_RE.test(f.name)) continue;
        customPromptsCount += 1;
        customPromptsBytes += fs.statSync(path.join(promptsDir, f.name)).size;
      }
    } catch {
      // Ignore unreadable prompts folder.
    }
  }
  files.push({
    id: 'custom-prompts',
    label: `prompts/*.md (${customPromptsCount} 个)`,
    exists: customPromptsCount > 0,
    bytes: customPromptsBytes,
    estTokens: estimateTokensFromBytes(customPromptsBytes),
  });

  const presetsDir = path.join(dshHome, '.agent-presets');
  let presetCount = 0;
  let presetBytes = 0;
  if (fs.existsSync(presetsDir)) {
    try {
      for (const f of fs.readdirSync(presetsDir, { withFileTypes: true })) {
        if (!f.isFile() || CRED_NAME_SKIP_RE.test(f.name)) continue;
        presetCount += 1;
        presetBytes += fs.statSync(path.join(presetsDir, f.name)).size;
      }
    } catch {
      // Ignore unreadable presets folder.
    }
  }

  if (systemPromptChars > 0) {
    files.push({
      id: 'system-prompt-patch',
      label: 'system-prompt (cordis.patch.yml)',
      exists: true,
      bytes: systemPromptChars,
      estTokens: estimateTokensFromBytes(systemPromptChars),
    });
  }

  const alwaysInjectedBytes = files
    .filter((f) => f.id === 'prompt-inject' || f.id === 'memory-md' || f.id === 'system-prompt-patch')
    .reduce((sum, f) => sum + f.bytes, 0);

  return {
    files,
    presetCount,
    presetBytes,
    alwaysInjectedBytes,
    alwaysInjectedTokens: estimateTokensFromBytes(alwaysInjectedBytes),
  };
}

/**
 * Audit startup context load strictly for the active profile ('web' or 'desktop').
 */
export function inspectContextAudit(dshHome, healthData, target = null, options = {}) {
  const activeProfile =
    normalizeProfileTarget(target) ||
    normalizeProfileTarget(healthData?.activeProfile) ||
    detectActiveProfile(options);

  const skillsReport = inspectSkills(dshHome, [activeProfile], options);
  const prof = healthData?.profiles?.[activeProfile] || healthData?.profile || {};
  const ext = healthData?.extensions?.[activeProfile] || { plugins: [], mcpServers: [] };
  const plugins = Array.isArray(ext.plugins) ? ext.plugins : [];
  const mcps = Array.isArray(ext.mcpServers) ? ext.mcpServers : [];

  const enabledPlugins = plugins.filter((p) => p.enabled && !p.isCore).length;
  const totalThirdPartyPlugins = plugins.filter((p) => !p.isCore).length;
  const enabledMcps = mcps.filter((m) => m.enabled).length;
  const promptMeta = inspectPromptsMetadata(dshHome, Number(prof.systemPromptChars || 0));

  // Heuristic startup footprint estimate: prompt tokens + ~450 tokens per active MCP + ~65 tokens per model-visible skill catalog entry
  const estimatedBaseTokens =
    promptMeta.alwaysInjectedTokens +
    enabledMcps * 450 +
    skillsReport.modelVisibleCount * 65;

  const level = estimatedBaseTokens >= 8000 || enabledMcps >= 8 || skillsReport.modelVisibleCount >= 40
    ? 'heavy'
    : estimatedBaseTokens >= 3500 || enabledMcps >= 4 || skillsReport.modelVisibleCount >= 20
      ? 'moderate'
      : 'light';

  const profileSummary = {
    profile: activeProfile,
    exists: Boolean(prof.exists),
    enabledPlugins,
    totalThirdPartyPlugins,
    enabledMcps,
    totalMcps: mcps.length,
    modelVisibleSkills: skillsReport.modelVisibleCount,
    estimatedBaseTokens,
    level,
    prompts: promptMeta,
  };

  const recommendations = [];
  if (enabledMcps >= 5) {
    recommendations.push(`当前已启用 ${enabledMcps} 台 MCP 服务；停用低频 MCP 可明显减少每轮工具 schema Token 占用`);
  }
  if (skillsReport.modelVisibleCount >= 25 && skillsReport.writableCount > 0) {
    recommendations.push(`共有 ${skillsReport.modelVisibleCount} 个技能对模型可见；可将低频用户技能切为仅手动 / 斜杠调用`);
  }
  if (promptMeta.alwaysInjectedBytes >= 12000) {
    recommendations.push(`全局常驻注入文件合计 ${Math.round(promptMeta.alwaysInjectedBytes / 1024)} KB；建议精简 prompt-inject.md / MEMORY.md，避免每轮上下文膨胀`);
  }
  if (enabledPlugins >= 15) {
    recommendations.push(`已启用 ${enabledPlugins} 个第三方插件；如感到启动变慢，可停用暂不使用的重型扩展`);
  }
  if (recommendations.length === 0) {
    recommendations.push('当前常驻提示词、MCP 数量与技能可见度处于轻量健康区间');
  }

  return {
    target: activeProfile,
    activeProfile,
    checkedAt: new Date().toISOString(),
    profile: profileSummary,
    profiles: [profileSummary],
    skills: skillsReport,
    recommendations,
  };
}

export function formatContextAuditText(audit) {
  const p = audit.profile || audit.profiles?.[0];
  const lines = [
    `[dsh-guard 上下文体检] 当前端=${audit.activeProfile || audit.target} · 技能总数 ${audit.skills.total}（模型可见 ${audit.skills.modelVisibleCount} / 可治理用户技能 ${audit.skills.writableCount}）`,
  ];
  if (p) {
    lines.push(
      `- ${p.profile}: 等级=${p.level} · 估算基线 ~${p.estimatedBaseTokens} tok · 启用插件 ${p.enabledPlugins}/${p.totalThirdPartyPlugins} · 启用 MCP ${p.enabledMcps}/${p.totalMcps} · 常驻注入 ${p.prompts.alwaysInjectedBytes} B`,
    );
  }
  for (const rec of audit.recommendations) {
    lines.push(`  建议: ${rec}`);
  }
  return lines.join('\n');
}
