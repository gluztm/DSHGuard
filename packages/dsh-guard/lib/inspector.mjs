import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import {
  appendAuditEntry,
  atomicWriteText,
  detectActiveProfile,
  discoverProfiles,
  formatContextAuditText,
  formatMcpProbeText,
  inspectContextAudit,
  inspectSkills,
  normalizeProfileTarget,
  probeMcpServers,
  readAuditLog,
  toggleSkillVisibility,
  validateProfileName,
} from './governance.mjs';

export const PACKAGE_VERSION = '2.4.3';
export const REPO_URL = 'https://github.com/gluztm/DSHGuard';
export const RELEASE_URL = `https://github.com/gluztm/DSHGuard/releases/tag/${PACKAGE_VERSION}`;
export const INSTALLER_URL = `https://github.com/gluztm/DSHGuard/releases/download/${PACKAGE_VERSION}/DSHGuard-Setup-${PACKAGE_VERSION}.exe`;

const SELF_PLUGIN_NAME = '@gluztm/dsh-guard';
const SELF_LOADER_IDS = new Set(['dsh-guard', SELF_PLUGIN_NAME]);
const MCP_CLIENT_PKG = '@deepseek-ai/dsh-mcp-client';
const LLM_PLUGIN_IDS = new Set(['llm-pi-ai', '@deepseek-ai/dsh-llm-pi-ai']);
const SYSTEM_PROMPT_IDS = new Set(['system-prompt', '@deepseek-ai/dsh-system-prompt']);
const RESTORABLE_REL_RE = /^(?:profiles\/(?:web|desktop)\/(?:cordis\.patch\.yml|cordis\.yml|package\.json)|settings\.yaml)$/;
const SNAPSHOT_SAFE_EXT_RE = /\.(?:ya?ml|json)$/i;
const CRED_SKIP_RE = /(?:cred|secret|token|auth|\.pem$|\.key$)/i;
const AUTO_SNAPSHOT_KEEP = 40;

function resolveTargetProfile(target, fallback = detectActiveProfile()) {
  if (target === undefined || target === null || target === '' || target === 'all') return fallback;
  return normalizeProfileTarget(String(target), null);
}

function safeReadText(filePath) {
  try {
    return fs.readFileSync(filePath, 'utf8');
  } catch {
    return null;
  }
}

function safeReadJson(filePath) {
  const raw = safeReadText(filePath);
  if (!raw) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function sha256Text(text) {
  return crypto.createHash('sha256').update(text, 'utf8').digest('hex');
}

function sha256File(filePath) {
  try {
    const buf = fs.readFileSync(filePath);
    return crypto.createHash('sha256').update(buf).digest('hex');
  } catch {
    return null;
  }
}

export function resolveDshHome(overrideHome) {
  if (overrideHome && String(overrideHome).trim()) {
    return path.resolve(String(overrideHome).trim());
  }
  if (process.env.DSH_HOME && process.env.DSH_HOME.trim()) {
    return path.resolve(process.env.DSH_HOME.trim());
  }
  return path.join(os.homedir(), '.dsh');
}

function readRegistryInstallLocation() {
  if (process.platform !== 'win32') return null;
  const regQueries = [
    { key: 'HKCU\\Software\\DSHGuard', valueName: 'InstallDir' },
    { key: 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{9E1F7C34-5B2A-4D18-9C77-6A0E5D3B21F4}_is1', valueName: 'InstallLocation' },
    { key: 'HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\{9E1F7C34-5B2A-4D18-9C77-6A0E5D3B21F4}_is1', valueName: 'InstallLocation' },
    { key: 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DSHGuard', valueName: 'InstallLocation' },
    { key: 'HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\DSHGuard', valueName: 'InstallLocation' },
  ];
  for (const { key, valueName } of regQueries) {
    try {
      const out = execFileSync('reg.exe', ['query', key, '/v', valueName], {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
        timeout: 1500,
      });
      const match = new RegExp(`${valueName}\\s+REG_\\w+\\s+(.+)`, 'i').exec(out);
      if (match && match[1]) {
        const dir = match[1].trim().replace(/[\\/]+$/, '');
        if (dir) return dir;
      }
    } catch {
      // Registry key absent on this machine.
    }
  }
  return null;
}

export function locateGuardInstallation(options = {}) {
  if (options.disableLookup) {
    return {
      installed: false,
      platform: process.platform,
      exePath: null,
      guardHome: null,
      configDir: null,
      snapshotsDir: null,
      dataDir: null,
      via: null,
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
    };
  }

  const candidates = [];
  const pushCandidate = (dir, via) => {
    if (!dir) return;
    const resolved = path.resolve(dir);
    candidates.push({ exe: path.join(resolved, 'DSHGuard.exe'), home: resolved, via });
  };

  if (options.guardHome) {
    pushCandidate(options.guardHome, 'option');
  }
  if (process.env.DSHGUARD_HOME) {
    pushCandidate(process.env.DSHGUARD_HOME, 'env:DSHGUARD_HOME');
  }

  if (process.platform === 'win32' && !options.guardHome) {
    const regDir = readRegistryInstallLocation();
    if (regDir) pushCandidate(regDir, 'registry');
    if (process.env.LOCALAPPDATA) {
      pushCandidate(path.join(process.env.LOCALAPPDATA, 'Programs', 'DSHGuard'), 'localappdata');
      pushCandidate(path.join(process.env.LOCALAPPDATA, 'DSHGuard'), 'localappdata-root');
    }
    pushCandidate('D:\\Applications\\DSHGuard', 'default-d-apps');
    pushCandidate('C:\\Program Files\\DSHGuard', 'program-files');
  }

  for (const item of candidates) {
    if (fs.existsSync(item.exe)) {
      const guardHome = path.dirname(item.exe);
      const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
      const dataCandidates = [
        path.join(guardHome, 'Snapshots'),
        path.join(localAppData, 'DSHGuard', 'Snapshots'),
      ];
      const snapshotsDir = dataCandidates.find((d) => fs.existsSync(d)) || dataCandidates[0];
      const configDir = path.join(guardHome, 'Config');
      return {
        installed: true,
        platform: process.platform,
        exePath: item.exe,
        guardHome,
        configDir: fs.existsSync(configDir) ? configDir : null,
        snapshotsDir,
        dataDir: path.dirname(snapshotsDir),
        via: item.via,
        releaseUrl: RELEASE_URL,
        installerUrl: INSTALLER_URL,
      };
    }
  }

  return {
    installed: false,
    platform: process.platform,
    exePath: null,
    guardHome: null,
    configDir: null,
    snapshotsDir: null,
    dataDir: null,
    via: null,
    releaseUrl: RELEASE_URL,
    installerUrl: INSTALLER_URL,
  };
}

function unquoteYamlScalar(raw) {
  const s = String(raw || '').trim();
  if (!s) return '';
  if ((s.startsWith('"') && s.endsWith('"')) || (s.startsWith("'") && s.endsWith("'"))) {
    const inner = s.slice(1, -1);
    if (s.startsWith('"')) {
      return inner.replace(/\\"/g, '"').replace(/\\\\/g, '\\').replace(/\\n/g, '\n');
    }
    return inner.replace(/''/g, "'");
  }
  return s;
}

function stripYamlComment(line) {
  let inSingle = false;
  let inDouble = false;
  let escaped = false;
  for (let i = 0; i < line.length; i += 1) {
    const ch = line[i];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (inDouble && ch === '\\') {
      escaped = true;
      continue;
    }
    if (ch === "'" && !inDouble) {
      inSingle = !inSingle;
      continue;
    }
    if (ch === '"' && !inSingle) {
      inDouble = !inDouble;
      continue;
    }
    if (ch === '#' && !inSingle && !inDouble) {
      if (i === 0 || /\s/.test(line[i - 1])) {
        return line.slice(0, i).replace(/\s+$/, '');
      }
    }
  }
  return line.replace(/\s+$/, '');
}

function splitKeyValue(trimmed) {
  let inSingle = false;
  let inDouble = false;
  let escaped = false;
  for (let i = 0; i < trimmed.length; i += 1) {
    const ch = trimmed[i];
    if (escaped) {
      escaped = false;
      continue;
    }
    if (inDouble && ch === '\\') {
      escaped = true;
      continue;
    }
    if (ch === "'" && !inDouble) {
      inSingle = !inSingle;
      continue;
    }
    if (ch === '"' && !inSingle) {
      inDouble = !inDouble;
      continue;
    }
    if (ch === ':' && !inSingle && !inDouble) {
      const next = trimmed[i + 1];
      if (next === undefined || /\s/.test(next)) {
        const key = unquoteYamlScalar(trimmed.slice(0, i).trim());
        const val = trimmed.slice(i + 1).trim();
        return { key, val };
      }
    }
  }
  return null;
}

function parseSimpleScalar(rawVal) {
  const s = String(rawVal || '').trim();
  if (!s) return undefined;
  if (s === 'true') return true;
  if (s === 'false') return false;
  if (s === 'null' || s === '~') return null;
  if (/^!!js\b/.test(s)) return { __jsTag: s };
  if (/^-?\d+(?:\.\d+)?$/.test(s)) return Number(s);
  if ((s.startsWith('[') && s.endsWith(']')) || (s.startsWith('{') && s.endsWith('}'))) {
    try {
      return JSON.parse(s.replace(/'([^'\\]*(?:\\.[^'\\]*)*)'/g, '"$1"'));
    } catch {
      return s;
    }
  }
  return unquoteYamlScalar(s);
}

/**
 * Indentation-driven lightweight YAML parser tailored for Cordis cordis.patch.yml / cordis.yml.
 * Preserves top-level patch structure (`- insert:` and `- id:`) without executing YAML tags.
 */
function parseYamlStructure(text) {
  if (!text) return [];
  const rawLines = text.replace(/\r\n/g, '\n').split('\n');
  const tokens = [];
  for (let idx = 0; idx < rawLines.length; idx += 1) {
    const clean = stripYamlComment(rawLines[idx]);
    if (!clean.trim()) continue;
    const indent = clean.length - clean.trimStart().length;
    tokens.push({ lineIdx: idx, indent, text: clean.trimStart(), raw: rawLines[idx] });
  }

  let cursor = 0;

  function parseBlock(minIndent) {
    if (cursor >= tokens.length) return null;
    const first = tokens[cursor];
    if (first.indent < minIndent) return null;
    if (first.text === '-' || first.text.startsWith('- ')) {
      return parseSequence(first.indent);
    }
    return parseMapping(first.indent);
  }

  function parseSequence(seqIndent) {
    const arr = [];
    while (cursor < tokens.length) {
      const tok = tokens[cursor];
      if (tok.indent < seqIndent) break;
      if (tok.indent !== seqIndent || !(tok.text === '-' || tok.text.startsWith('- '))) break;
      const startLine = tok.lineIdx;
      const afterDash = tok.text === '-' ? '' : tok.text.slice(2).trimStart();
      cursor += 1;

      if (!afterDash) {
        const val = cursor < tokens.length && tokens[cursor].indent > seqIndent
          ? parseBlock(seqIndent + 1)
          : null;
        if (val && typeof val === 'object' && !Array.isArray(val)) {
          val.__lineRange = [startLine, cursor > 0 ? tokens[cursor - 1].lineIdx : startLine];
        }
        arr.push(val);
        continue;
      }

      const kv = splitKeyValue(afterDash);
      if (kv) {
        const obj = {};
        const itemIndent = seqIndent + 2;
        if (kv.val === '|' || kv.val === '>') {
          const lines = [];
          while (cursor < tokens.length && tokens[cursor].indent >= itemIndent + 1) {
            lines.push(tokens[cursor].text);
            cursor += 1;
          }
          obj[kv.key] = lines.join('\n');
        } else if (!kv.val) {
          if (cursor < tokens.length && tokens[cursor].indent > seqIndent) {
            // Handle `- insert:` followed by same-indent `- ...` or deeper indent mapping/list
            obj[kv.key] = parseBlock(seqIndent + 1);
          } else {
            obj[kv.key] = null;
          }
        } else {
          obj[kv.key] = parseSimpleScalar(kv.val);
        }

        while (cursor < tokens.length) {
          const next = tokens[cursor];
          if (next.indent < itemIndent) break;
          if (next.indent === seqIndent && (next.text === '-' || next.text.startsWith('- '))) break;
          if (next.indent > itemIndent && !splitKeyValue(next.text)) {
            cursor += 1;
            continue;
          }
          if (next.indent === itemIndent) {
            const subKv = splitKeyValue(next.text);
            if (!subKv) {
              cursor += 1;
              continue;
            }
            cursor += 1;
            if (subKv.val === '|' || subKv.val === '>') {
              const lines = [];
              while (cursor < tokens.length && tokens[cursor].indent > itemIndent) {
                lines.push(tokens[cursor].text);
                cursor += 1;
              }
              obj[subKv.key] = lines.join('\n');
            } else if (!subKv.val) {
              obj[subKv.key] = cursor < tokens.length && tokens[cursor].indent > itemIndent
                ? parseBlock(itemIndent + 1)
                : null;
            } else {
              obj[subKv.key] = parseSimpleScalar(subKv.val);
            }
          } else {
            break;
          }
        }
        obj.__lineRange = [startLine, cursor > 0 ? tokens[cursor - 1].lineIdx : startLine];
        arr.push(obj);
      } else {
        arr.push(parseSimpleScalar(afterDash));
      }
    }
    return arr;
  }

  function parseMapping(mapIndent) {
    const obj = {};
    while (cursor < tokens.length) {
      const tok = tokens[cursor];
      if (tok.indent < mapIndent) break;
      if (tok.indent !== mapIndent) break;
      if (tok.text === '-' || tok.text.startsWith('- ')) break;
      const kv = splitKeyValue(tok.text);
      if (!kv) {
        cursor += 1;
        continue;
      }
      cursor += 1;
      if (kv.val === '|' || kv.val === '>') {
        const lines = [];
        while (cursor < tokens.length && tokens[cursor].indent > mapIndent) {
          lines.push(tokens[cursor].text);
          cursor += 1;
        }
        obj[kv.key] = lines.join('\n');
      } else if (!kv.val) {
        if (cursor < tokens.length) {
          const nxt = tokens[cursor];
          if (nxt.indent > mapIndent || (nxt.indent === mapIndent && (nxt.text === '-' || nxt.text.startsWith('- ')))) {
            obj[kv.key] = parseBlock(nxt.indent);
          } else {
            obj[kv.key] = null;
          }
        } else {
          obj[kv.key] = null;
        }
      } else {
        obj[kv.key] = parseSimpleScalar(kv.val);
      }
    }
    return obj;
  }

  const parsed = parseBlock(0);
  return parsed ?? [];
}

function collectPatchNodes(parsed) {
  const list = Array.isArray(parsed)
    ? parsed
    : Array.isArray(parsed?.plugins)
      ? parsed.plugins
      : [];
  const entries = [];
  for (const item of list) {
    if (!item || typeof item !== 'object') continue;
    if (item.insert !== undefined && item.insert !== null) {
      const inserts = Array.isArray(item.insert) ? item.insert : [item.insert];
      for (const ins of inserts) {
        if (ins && typeof ins === 'object') {
          entries.push({
            kind: 'insert',
            id: typeof ins.id === 'string' ? ins.id : null,
            name: typeof ins.name === 'string' ? ins.name : null,
            disabled: ins.disabled === true,
            disabledRaw: ins.disabled,
            config: ins.config && typeof ins.config === 'object' ? ins.config : {},
            lineRange: ins.__lineRange || item.__lineRange || null,
          });
        }
      }
      continue;
    }
    entries.push({
      kind: 'override',
      id: typeof item.id === 'string' ? item.id : null,
      name: typeof item.name === 'string' ? item.name : null,
      disabled: item.disabled === true,
      disabledRaw: item.disabled,
      hasConfig: item.config !== undefined,
      config: item.config && typeof item.config === 'object' ? item.config : {},
      keys: Object.keys(item).filter((k) => k !== '__lineRange'),
      lineRange: item.__lineRange || null,
    });
  }
  return entries;
}

export function quoteYamlId(id) {
  const s = String(id || '');
  if (!s) return '""';
  const needsQuote =
    /^(?:@|!|&|\*|\?|:|-|\[|\{|#|%|\||>|'|"|`)/.test(s) ||
    /^(?:true|false|null|yes|no|on|off|~)$/i.test(s) ||
    /^-?\d+(?:\.\d+)?$/.test(s) ||
    /[:#\s]/.test(s) ||
    /: /.test(s);
  if (!needsQuote) return s;
  return `"${s.replace(/\\/g, '\\\\').replace(/"/g, '\\"')}"`;
}

export function findBrokenModuleLinks(profileDir) {
  const nodeModulesDir = path.join(profileDir, 'node_modules');
  const broken = [];
  if (!fs.existsSync(nodeModulesDir)) return broken;

  const inspectEntry = (pkgName, fullPath) => {
    try {
      const st = fs.lstatSync(fullPath);
      if (!st.isSymbolicLink()) return;
      let brokenTarget = null;
      try {
        brokenTarget = fs.readlinkSync(fullPath);
      } catch {
        brokenTarget = null;
      }
      if (!fs.existsSync(fullPath)) {
        broken.push({ name: pkgName, linkPath: fullPath, brokenTarget });
      }
    } catch {
      // Ignore unreadable entries.
    }
  };

  let entries = [];
  try {
    entries = fs.readdirSync(nodeModulesDir, { withFileTypes: true });
  } catch {
    return broken;
  }

  for (const dirent of entries) {
    const fullPath = path.join(nodeModulesDir, dirent.name);
    if (dirent.name.startsWith('@') && !dirent.isSymbolicLink() && dirent.isDirectory()) {
      try {
        const scoped = fs.readdirSync(fullPath, { withFileTypes: true });
        for (const sub of scoped) {
          inspectEntry(`${dirent.name}/${sub.name}`, path.join(fullPath, sub.name));
        }
      } catch {
        // Ignore unreadable scoped folder.
      }
    } else if (!dirent.name.startsWith('.')) {
      inspectEntry(dirent.name, fullPath);
    }
  }
  return broken;
}

function collectApiKeyEnvs(node, outSet, depth = 0) {
  if (!node || typeof node !== 'object' || depth > 10) return;
  if (Array.isArray(node)) {
    for (const item of node) collectApiKeyEnvs(item, outSet, depth + 1);
    return;
  }
  for (const [k, v] of Object.entries(node)) {
    if (k === 'apiKeyEnv' && typeof v === 'string' && v.trim()) {
      outSet.add(v.trim());
    } else if (v && typeof v === 'object') {
      collectApiKeyEnvs(v, outSet, depth + 1);
    }
  }
}

export function inspectProfile(dshHome, profileName) {
  const profileDir = path.join(dshHome, 'profiles', profileName);
  const exists = fs.existsSync(profileDir);
  const pkgPath = path.join(profileDir, 'package.json');
  const patchPath = path.join(profileDir, 'cordis.patch.yml');
  const basePath = path.join(profileDir, 'cordis.yml');
  const pkg = safeReadJson(pkgPath);
  const patchText = safeReadText(patchPath);
  const baseText = safeReadText(basePath);
  const brokenLinks = exists ? findBrokenModuleLinks(profileDir) : [];

  const deps = pkg && typeof pkg.dependencies === 'object' && pkg.dependencies ? Object.keys(pkg.dependencies) : [];
  const depSet = new Set(deps);
  const bundleNames = pkg?.dsh?.bundles && typeof pkg.dsh.bundles === 'object' ? Object.keys(pkg.dsh.bundles) : [];
  const missingBundles = bundleNames.filter((b) => !depSet.has(b));
  const thirdPartyDeps = deps.filter((name) => !name.startsWith('@deepseek-ai/'));
  const parsedPatch = patchText ? parseYamlStructure(patchText) : null;
  const patchEntries = parsedPatch ? collectPatchNodes(parsedPatch) : [];
  const referencedEnvSet = new Set();
  if (parsedPatch) collectApiKeyEnvs(parsedPatch, referencedEnvSet);
  const mcpCount = patchEntries.filter((e) => e.kind === 'insert' && e.name === MCP_CLIENT_PKG).length;
  const disabledOverrides = patchEntries.filter((e) => e.disabled === true);
  const sysPromptNode = patchEntries.find((e) => SYSTEM_PROMPT_IDS.has(e.id || '') || SYSTEM_PROMPT_IDS.has(e.name || ''));
  const systemPromptChars = sysPromptNode?.config && typeof sysPromptNode.config === 'object'
    ? JSON.stringify(sysPromptNode.config).length
    : 0;

  return {
    name: profileName,
    exists,
    profileDir,
    packageJsonPresent: Boolean(pkg),
    totalDependencies: deps.length,
    thirdPartyCount: thirdPartyDeps.length,
    thirdPartyNames: thirdPartyDeps,
    missingBundles,
    referencedEnvKeys: Array.from(referencedEnvSet),
    mcpCount,
    disabledEntryCount: disabledOverrides.length,
    brokenLinks,
    brokenLinkCount: brokenLinks.length,
    systemPromptChars,
    patchPresent: patchText !== null,
    patchHash: patchText !== null ? sha256Text(patchText).slice(0, 12) : null,
    patchBytes: patchText !== null ? Buffer.byteLength(patchText, 'utf8') : 0,
    basePresent: baseText !== null,
    baseHash: baseText !== null ? sha256Text(baseText).slice(0, 12) : null,
  };
}

function parseCredentialRefsDocument(rawText) {
  const lines = rawText ? rawText.replace(/\r\n/g, '\n').split('\n') : [];
  let inRefs = false;
  let refsStart = -1;
  let refsEnd = -1;
  const entries = [];
  let current = null;

  const flushCurrent = () => {
    if (!current) return;
    entries.push(current);
    current = null;
  };

  for (let i = 0; i < lines.length; i += 1) {
    const line = lines[i];
    const trimmed = line.trim();

    if (/^\S/.test(line) && !trimmed.startsWith('#')) {
      if (inRefs) {
        flushCurrent();
        refsEnd = i;
        inRefs = false;
      }
      if (/^refs:\s*(?:#.*)?$/.test(line)) {
        inRefs = true;
        refsStart = i;
      } else if (/^refs:\s*\{\}\s*(?:#.*)?$/.test(line)) {
        refsStart = i;
        refsEnd = i + 1;
      }
      continue;
    }

    if (!inRefs) continue;
    if (!trimmed || trimmed.startsWith('#')) continue;

    const indent = line.length - line.trimStart().length;
    if (indent === 2 && !line.trimStart().startsWith('-')) {
      const kv = splitKeyValue(line.trimStart());
      if (kv && kv.key) {
        flushCurrent();
        const isProtectedAccount = /_ACCOUNT(?:_|$)/i.test(kv.key);
        const val = kv.val;
        const isBlock = val === '|' || val === '|-' || val === '|+' || val === '>' || val === '>-' || !val;
        const present = isBlock ? true : val !== '""' && val !== "''" && val !== 'null' && val !== '~';
        current = {
          key: kv.key,
          protected: isProtectedAccount,
          present,
          isMultiline: isBlock,
          startLine: i,
          endLine: i,
        };
      }
    } else if (current && indent > 2) {
      current.endLine = i;
      current.isMultiline = true;
      current.present = true;
    }
  }

  if (inRefs) {
    flushCurrent();
    refsEnd = lines.length;
  }

  return { lines, refsStart, refsEnd, entries };
}

/**
 * Inspect .credentials.yaml ONLY for key existence/readiness metadata.
 * Never reads or returns any secret literal, preview, or prefix.
 */
export function inspectCredentialKeysMasked(dshHome) {
  const credPath = path.join(dshHome, '.credentials.yaml');
  if (!fs.existsSync(credPath)) {
    return {
      present: false,
      path: credPath,
      count: 0,
      protectedCount: 0,
      keys: [],
    };
  }

  const raw = safeReadText(credPath);
  if (raw === null) {
    return {
      present: true,
      unreadable: true,
      path: credPath,
      count: 0,
      protectedCount: 0,
      keys: [],
    };
  }

  const doc = parseCredentialRefsDocument(raw);
  const seen = new Set();
  const keys = [];

  for (const entry of doc.entries) {
    if (!entry.key || seen.has(entry.key)) continue;
    seen.add(entry.key);
    keys.push({
      key: entry.key,
      protected: entry.protected,
      present: entry.present,
      source: 'refs',
      envSet: Boolean(process.env[entry.key]),
    });
  }

  // Fallback for flat legacy key: value files without refs: section
  if (doc.refsStart === -1) {
    for (const rawLine of doc.lines) {
      const clean = stripYamlComment(rawLine);
      if (!clean.trim() || /^\s/.test(clean) || clean.trimStart().startsWith('-')) continue;
      const kv = splitKeyValue(clean.trim());
      if (!kv || !kv.key || /^(?:version|records|refs)$/i.test(kv.key) || seen.has(kv.key)) continue;
      seen.add(kv.key);
      keys.push({
        key: kv.key,
        protected: /_ACCOUNT(?:_|$)/i.test(kv.key),
        present: Boolean(kv.val && kv.val !== '""' && kv.val !== "''" && kv.val !== 'null'),
        source: 'root',
        envSet: Boolean(process.env[kv.key]),
      });
    }
  }

  const nonEmptyKeysCount = keys.filter((k) => k.present).length;

  return {
    present: true,
    path: credPath,
    count: keys.length,
    nonEmptyKeysCount,
    protectedCount: keys.filter((k) => k.protected).length,
    missingReferencedKeys: [],
    keys,
  };
}

/**
 * One-way secret write into ~/.dsh/.credentials.yaml `refs:` preserving `version`, `records`,
 * comments, and existing `*_ACCOUNT_*` OAuth blocks. Never returns or logs the secret value.
 */
export function upsertCredentialKey(dshHome, keyName, secretValue) {
  const key = String(keyName || '').trim();
  if (!/^[A-Za-z_][A-Za-z0-9_.-]{1,95}$/.test(key)) {
    return { ok: false, error: '环境变量名格式无效（仅支持字母、数字、下划线、点或连字符）' };
  }
  if (/^(?:version|records|refs)$/i.test(key) || /_ACCOUNT(?:_|$)/i.test(key)) {
    return { ok: false, error: `保护键 ${key} 不可通过插件覆盖写入` };
  }
  const secret = typeof secretValue === 'string' ? secretValue.trim() : '';
  if (!secret || /[\r\n]/.test(secret) || secret.length > 4096) {
    return { ok: false, error: '密钥值不能为空且必须为单行（<= 4096 字符）' };
  }

  const credPath = path.join(dshHome, '.credentials.yaml');
  const escaped = secret.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
  const formattedLine = `  ${key}: "${escaped}"`;

  if (!fs.existsSync(credPath)) {
    const initial = `version: 1\nrecords: []\nrefs:\n${formattedLine}\n`;
    atomicWriteText(credPath, initial, true);
    appendAuditEntry(dshHome, {
      action: 'credential.upsert',
      target: 'credentials',
      summary: `初始化并单向写入凭据键 ${key}`,
      ok: true,
    });
    return { ok: true, key, path: credPath, created: true };
  }

  const raw = safeReadText(credPath);
  if (raw === null) {
    return { ok: false, error: '无法读取 .credentials.yaml' };
  }

  const doc = parseCredentialRefsDocument(raw);
  const lines = doc.lines.slice();
  const existing = doc.entries.find((e) => e.key === key);

  if (existing) {
    lines.splice(existing.startLine, existing.endLine - existing.startLine + 1, formattedLine);
  } else if (doc.refsStart !== -1) {
    if (/^refs:\s*\{\}/.test(lines[doc.refsStart])) {
      lines[doc.refsStart] = 'refs:';
      lines.splice(doc.refsStart + 1, 0, formattedLine);
    } else {
      let insertAt = doc.refsStart + 1;
      for (let i = doc.refsStart + 1; i < doc.refsEnd; i += 1) {
        if (lines[i].trim()) insertAt = i + 1;
      }
      lines.splice(insertAt, 0, formattedLine);
    }
  } else {
    while (lines.length > 0 && !lines[lines.length - 1].trim()) lines.pop();
    lines.push('refs:', formattedLine);
  }

  const output = `${lines.join('\n').replace(/\n+$/, '')}\n`;
  atomicWriteText(credPath, output, true);
  appendAuditEntry(dshHome, {
    action: 'credential.upsert',
    target: 'credentials',
    summary: `${existing ? '更新' : '新增'}凭据键 ${key}（单向盲写）`,
    ok: true,
  });
  return { ok: true, key, path: credPath, created: !existing };
}

export function inspectProviders(dshHome, target = null) {
  const maskedCreds = inspectCredentialKeysMasked(dshHome);
  const readyKeys = new Set(maskedCreds.keys.filter((k) => k.present).map((k) => k.key));
  const merged = new Map();
  const profileName = normalizeProfileTarget(target) || detectActiveProfile();

  for (const pName of [profileName]) {
    const profileDir = path.join(dshHome, 'profiles', pName);
    for (const fileName of ['cordis.patch.yml', 'cordis.yml']) {
      const text = safeReadText(path.join(profileDir, fileName));
      if (!text) continue;
      const nodes = collectPatchNodes(parseYamlStructure(text));
      const piNode = nodes.find((n) => LLM_PLUGIN_IDS.has(n.id || '') || LLM_PLUGIN_IDS.has(n.name || ''));
      const providers = piNode?.config?.providers;
      if (!providers || typeof providers !== 'object') continue;

      for (const [pid, cfg] of Object.entries(providers)) {
        if (!cfg || typeof cfg !== 'object') continue;
        const existing = merged.get(pid) || {
          id: pid,
          displayName: String(cfg.displayName || cfg.name || pid),
          api: String(cfg.api || 'openai-completions'),
          baseURL: String(cfg.baseURL || cfg.baseUrl || cfg.url || ''),
          apiKeyEnv: String(cfg.apiKeyEnv || ''),
          keyConfigured: false,
          profiles: [],
          models: [],
        };
        if (!existing.profiles.includes(pName)) {
          existing.profiles.push(pName);
        }
        if (Array.isArray(cfg.models) && existing.models.length === 0) {
          existing.models = cfg.models
            .map((m) => {
              if (typeof m === 'string') return { id: m, name: m };
              if (m && typeof m === 'object') {
                return { id: String(m.id || m.name || ''), name: String(m.name || m.id || '') };
              }
              return null;
            })
            .filter((m) => m && m.id);
        }
        existing.keyConfigured = existing.apiKeyEnv
          ? readyKeys.has(existing.apiKeyEnv) || Boolean(process.env[existing.apiKeyEnv])
          : false;
        merged.set(pid, existing);
      }
      break;
    }
  }

  return Array.from(merged.values());
}

function readPackageLoaderIds(profileDir, pkgName, guard) {
  const pkgDir = path.join(profileDir, 'node_modules', ...pkgName.split('/'));
  const pkgJson = safeReadJson(path.join(pkgDir, 'package.json'));
  const patchRel = pkgJson?.dsh?.bundle?.patch;
  const candidateFiles = [];
  if (typeof patchRel === 'string' && patchRel.trim()) {
    const resolved = path.resolve(pkgDir, patchRel.trim());
    if (resolved.startsWith(pkgDir)) candidateFiles.push(resolved);
  }
  candidateFiles.push(path.join(pkgDir, 'cordis.patch.yml'));

  const ids = [];
  const pushId = (val) => {
    if (typeof val === 'string' && val.trim() && !ids.includes(val.trim())) {
      ids.push(val.trim());
    }
  };

  for (const file of candidateFiles) {
    const text = safeReadText(file);
    if (!text) continue;
    const nodes = collectPatchNodes(parseYamlStructure(text));
    for (const n of nodes) {
      if (n.kind === 'insert' && n.id) pushId(n.id);
    }
    if (ids.length > 0) break;
  }

  if (guard?.configDir) {
    const mapJson = safeReadJson(path.join(guard.configDir, 'loader-ids.json'));
    const mapped = mapJson?.[pkgName];
    if (Array.isArray(mapped)) {
      for (const m of mapped) pushId(m);
    } else if (typeof mapped === 'string') {
      pushId(mapped);
    }
  }

  return { ids, description: typeof pkgJson?.description === 'string' ? pkgJson.description : '', version: pkgJson?.version || '' };
}

export function inspectExtensions(dshHome, target = null, guard = null) {
  const profileName = resolveTargetProfile(target) || detectActiveProfile();
  const profileDir = path.join(dshHome, 'profiles', profileName);
  const pkg = safeReadJson(path.join(profileDir, 'package.json'));
  const patchPath = path.join(profileDir, 'cordis.patch.yml');
  const patchText = safeReadText(patchPath) || '';
  const patchNodes = collectPatchNodes(parseYamlStructure(patchText));

  const disabledMap = new Map();
  for (const n of patchNodes) {
    if (n.id && n.kind === 'override') {
      if (n.disabled === true) disabledMap.set(n.id, true);
      else if (n.disabled === false || n.disabledRaw === false) disabledMap.set(n.id, false);
    }
  }

  const deps = pkg?.dependencies && typeof pkg.dependencies === 'object' ? pkg.dependencies : {};
  const plugins = [];
  for (const [name, versionSpec] of Object.entries(deps)) {
    const isCore = name.startsWith('@deepseek-ai/');
    const isSelf = SELF_LOADER_IDS.has(name);
    const meta = readPackageLoaderIds(profileDir, name, guard);
    const loaderIds = [...meta.ids];
    for (const n of patchNodes) {
      if (n.name === name && n.id && !loaderIds.includes(n.id)) {
        loaderIds.push(n.id);
      }
    }
    if (loaderIds.length === 0) {
      const short = name.includes('/') ? name.split('/').pop() : name;
      loaderIds.push(short);
      if (short !== name) loaderIds.push(name);
    }

    const disabled = loaderIds.some((id) => disabledMap.get(id) === true);
    plugins.push({
      name,
      version: meta.version || String(versionSpec || ''),
      description: meta.description,
      isCore,
      isSelf,
      canToggle: !isCore && !isSelf,
      loaderIds,
      primaryId: loaderIds[0],
      enabled: !disabled,
      disabled,
    });
  }

  plugins.sort((a, b) => {
    if (a.isSelf !== b.isSelf) return a.isSelf ? -1 : 1;
    if (a.isCore !== b.isCore) return a.isCore ? 1 : -1;
    return a.name.localeCompare(b.name);
  });

  const mcpServers = [];
  for (const n of patchNodes) {
    if (n.kind === 'insert' && n.name === MCP_CLIENT_PKG && n.id) {
      const overrideDisabled = disabledMap.get(n.id);
      const isDisabled = overrideDisabled !== undefined ? overrideDisabled : n.disabled === true;
      const cfg = n.config || {};
      const parsedTimeout = Number(cfg.toolCallTimeoutMs);
      const timeoutConfigured = Number.isFinite(parsedTimeout) && parsedTimeout > 0;
      mcpServers.push({
        id: n.id,
        serverName: String(cfg.serverName || n.id.replace(/^mcp-/, '')),
        transport: String(cfg.transport || (cfg.url ? 'sse' : 'stdio')),
        command: cfg.command ? String(cfg.command) : null,
        args: Array.isArray(cfg.args) ? cfg.args.map((a) => String(a)) : [],
        url: cfg.url ? String(cfg.url) : null,
        toolCallTimeoutMs: timeoutConfigured ? parsedTimeout : 60000,
        timeoutConfigured,
        enabled: !isDisabled,
        disabled: isDisabled,
        canToggle: true,
      });
    }
  }

  return {
    target: profileName,
    profileExists: fs.existsSync(profileDir),
    patchPath,
    plugins,
    mcpServers,
  };
}

export function repairBrokenModuleLinks(dshHome, target = null) {
  const profileName = resolveTargetProfile(target);
  if (!profileName) {
    return { ok: false, error: '仅支持 web 或 desktop 档案', target: String(target || ''), repairedCount: 0, unresolvableCount: 0, results: [] };
  }
  const profileDir = path.join(dshHome, 'profiles', profileName);
  const broken = findBrokenModuleLinks(profileDir);
  if (broken.length === 0) {
    return {
      ok: true,
      allRepaired: true,
      target: profileName,
      repairedCount: 0,
      unresolvableCount: 0,
      results: [],
      message: '当前档案无断裂依赖软链接',
    };
  }

  const otherProfile = profileName === 'desktop' ? 'web' : 'desktop';
  const results = [];

  for (const item of broken) {
    const parts = item.name.split('/');
    const candidates = [
      path.join(dshHome, '.dsh-module-fallback', 'node_modules', ...parts),
      path.join(dshHome, 'profiles', otherProfile, 'node_modules', ...parts),
      path.join(dshHome, 'node_modules', ...parts),
    ];

    let resolvedSource = null;
    for (const cand of candidates) {
      try {
        if (fs.existsSync(cand)) {
          const real = fs.realpathSync(cand);
          if (fs.existsSync(path.join(real, 'package.json'))) {
            resolvedSource = real;
            break;
          }
        }
      } catch {
        // Ignore broken candidate.
      }
    }

    if (!resolvedSource) {
      results.push({ name: item.name, status: 'unresolvable', linkPath: item.linkPath });
      continue;
    }

    try {
      try {
        fs.unlinkSync(item.linkPath);
      } catch {
        fs.rmSync(item.linkPath, { recursive: true, force: true });
      }
      fs.symlinkSync(
        resolvedSource,
        item.linkPath,
        process.platform === 'win32' ? 'junction' : 'dir',
      );
      results.push({ name: item.name, status: 'repaired', linkPath: item.linkPath, targetDir: resolvedSource });
    } catch (err) {
      results.push({
        name: item.name,
        status: 'error',
        linkPath: item.linkPath,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  }

  const repairedCount = results.filter((r) => r.status === 'repaired').length;
  const unresolvedItems = results.filter((r) => r.status !== 'repaired');
  const unresolvableCount = unresolvedItems.length;
  const unresolvedNames = unresolvedItems.map((r) => r.name);
  const allRepaired = unresolvableCount === 0;
  const message = allRepaired
    ? `已成功修复 ${repairedCount} 个断裂依赖软链接`
    : `已修复 ${repairedCount} 个，另有 ${unresolvableCount} 个断链（${unresolvedNames.join('、')}）在本地无可用源包，请重新安装或卸载对应插件`;

  appendAuditEntry(dshHome, {
    action: 'links.repair',
    target: profileName,
    summary: `修复断裂依赖链接：成功 ${repairedCount}，未解析 ${unresolvableCount}`,
    ok: true,
  });

  return {
    ok: true,
    allRepaired,
    target: profileName,
    repairedCount,
    unresolvableCount,
    unresolvedNames,
    message,
    results,
  };
}

function getSnapshotsRoot(dshHome) {
  return path.join(dshHome, 'guard-snapshots');
}

function collectSnapshotCandidates(dshHome, target) {
  const p = normalizeProfileTarget(target) || detectActiveProfile();
  const dir = path.join(dshHome, 'profiles', p);
  const list = [
    { relPath: `profiles/${p}/cordis.patch.yml`, snapFile: 'profile-cordis.patch.yml', absPath: path.join(dir, 'cordis.patch.yml') },
    { relPath: `profiles/${p}/cordis.yml`, snapFile: 'profile-cordis.yml', absPath: path.join(dir, 'cordis.yml') },
    { relPath: `profiles/${p}/package.json`, snapFile: 'profile-package.json', absPath: path.join(dir, 'package.json') },
    { relPath: 'settings.yaml', snapFile: 'home-settings.yaml', absPath: path.join(dshHome, 'settings.yaml') },
  ];
  return list;
}

function pruneOldAutoSnapshots(dshHome, keepCount = AUTO_SNAPSHOT_KEEP) {
  const root = getSnapshotsRoot(dshHome);
  if (!fs.existsSync(root)) return;
  let dirs = [];
  try {
    dirs = fs.readdirSync(root, { withFileTypes: true }).filter((d) => d.isDirectory());
  } catch {
    return;
  }

  const autoUnpinned = [];
  for (const d of dirs) {
    const full = path.join(root, d.name);
    const manifest = safeReadJson(path.join(full, 'manifest.json'));
    const kind = String(manifest?.kind || '');
    const pinned = Boolean(manifest?.pinned);
    if (!pinned && kind.startsWith('auto-')) {
      const ts = manifest?.createdAt ? Date.parse(manifest.createdAt) : 0;
      autoUnpinned.push({ full, ts, name: d.name });
    }
  }

  if (autoUnpinned.length <= keepCount) return;
  autoUnpinned.sort((a, b) => b.ts - a.ts || b.name.localeCompare(a.name));
  for (const old of autoUnpinned.slice(keepCount)) {
    try {
      fs.rmSync(old.full, { recursive: true, force: true });
    } catch {
      // Ignore cleanup errors for locked directories.
    }
  }
}

export function createSnapshot(dshHome, target = null, kind = 'manual', reason = '') {
  const normTarget = resolveTargetProfile(target) || detectActiveProfile();
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
  const safeKind = String(kind || 'manual').replace(/[^a-z0-9_-]/gi, '').slice(0, 24) || 'manual';
  const id = `${stamp}-${safeKind}-${normTarget}`;
  const snapDir = path.join(getSnapshotsRoot(dshHome), id);
  fs.mkdirSync(snapDir, { recursive: true });

  const candidates = collectSnapshotCandidates(dshHome, normTarget);
  const items = [];

  for (const cand of candidates) {
    if (CRED_SKIP_RE.test(cand.relPath) || !SNAPSHOT_SAFE_EXT_RE.test(cand.relPath)) continue;
    if (!fs.existsSync(cand.absPath)) continue;
    const raw = fs.readFileSync(cand.absPath);
    const dest = path.join(snapDir, cand.snapFile);
    fs.writeFileSync(dest, raw);
    items.push({
      relPath: cand.relPath,
      snapFile: cand.snapFile,
      sha256: crypto.createHash('sha256').update(raw).digest('hex'),
      size: raw.length,
    });
  }

  const manifest = {
    id,
    source: 'plugin',
    target: normTarget,
    kind: safeKind,
    reason: String(reason || (safeKind === 'manual' ? '手动配置快照' : safeKind)),
    createdAt: now.toISOString(),
    pinned: false,
    pinLabel: '',
    itemCount: items.length,
    items,
  };
  atomicWriteText(path.join(snapDir, 'manifest.json'), JSON.stringify(manifest, null, 2));
  if (safeKind.startsWith('auto-')) {
    pruneOldAutoSnapshots(dshHome, AUTO_SNAPSHOT_KEEP);
  } else {
    appendAuditEntry(dshHome, {
      action: 'snapshot.create',
      target: normTarget,
      summary: `${manifest.reason}（${items.length} 个文件）`,
      snapshotId: id,
      ok: true,
    });
  }
  return { ok: true, snapshot: manifest, dir: snapDir };
}

export function pinSnapshot(dshHome, snapshotId, pinned, pinLabel = '') {
  const id = String(snapshotId || '').trim();
  if (!id || /[\\/]|\.\./.test(id)) {
    return { ok: false, error: '无效的快照 ID' };
  }

  const snapDir = path.join(getSnapshotsRoot(dshHome), id);
  const manifestPath = path.join(snapDir, 'manifest.json');
  if (!fs.existsSync(manifestPath)) {
    return { ok: false, error: '仅支持置顶插件原生配置快照（~/.dsh/guard-snapshots）' };
  }

  const manifest = safeReadJson(manifestPath);
  if (!manifest || typeof manifest !== 'object') {
    return { ok: false, error: '快照清单已损坏或无法解析' };
  }

  const nextPinned = Boolean(pinned);
  const nextLabel = nextPinned ? String(pinLabel || '').trim().slice(0, 60) : '';
  manifest.pinned = nextPinned;
  manifest.pinLabel = nextLabel;
  atomicWriteText(manifestPath, JSON.stringify(manifest, null, 2));

  appendAuditEntry(dshHome, {
    action: nextPinned ? 'snapshot.pin' : 'snapshot.unpin',
    target: String(manifest.target || 'web'),
    summary: nextPinned ? `置顶保护快照${nextLabel ? ` (${nextLabel})` : ''}` : '取消快照置顶',
    snapshotId: id,
    ok: true,
  });

  return {
    ok: true,
    snapshotId: id,
    pinned: nextPinned,
    pinLabel: nextLabel,
  };
}

export function togglePatchEntry(dshHome, target = null, id, disabled, guard = null) {
  const profileName = resolveTargetProfile(target);
  if (!profileName) {
    return { ok: false, error: '仅支持 web 或 desktop 档案' };
  }
  const entryId = String(id || '').trim();
  if (!entryId) return { ok: false, error: '缺少条目 ID' };
  if (SELF_LOADER_IDS.has(entryId)) {
    return { ok: false, error: '不能通过伴生插件禁用当前正在运行的 @gluztm/dsh-guard 自身' };
  }

  const ext = inspectExtensions(dshHome, profileName, guard);
  const matchedPlugin = ext.plugins.find((p) => p.loaderIds.includes(entryId) || p.name === entryId);
  if (matchedPlugin && matchedPlugin.isCore) {
    return { ok: false, error: `核心组件 ${matchedPlugin.name} 受保护，请保持启用` };
  }

  const patchPath = path.join(dshHome, 'profiles', profileName, 'cordis.patch.yml');
  const existingText = safeReadText(patchPath) ?? '';
  const targetId = matchedPlugin ? matchedPlugin.primaryId : entryId;
  const wantDisabled = Boolean(disabled);

  const preSnap = createSnapshot(
    dshHome,
    profileName,
    'auto-toggle',
    `${wantDisabled ? '禁用' : '启用'} ${targetId} 前自动快照`,
  );

  const rawLines = existingText ? existingText.replace(/\r\n/g, '\n').split('\n') : [];
  const parsedNodes = collectPatchNodes(parseYamlStructure(existingText));
  const idsToMatch = matchedPlugin ? new Set(matchedPlugin.loaderIds) : new Set([targetId]);

  const overrideBlocks = parsedNodes.filter(
    (n) => n.kind === 'override' && n.id && idsToMatch.has(n.id) && Array.isArray(n.lineRange),
  );
  const insertBlocks = parsedNodes.filter(
    (n) => n.kind === 'insert' && n.id && idsToMatch.has(n.id) && Array.isArray(n.lineRange),
  );

  const linesToRemove = new Set();
  let updatedOverrideInPlace = false;

  if (!wantDisabled) {
    for (const blk of overrideBlocks) {
      const [s, e] = blk.lineRange;
      const nonControlKeys = blk.keys.filter((k) => k !== 'id' && k !== 'disabled');
      if (nonControlKeys.length === 0) {
        for (let i = s; i <= e; i += 1) linesToRemove.add(i);
      } else {
        for (let i = s; i <= e; i += 1) {
          if (/^\s+disabled:\s*/.test(rawLines[i])) linesToRemove.add(i);
        }
      }
    }
    for (const blk of insertBlocks) {
      const [s, e] = blk.lineRange;
      for (let i = s; i <= e; i += 1) {
        if (/^\s+disabled:\s*true\b/.test(rawLines[i])) linesToRemove.add(i);
      }
    }
  } else {
    for (const blk of overrideBlocks) {
      const [s, e] = blk.lineRange;
      let hadDisabledLine = false;
      for (let i = s; i <= e; i += 1) {
        if (/^\s+disabled:\s*/.test(rawLines[i])) {
          rawLines[i] = rawLines[i].replace(/(^\s+disabled:\s*).*/, '$1true');
          hadDisabledLine = true;
          updatedOverrideInPlace = true;
        }
      }
      if (!hadDisabledLine) {
        rawLines.splice(e + 1, 0, '  disabled: true');
        updatedOverrideInPlace = true;
      }
    }
  }

  let nextLines = rawLines.filter((_, idx) => !linesToRemove.has(idx));
  if (wantDisabled && !updatedOverrideInPlace) {
    while (nextLines.length > 0 && !nextLines[nextLines.length - 1].trim()) nextLines.pop();
    nextLines.push(`- id: ${quoteYamlId(targetId)}`, '  disabled: true');
  }

  const finalBody = nextLines.length > 0 ? `${nextLines.join('\n').replace(/\n+$/, '')}\n` : '';
  atomicWriteText(patchPath, finalBody);
  appendAuditEntry(dshHome, {
    action: 'entry.toggle',
    target: profileName,
    summary: `${wantDisabled ? '禁用' : '启用'} ${targetId}`,
    snapshotId: preSnap.snapshot.id,
    ok: true,
  });

  return {
    ok: true,
    target: profileName,
    id: targetId,
    enabled: !wantDisabled,
    disabled: wantDisabled,
    preSnapshotId: preSnap.snapshot.id,
    patchPath,
    restartHint: profileName === 'web'
      ? '已写入 Web 配置文件，重载 DSH 服务后生效（已在变更前自动保存快照）'
      : `已写入 ${profileName} 配置文件，重启对应 DSH 端后生效（已在变更前自动保存快照）`,
  };
}

/**
 * Losslessly update or insert `toolCallTimeoutMs` on a @deepseek-ai/dsh-mcp-client insert block.
 */
export function updateMcpTimeout(dshHome, target = null, id, timeoutMs, guard = null) {
  const profileName = resolveTargetProfile(target);
  if (!profileName) {
    return { ok: false, error: '仅支持 web 或 desktop 档案' };
  }
  const entryId = String(id || '').trim();
  if (!entryId) return { ok: false, error: '缺少 MCP 条目 ID' };

  const ms = Math.round(Number(timeoutMs));
  if (!Number.isFinite(ms) || ms < 5000 || ms > 600000) {
    return { ok: false, error: '超时范围须在 5000 ms 到 600000 ms 之间' };
  }

  const ext = inspectExtensions(dshHome, profileName, guard);
  const mcp = ext.mcpServers.find((m) => m.id === entryId);
  if (!mcp) {
    return { ok: false, error: `在 ${profileName} 中未找到 MCP 服务 ${entryId}` };
  }

  const patchPath = path.join(dshHome, 'profiles', profileName, 'cordis.patch.yml');
  const existingText = safeReadText(patchPath) ?? '';
  const rawLines = existingText.replace(/\r\n/g, '\n').split('\n');
  const nodes = collectPatchNodes(parseYamlStructure(existingText));
  const targetNode = nodes.find(
    (n) => n.kind === 'insert' && n.name === MCP_CLIENT_PKG && n.id === entryId && Array.isArray(n.lineRange),
  );
  if (!targetNode) {
    return { ok: false, error: `未定位到 ${entryId} 的 YAML 配置块` };
  }

  const [blockStart, blockEnd] = targetNode.lineRange;
  let idLineIdx = -1;
  for (let i = blockStart; i <= Math.min(blockEnd, rawLines.length - 1); i += 1) {
    const clean = stripYamlComment(rawLines[i]).trimStart();
    const afterDash = clean.startsWith('- ') ? clean.slice(2).trimStart() : clean;
    const kv = splitKeyValue(afterDash);
    if (kv && kv.key === 'id' && unquoteYamlScalar(kv.val) === entryId) {
      idLineIdx = i;
      break;
    }
  }
  if (idLineIdx === -1) {
    return { ok: false, error: `未找到 MCP ${entryId} 的 id 定义行` };
  }

  const idLine = rawLines[idLineIdx];
  const idCleanTrim = idLine.trimStart();
  const idBaseIndent = idLine.length - idCleanTrim.length;
  const idIsListItem = idCleanTrim.startsWith('- ');
  const propIndent = idIsListItem ? idBaseIndent + 2 : idBaseIndent;

  let itemEnd = Math.min(blockEnd, rawLines.length - 1);
  if (idIsListItem) {
    for (let i = idLineIdx + 1; i <= itemEnd; i += 1) {
      const clean = stripYamlComment(rawLines[i]);
      if (!clean.trim()) continue;
      const ind = clean.length - clean.trimStart().length;
      if (ind < idBaseIndent || (ind === idBaseIndent && clean.trimStart().startsWith('- '))) {
        itemEnd = i - 1;
        break;
      }
    }
  }

  let configLineIdx = -1;
  let cfgIndent = propIndent;
  for (let i = Math.min(blockStart, idLineIdx); i <= itemEnd; i += 1) {
    if (!idIsListItem && i < blockStart) continue;
    if (idIsListItem && i <= idLineIdx) continue;
    const clean = stripYamlComment(rawLines[i]);
    if (!clean.trim()) continue;
    const ind = clean.length - clean.trimStart().length;
    const kv = splitKeyValue(clean.trimStart());
    if (kv && kv.key === 'config' && ind >= propIndent - 2 && ind <= propIndent + 2) {
      configLineIdx = i;
      cfgIndent = ind;
      break;
    }
  }

  const preSnap = createSnapshot(
    dshHome,
    profileName,
    'auto-mcp',
    `调整 MCP ${entryId} 超时为 ${ms}ms 前自动快照`,
  );

  if (configLineIdx !== -1) {
    let cfgSubEnd = configLineIdx;
    let timeoutLineIdx = -1;
    let childIndent = cfgIndent + 2;
    let lastNonEmptyInCfg = configLineIdx;

    for (let i = configLineIdx + 1; i <= itemEnd; i += 1) {
      const clean = stripYamlComment(rawLines[i]);
      if (!clean.trim()) continue;
      const ind = clean.length - clean.trimStart().length;
      if (ind <= cfgIndent) break;
      cfgSubEnd = i;
      lastNonEmptyInCfg = i;
      const trimmed = clean.trimStart();
      if (!trimmed.startsWith('-')) {
        const kv = splitKeyValue(trimmed);
        if (kv) {
          if (childIndent === cfgIndent + 2 || ind < childIndent) {
            childIndent = ind;
          }
          if (kv.key === 'toolCallTimeoutMs' && ind === childIndent) {
            timeoutLineIdx = i;
          }
        }
      }
    }

    if (timeoutLineIdx !== -1) {
      const orig = rawLines[timeoutLineIdx];
      const lead = (orig.match(/^(\s*)/) || ['', ' '.repeat(childIndent)])[1];
      const cleanNoComment = stripYamlComment(orig);
      const commentSuffix = orig.slice(cleanNoComment.length);
      rawLines[timeoutLineIdx] = `${lead}toolCallTimeoutMs: ${ms}${commentSuffix}`;
    } else {
      const insertAt = Math.max(configLineIdx + 1, lastNonEmptyInCfg + 1);
      rawLines.splice(insertAt, 0, `${' '.repeat(childIndent)}toolCallTimeoutMs: ${ms}`);
    }
  } else {
    let insertAt = idLineIdx + 1;
    for (let i = idLineIdx; i <= itemEnd; i += 1) {
      if (rawLines[i].trim()) insertAt = i + 1;
    }
    rawLines.splice(
      insertAt,
      0,
      `${' '.repeat(propIndent)}config:`,
      `${' '.repeat(propIndent + 2)}toolCallTimeoutMs: ${ms}`,
    );
  }

  const finalBody = `${rawLines.join('\n').replace(/\n+$/, '')}\n`;
  atomicWriteText(patchPath, finalBody);

  appendAuditEntry(dshHome, {
    action: 'mcp.timeout',
    target: profileName,
    summary: `调整 MCP ${entryId} toolCallTimeoutMs 为 ${ms}ms`,
    snapshotId: preSnap.snapshot.id,
    ok: true,
  });

  return {
    ok: true,
    target: profileName,
    id: entryId,
    toolCallTimeoutMs: ms,
    preSnapshotId: preSnap.snapshot.id,
    patchPath,
  };
}

function readSnapshotRecordsFromDir(rootDir, sourceKind, dshHome) {
  if (!rootDir || !fs.existsSync(rootDir)) return [];
  let dirs = [];
  try {
    dirs = fs.readdirSync(rootDir, { withFileTypes: true }).filter((d) => d.isDirectory());
  } catch {
    return [];
  }

  const records = [];
  for (const d of dirs) {
    const full = path.join(rootDir, d.name);
    const manifest = safeReadJson(path.join(full, 'manifest.json'));
    let createdAt = manifest?.createdAt || manifest?.CreatedAt || null;
    if (!createdAt) {
      try {
        createdAt = fs.statSync(full).mtime.toISOString();
      } catch {
        createdAt = null;
      }
    }

    const rawItems = Array.isArray(manifest?.items)
      ? manifest.items
      : Array.isArray(manifest?.Items)
        ? manifest.Items
        : null;

    let files = [];
    if (rawItems) {
      files = rawItems
        .map((it) => {
          const relPath = String(it.relPath || it.RelPath || '').replace(/\\/g, '/');
          const snapFile = String(it.snapFile || it.SnapFile || path.basename(relPath));
          if (!snapFile || CRED_SKIP_RE.test(snapFile) || CRED_SKIP_RE.test(relPath)) return null;
          const snapSha = it.sha256 || it.Sha256 || sha256File(path.join(full, snapFile));
          let status = 'unknown';
          if (relPath) {
            const livePath = path.join(dshHome, ...relPath.split('/'));
            if (!fs.existsSync(livePath)) {
              status = 'missing_now';
            } else {
              const liveSha = sha256File(livePath);
              status = liveSha && snapSha ? (liveSha === snapSha ? 'same' : 'modified') : 'unknown';
            }
          }
          return {
            name: snapFile,
            relPath: relPath || snapFile,
            size: Number(it.size || it.Size || 0),
            sha256: snapSha,
            status,
            restorableInPlugin: RESTORABLE_REL_RE.test(relPath) && SNAPSHOT_SAFE_EXT_RE.test(snapFile),
          };
        })
        .filter(Boolean);
    } else if (Array.isArray(manifest?.files)) {
      files = manifest.files
        .filter((f) => typeof f === 'string' && !CRED_SKIP_RE.test(f))
        .map((f) => ({
          name: f,
          relPath: f,
          size: 0,
          sha256: null,
          status: 'unknown',
          restorableInPlugin: false,
        }));
    } else {
      try {
        const listed = fs.readdirSync(full, { withFileTypes: true }).filter((f) => f.isFile() && f.name !== 'manifest.json');
        files = listed
          .filter((f) => !CRED_SKIP_RE.test(f.name))
          .map((f) => {
            const p = path.join(full, f.name);
            let size = 0;
            try {
              size = fs.statSync(p).size;
            } catch {
              size = 0;
            }
            return {
              name: f.name,
              relPath: f.name,
              size,
              sha256: sha256File(p),
              status: 'unknown',
              restorableInPlugin: false,
            };
          });
      } catch {
        files = [];
      }
    }

    const changedCount = files.filter((f) => f.status === 'modified' || f.status === 'missing_now').length;
    records.push({
      id: String(manifest?.id || manifest?.Id || d.name),
      source: manifest?.source || sourceKind,
      dir: full,
      target: manifest?.target || manifest?.Target || (d.name.includes('desktop') ? 'desktop' : 'web'),
      kind: manifest?.kind || manifest?.Kind || 'snapshot',
      reason: manifest?.reason || manifest?.Reason || '',
      createdAt,
      pinned: Boolean(manifest?.pinned ?? manifest?.Pinned),
      pinLabel: String(manifest?.pinLabel ?? manifest?.PinLabel ?? ''),
      itemCount: Number(manifest?.itemCount || manifest?.ItemCount || files.length),
      changedCount,
      restorableCount: files.filter((f) => f.restorableInPlugin).length,
      files,
    });
  }
  return records;
}

export function inspectSnapshots(options = {}) {
  const dshHome = resolveDshHome(options.dshHome);
  const guard = options.guard || locateGuardInstallation(options);
  const limit = Number.isFinite(options.limit) ? Math.max(1, Math.min(50, options.limit)) : 10;
  const targetFilter = options.target && options.target !== 'all'
    ? (normalizeProfileTarget(String(options.target)) || 'all')
    : 'all';

  const isolateGuardOnly = Boolean(options.guardHome && !options.dshHome && !process.env.DSH_HOME);
  const pluginRoot = getSnapshotsRoot(dshHome);
  const pluginRecs = isolateGuardOnly ? [] : readSnapshotRecordsFromDir(pluginRoot, 'plugin', dshHome);
  const desktopRecs = guard.snapshotsDir && path.resolve(guard.snapshotsDir) !== path.resolve(pluginRoot)
    ? readSnapshotRecordsFromDir(guard.snapshotsDir, 'desktop', dshHome)
    : [];

  const merged = new Map();
  for (const r of [...pluginRecs, ...desktopRecs]) {
    if (!merged.has(r.id)) merged.set(r.id, r);
  }

  let snapshots = Array.from(merged.values()).sort((a, b) => {
    const ta = a.createdAt ? Date.parse(a.createdAt) : 0;
    const tb = b.createdAt ? Date.parse(b.createdAt) : 0;
    return tb - ta || b.id.localeCompare(a.id);
  });

  if (targetFilter !== 'all') {
    snapshots = snapshots.filter((s) => s.target === targetFilter || s.target === 'all');
  }

  return {
    available: snapshots.length > 0 || fs.existsSync(pluginRoot) || Boolean(guard.snapshotsDir && fs.existsSync(guard.snapshotsDir)),
    guardInstalled: Boolean(guard.installed),
    releaseUrl: RELEASE_URL,
    pluginSnapshotsDir: pluginRoot,
    snapshotsDir: guard.snapshotsDir || pluginRoot,
    total: snapshots.length,
    snapshots: snapshots.slice(0, limit),
  };
}

export function restoreSnapshotConfig(dshHome, guard, snapshotId) {
  const id = String(snapshotId || '').trim();
  if (!id || /[\\/]|\.\./.test(id)) {
    return { ok: false, error: '无效的快照 ID' };
  }

  const listed = inspectSnapshots({ dshHome, guard, limit: 50 });
  const snap = listed.snapshots.find((s) => s.id === id);
  if (!snap) {
    return { ok: false, error: `未找到快照 ${id}` };
  }

  const restorable = snap.files.filter((f) => f.restorableInPlugin);
  if (restorable.length === 0) {
    return {
      ok: false,
      error: '该快照仅包含桌面端级产物（如二进制包、注册表或非 YAML 档案），请在插件边界说明中使用原生桌面端还原',
    };
  }

  const preSnapTarget = snap.target === 'all' ? 'all' : (resolveTargetProfile(snap.target) || 'web');
  const preSnap = createSnapshot(
    dshHome,
    preSnapTarget,
    'pre-restore',
    `恢复快照 ${id} 前自动备份`,
  );

  const restoredFiles = [];
  const skippedFiles = [];
  const resolvedHome = path.resolve(dshHome);

  for (const f of snap.files) {
    if (!f.restorableInPlugin) {
      skippedFiles.push({ relPath: f.relPath, reason: '超出伴生插件白名单范围' });
      continue;
    }
    const snapFile = String(f.snapFile || f.name || '').trim();
    if (!snapFile || CRED_SKIP_RE.test(snapFile) || CRED_SKIP_RE.test(f.relPath) || /[\\/]|\.\./.test(snapFile)) {
      skippedFiles.push({ relPath: f.relPath, reason: '安全跳过' });
      continue;
    }

    const srcFile = path.join(snap.dir, snapFile);
    const destFile = path.resolve(resolvedHome, ...f.relPath.split('/'));
    if (!destFile.startsWith(`${resolvedHome}${path.sep}`)) {
      skippedFiles.push({ relPath: f.relPath, reason: '路径越界保护' });
      continue;
    }
    if (!fs.existsSync(srcFile)) {
      skippedFiles.push({ relPath: f.relPath, reason: '快照文件缺失' });
      continue;
    }

    const content = fs.readFileSync(srcFile, 'utf8');
    atomicWriteText(destFile, content);
    restoredFiles.push(f.relPath);
  }

  appendAuditEntry(dshHome, {
    action: 'snapshot.restore',
    target: preSnapTarget,
    summary: `从快照 ${id} 恢复 ${restoredFiles.length} 个配置文件`,
    snapshotId: preSnap.snapshot.id,
    ok: restoredFiles.length > 0,
  });

  return {
    ok: restoredFiles.length > 0,
    snapshotId: id,
    preRestoreId: preSnap.snapshot.id,
    restoredFiles,
    skippedFiles,
  };
}

export function inspectHealth(options = {}) {
  const dshHome = resolveDshHome(options.dshHome);
  const guard = locateGuardInstallation(options);
  const activeProfile =
    normalizeProfileTarget(options.activeProfile || (options.target && options.target !== 'all' ? options.target : null)) ||
    detectActiveProfile({ ctx: options.ctx, dshHome });

  const profile = inspectProfile(dshHome, activeProfile);
  const ext = inspectExtensions(dshHome, activeProfile, guard);
  const profiles = { [activeProfile]: profile };
  const extensions = { [activeProfile]: ext };
  const referencedEnvSet = new Set(profile.referencedEnvKeys || []);

  const credentials = inspectCredentialKeysMasked(dshHome);
  const providers = inspectProviders(dshHome, activeProfile);
  const snapshots = inspectSnapshots({ dshHome, guard, target: activeProfile, limit: 15 });

  const readyKeySet = new Set(credentials.keys.filter((k) => k.present || k.envSet).map((k) => k.key));
  const missingReferencedKeys = Array.from(referencedEnvSet).filter((k) => !readyKeySet.has(k));
  credentials.missingReferencedKeys = missingReferencedKeys;

  const trackTitle = activeProfile === 'desktop' ? '桌面端' : 'Web 端';
  const issues = [];
  if (!fs.existsSync(dshHome)) {
    issues.push(`未找到 DSH 目录: ${dshHome}`);
  }
  if (!profile.exists) {
    issues.push(`未找到 ${trackTitle} (${activeProfile}) 运行档案目录`);
  }
  if (profile.missingBundles && profile.missingBundles.length > 0) {
    issues.push(`${trackTitle}存在未在 dependencies 注册的 bundle: ${profile.missingBundles.join(', ')}`);
  }
  if (profile.brokenLinkCount > 0) {
    issues.push(`${trackTitle}存在 ${profile.brokenLinkCount} 个断裂依赖软链接`);
  }
  if (!credentials.present) {
    issues.push('.credentials.yaml 尚未初始化');
  }
  if (missingReferencedKeys.length > 0) {
    issues.push(`配置中引用的凭据键缺失: ${missingReferencedKeys.join(', ')}`);
  }

  const contextAudit = inspectContextAudit(
    dshHome,
    { activeProfile, profile, profiles, extensions },
    activeProfile,
    { agentsHome: options.agentsHome },
  );
  const auditLog = readAuditLog(dshHome, 20);

  return {
    companionVersion: PACKAGE_VERSION,
    checkedAt: new Date().toISOString(),
    platform: process.platform,
    dshHome,
    dshHomeExists: fs.existsSync(dshHome),
    activeProfile,
    profileNames: [activeProfile],
    profile,
    healthy: issues.length === 0,
    issuesCount: issues.length,
    issues,
    driftWarnings: issues,
    guard,
    profiles,
    credentials,
    providers,
    extensions,
    contextAudit,
    auditLog,
    snapshots,
  };
}

export function openGuard(options = {}) {
  const guard = locateGuardInstallation(options);
  if (process.platform !== 'win32') {
    return {
      ok: false,
      installed: false,
      launched: false,
      reason: 'non-windows',
      message: `当前系统为 ${process.platform}；原版 DSHGuard 桌面应用目前仅提供 Windows 安装包（伴生插件本身的插件/凭据/快照功能已在当前平台完整可用）。`,
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
      installUrl: INSTALLER_URL,
    };
  }
  if (!guard.installed || !guard.exePath) {
    return {
      ok: false,
      installed: false,
      launched: false,
      reason: 'not-installed',
      message: '未在本机检测到 DSHGuard.exe（伴生插件本身已内置插件开关、凭据写入与配置快照）；如需跨进程托管或硬件加密隔离，可安装 Windows 桌面端。',
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
      installUrl: INSTALLER_URL,
    };
  }
  if (!options.launch || options.dryRun) {
    return {
      ok: true,
      installed: true,
      launched: Boolean(options.dryRun && options.launch),
      dryRun: Boolean(options.dryRun),
      exePath: guard.exePath,
      message: `已定位到桌面端：${guard.exePath}${options.launch ? '（未拉起进程）' : ''}`,
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
      installUrl: INSTALLER_URL,
    };
  }
  try {
    const child = spawn(guard.exePath, [], {
      detached: true,
      stdio: 'ignore',
      windowsHide: false,
    });
    child.unref();
    return {
      ok: true,
      installed: true,
      launched: true,
      exePath: guard.exePath,
      pid: child.pid || null,
      message: `已启动桌面端：${guard.exePath}`,
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
      installUrl: INSTALLER_URL,
    };
  } catch (err) {
    return {
      ok: false,
      installed: true,
      launched: false,
      reason: 'spawn-error',
      exePath: guard.exePath,
      message: `启动失败：${err instanceof Error ? err.message : String(err)}`,
      releaseUrl: RELEASE_URL,
      installerUrl: INSTALLER_URL,
      installUrl: INSTALLER_URL,
    };
  }
}

export function formatHealthText(report) {
  const cred = report.credentials;
  const provCount = Array.isArray(report.providers) ? report.providers.length : 0;
  const activeName = normalizeProfileTarget(report.activeProfile) || report.profileNames?.[0] || 'web';
  const p = report.profile || report.profiles?.[activeName];
  const mcpCount = report.extensions?.[activeName]?.mcpServers?.length || 0;
  const lines = [
    `[dsh-guard v${report.companionVersion}] DSH Home: ${report.dshHome} (${report.dshHomeExists ? '已找到' : '未找到'})`,
    `桌面端: ${report.guard.installed ? `已安装 (${report.guard.exePath})` : report.platform === 'win32' ? '未检测到本地 DSHGuard.exe（非必需，插件功能可独立运行）' : `${report.platform}（原生桌面端仅限 Windows）`}`,
    `当前运行端 (${activeName}): ${p?.exists ? `${p.totalDependencies} 依赖（第三方 ${p.thirdPartyCount}），MCP ${mcpCount}，断链 ${p.brokenLinkCount}，patch=${p.patchHash || '无'}` : '未找到'}`,
  ];
  const ctxSummary = report.contextAudit
    ? ` · 技能 ${report.contextAudit.skills?.modelVisibleCount || 0}/${report.contextAudit.skills?.total || 0}（模型可见）`
    : '';
  lines.push(
    `凭据与模型: ${cred.present ? `${cred.count} 个密钥引用（含 ${cred.protectedCount} 保护账号）` : '未找到 .credentials.yaml'} · ${provCount} 个模型提供商${ctxSummary}`,
  );
  lines.push(`快照: 共 ${report.snapshots.total} 份`);
  if (report.driftWarnings.length > 0) {
    lines.push(`提示: ${report.driftWarnings.join('；')}`);
  } else {
    lines.push(`状态: ${activeName} 档案结构健康且无断链。`);
  }
  return lines.join('\n');
}

export function formatSnapshotsText(snapReport) {
  const lines = [
    `[dsh-guard] 快照目录: ${snapReport.pluginSnapshotsDir}`,
    `累计快照: ${snapReport.total} 份（展示最近 ${snapReport.snapshots.length} 份）`,
  ];
  if (snapReport.snapshots.length === 0) {
    lines.push('暂无快照。在插件 UI 快照页签或运行 /guard backup 即可生成本地配置快照。');
    return lines.join('\n');
  }
  for (const s of snapReport.snapshots) {
    const srcTag = s.source === 'desktop' ? '桌面端' : '插件';
    const pinTag = s.pinned ? '📌 ' : '';
    const diffTag = s.changedCount > 0 ? ` · 与磁盘有 ${s.changedCount} 处差异` : '';
    lines.push(`- ${pinTag}${s.id} [${srcTag}/${s.target}/${s.kind}]${s.reason ? ` (${s.reason})` : ''} · ${s.itemCount} 文件${diffTag}`);
  }
  return lines.join('\n');
}

export {
  detectActiveProfile,
  discoverProfiles,
  formatContextAuditText,
  formatMcpProbeText,
  inspectContextAudit,
  inspectSkills,
  normalizeProfileTarget,
  probeMcpServers,
  readAuditLog,
  toggleSkillVisibility,
  validateProfileName,
};
