# @gluztm/dsh-guard

> Cross-platform configuration, extension, and snapshot companion plugin for **DeepSeek Harness (DSH)**. Runs directly inside DSH Web and CLI without external installers: toggle plugins & MCP servers with automatic backups, repair broken symlinks, inspect `llm-pi-ai` providers, blind-write `.credentials.yaml` refs, and compare/restore local YAML/JSON snapshots.

[中文文档](./README.md) · [GitHub Repository](https://github.com/gluztm/DSHGuard) · [Releases](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)

![@gluztm/dsh-guard v2.4.2 Companion Panel Preview](https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/242-companion-sidebar.png)

## Built-In Capabilities

1. **Plugin & MCP Toggle Switchboard (Web & Desktop tracks)**
   - Resolves per-package `loaderId` declarations via `dsh.bundle.patch` / `cordis.patch.yml` and detects `@deepseek-ai/dsh-mcp-client` servers.
   - Protects core `@deepseek-ai/*` modules and the companion plugin itself from accidental disablement.
   - Creates an automatic pre-change snapshot before modifying `cordis.patch.yml` using strict YAML quoting rules.
2. **Profile Drift & Broken Symlink Repair**
   - Audits `profiles/web` and `profiles/desktop` for missing `dsh.bundles` dependencies and hash drift.
   - Detects broken symlinks / NTFS junctions under `node_modules` and relinks valid targets from `.dsh-module-fallback` or sibling profiles in pure Node.js.
3. **`llm-pi-ai` Inspection & One-Way `.credentials.yaml` Blind Write**
   - Displays provider protocols (`anthropic-messages`, `openai-completions`, `google-generative-ai`), `baseURL`, model counts, and key readiness badges.
   - **Zero secret readback**: writes user-entered API keys strictly one-way into `.credentials.yaml` `refs:` while preserving `version`, `records`, comments, and protected `*_ACCOUNT_*` OAuth blocks. Never reads back or transmits plaintext values (`redLines: []`, `secretTouches: []` in `dsh-trust-check`).
4. **Native Config Snapshots, Live SHA256 Diff & Whitelist Rollback**
   - Backs up `cordis.patch.yml`, `cordis.yml`, `package.json`, and `settings.yaml` into `~/.dsh/guard-snapshots` (**strictly excluding** `.credentials.yaml` and secret files).
   - Computes live SHA256 file diff status against disk and restores whitelisted YAML/JSON configs in 1 click (with an automatic `pre-restore` backup).
5. **Agent Tools, Slash Command & CLI**
   - Tools: `dsh_guard_status`, `dsh_guard_snapshots`, `dsh_guard_backup`, `dsh_guard_open`
   - Slash command: `/guard [status|snapshots|backup [web|desktop|all]|open]`
   - CLI: `dsh-guard status [--json]`, `dsh-guard snapshots`, `dsh-guard backup [web|desktop]`, `dsh-guard repair [web|desktop]`

---

## Architecture & Security Boundaries (When Native Desktop App Is Needed)

Routine plugin toggling, credential key entry, link repair, and YAML config snapshots all run directly inside `@gluztm/dsh-guard`. A smaller set of OS-level / out-of-band workflows cannot safely execute inside a live DSH host plugin and live exclusively in the standalone **[DSHGuard Desktop (Windows)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)** app:

| Area | Handled Inside Companion Plugin | Exclusive to Native Desktop App (Why) |
| :--- | :--- | :--- |
| **Extension Lifecycle** | Toggle plugins & MCP entries, repair symlinks | **Fresh install / full uninstall / force kill locks**: running `npm install` or deleting `node_modules` inside the live host tears loaded modules and hits file locks; plugins should also never pull remote installers or terminate the host process. |
| **Secrets & Migration** | Key existence audit + one-way write to `.credentials.yaml` | **Plaintext vault, Windows DPAPI & CC Switch import**: Web UI plugins with `fetch` are barred from reading back secrets; hardware-bound DPAPI encryption and cross-tool migrations rely on native Windows security APIs. |
| **System Rollback** | Live diff & rollback of YAML/JSON configs | **Full `node_modules` ZIP recovery & crash recovery**: restoring full package trees and relaunching DSH requires an external supervisor running out of process. |

- When `DSHGuard.exe` is detected locally on Windows, the header shows an unobtrusive **Open Desktop** action.
- Windows users who need those out-of-band capabilities can obtain [DSHGuard-Setup-2.4.2.exe](https://github.com/gluztm/DSHGuard/releases/download/2.4.2/DSHGuard-Setup-2.4.2.exe) from [Release v2.4.2](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2).

## License

MIT © gluztm
