# @gluztm/dsh-guard

> Cross-platform DSH control plane and companion governance plugin (`v2.5.0`) for **DeepSeek Harness (DSH)**. Runs directly inside DSH Web and Desktop without external installers: **auto-detects the running host profile (`desktop` on Desktop, `web` on Web)** with zero cross-profile noise, switches work scene loadouts, performs on-demand lossless `web` ↔ `desktop` diff & merge, audits DSH storage with reversible recycle & log anomaly radar, toggles plugins/MCP & tunes timeouts with local loopback probing, governs Skills/context footprint, blind-writes `.credentials.yaml` refs, and maintains pinned YAML/JSON snapshots with redacted audit logs.

[中文文档](./README.md) · [GitHub Repository](https://github.com/gluztm/DSHGuard) · [Roadmap](https://github.com/gluztm/DSHGuard/blob/main/docs/ROADMAP-COMPANION.md) · [Releases](https://github.com/gluztm/DSHGuard/releases/tag/2.4.3)

![@gluztm/dsh-guard Companion Panel Preview](https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/242-companion-sidebar.png)

## Built-In Capabilities

1. **Single-Track Host Auto-Detection & Broken Symlink Repair**
   - Automatically locks onto the active DSH host track (`desktop` when launched inside DSH Desktop, `web` when launched in DSH Web), omitting `headless`, custom directories, and cross-profile drift alerts.
   - Detects broken symlinks / NTFS junctions under `node_modules` and relinks valid targets from `.dsh-module-fallback` or sibling profiles in pure Node.js; any unresolvable links return HTTP 200 with a clear list instead of failing with HTTP 400.
2. **Plugin & MCP Switchboard, Timeout Tuning & Safe Local Probe**
   - Resolves `dsh.bundle.patch` / `cordis.patch.yml` loader IDs and `@deepseek-ai/dsh-mcp-client` entries while protecting core `@deepseek-ai/*` modules and the companion plugin itself.
   - Losslessly tunes per-MCP `toolCallTimeoutMs` (`5000`–`600000 ms`) with automatic pre-change `auto-mcp` snapshots.
   - Probes MCP health safely without side effects: HTTP/SSE servers are probed **strictly on loopback addresses** (`127.0.0.1` / `localhost` / `::1`, SSRF-safe) via TCP connect; `stdio` entries are checked on disk for executable and script path readiness.
3. **Startup Context Footprint Audit & Skills Visibility Governance**
   - Estimates the active profile's startup token overhead across active third-party plugins, enabled MCP servers, visible Skills, and permanent prompt files (`prompt-inject.md`, `MEMORY.md`, `prompts/*.md`, `system-prompt`).
   - Scans `~/.dsh/skills`, `~/.agents/skills`, and read-only plugin skills (`SKILL.md`), allowing lossless toggles of `disable-model-invocation` and `user-invocable` frontmatter flags with SHA256 fingerprint conflict detection.
4. **`llm-pi-ai` Inspection & One-Way `.credentials.yaml` Blind Write**
   - Displays configured providers, protocols, endpoints, model counts, and key readiness for the active profile; includes 7 mainstream provider env-key presets in the add-key drawer.
   - **Zero secret readback**: writes user-entered API keys strictly one-way into `.credentials.yaml` `refs:` while preserving `version`, `records`, comments, and protected `*_ACCOUNT_*` OAuth blocks (`redLines: []`, `secretTouches: []` in `dsh-trust-check`).
5. **Pinned Snapshots, Live SHA256 Diff Rollback & Redacted Audit Log**
   - Backs up `cordis.patch.yml`, `cordis.yml`, `package.json`, and `settings.yaml` into `~/.dsh/guard-snapshots` (strictly excluding `.credentials.yaml`), supports **📌 Pin** protection against auto-pruning, 1-click whitelist rollback, and redacted mutation tracking in `~/.dsh/guard-state/audit.jsonl`.
6. **Work Scene Loadout Switching**
   - Provides built-in `lean-coding`, `creative-full`, and `safe-mode` presets alongside custom scenes persisted to `~/.dsh/guard-state/scenes.json`, supporting L0 Dry-Run previews, transactional `auto-scene` pre-snapshots, single aggregated audit entries, and strict protection for `@deepseek-ai/*` core modules and self.
7. **On-Demand Profile Sync, Storage Recycle & Log Anomaly Radar (`Ops` Tab)**
   - **On-demand diff & lossless merge**: keeps default startup strictly single-track while allowing explicit comparison and block-level merge of `llm-providers`, `mcp-servers`, and `permissions` between `web` and `desktop` (preserving `!!js` YAML tags, comments, and target-only custom entries).
   - **Reversible storage cleanup**: maps storage usage across 10 standard buckets, strictly skips symlinks, pinned snapshots, and credential/key backups, plans via Dry-Run, moves items reversibly to `~/.dsh/guard-recycle/<batchId>`, and restores batches with 1 click.
   - **Bounded low-token log radar**: scans up to 40 newest logs (tail 256 KB per file), redacts secrets and homedir paths before grouping signatures, and provides actionable troubleshooting hints.
8. **10 Agent Tools, Slash Command & CLI**
   - Tools: `dsh_guard_status`, `dsh_guard_context_audit`, `dsh_guard_mcp_probe`, `dsh_guard_scenes`, `dsh_guard_profile_diff`, `dsh_guard_storage_audit`, `dsh_guard_log_radar`, `dsh_guard_snapshots`, `dsh_guard_backup`, `dsh_guard_open`
   - Slash command: `/guard [status|context|mcp|scenes|diff|storage|logs|snapshots|backup [web|desktop]|open]`
   - CLI: `dsh-guard status|context|mcp-probe|scenes|diff|storage|logs|snapshots|backup|repair|audit|open` (`--json` supported)

---

## Architecture & Security Boundaries (When Native Desktop App Is Needed)

Routine active-profile inspection, extension & context governance, local MCP probing, blind-write credential setup, and YAML config snapshots all run directly inside `@gluztm/dsh-guard`. A smaller set of OS-level / out-of-band workflows cannot safely execute inside a live DSH host plugin and live exclusively in the standalone **[DSHGuard Desktop (Windows)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.3)** app:

| Area | Handled Inside Companion Plugin | Exclusive to Native Desktop App (Why) |
| :--- | :--- | :--- |
| **Extension Lifecycle** | Toggle plugins, MCP & Skills; tune MCP timeouts; local probe; repair symlinks | **Fresh install / full uninstall / force kill locks**: running `npm install` or deleting `node_modules` inside the live host tears loaded modules and hits file locks; plugins should also never pull remote installers or terminate the host process. |
| **Secrets & Migration** | Key existence audit + one-way write to `.credentials.yaml` | **Plaintext vault, Windows DPAPI & CC Switch import**: Web UI plugins with `fetch` are barred from reading back secrets; hardware-bound DPAPI encryption and cross-tool migrations rely on native Windows security APIs. |
| **System Rollback** | Live diff, pinning & rollback of YAML/JSON configs | **Full `node_modules` ZIP recovery & crash recovery**: restoring full package trees and relaunching DSH requires an external supervisor running out of process. |

- When `DSHGuard.exe` is detected locally on Windows, the header shows an unobtrusive **Open Desktop** action.
- Windows users who need those out-of-band capabilities can obtain [DSHGuard-Setup-2.4.3.exe](https://github.com/gluztm/DSHGuard/releases/download/2.4.3/DSHGuard-Setup-2.4.3.exe) from [Release v2.4.3](https://github.com/gluztm/DSHGuard/releases/tag/2.4.3).
