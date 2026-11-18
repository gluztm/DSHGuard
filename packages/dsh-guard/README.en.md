# `@gluztm/dsh-guard`

> **Cross-platform plugin manager, symlink repair, and YAML snapshot rollback companion for DeepSeek Harness (DSH)**: Runs inside the DSH Web sidebar and CLI without external installers. Toggle plugins and MCP servers (with automatic backups), fix broken `node_modules` links, audit `llm-pi-ai` providers, write `.credentials.yaml` keys one-way without secret readback, and restore `cordis.patch.yml` diffs.

[中文文档](./README.md) · [Main Repo README](../../README.md) · [Releases (`2.4.2`)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)

![@gluztm/dsh-guard v2.4.2 Companion Panel](https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/242-companion-sidebar.png)

## When to Install This Plugin

If you use DSH on macOS or Linux, or just prefer handling everyday config chores from inside the DSH sidebar without opening a separate desktop launcher:

1. **Toggle plugins and MCP servers without editing YAML by hand**
   - Resolves `loaderId` declarations from `dsh.bundle.patch` / `cordis.patch.yml` and detects `@deepseek-ai/dsh-mcp-client` servers.
   - Blocks accidental toggles on core `@deepseek-ai/*` modules and the companion plugin itself.
   - Saves an `auto-toggle` backup snapshot before every change.
2. **Fix profile drift and broken `node_modules` symlinks**
   - Compares `profiles/web` and `profiles/desktop` for missing `dsh.bundles` dependencies and hash drift.
   - Detects dead symlinks or NTFS junctions under `node_modules` and re-links valid targets from `.dsh-module-fallback` or sibling profiles in pure Node.js.
3. **Check `llm-pi-ai` providers and write `.credentials.yaml` keys one-way**
   - Lists configured protocols (`anthropic-messages`, `openai-completions`, `google-generative-ai`), `baseURL`, model counts, and key readiness.
   - **Zero plaintext readback**: writes user-entered keys strictly one-way into `.credentials.yaml` `refs:` while keeping `version`, `records`, comments, and protected `*_ACCOUNT_*` OAuth blocks intact (`redLines: []`, `secretTouches: []` in `dsh-trust-check`).
4. **Compare live SHA256 file diffs and roll back broken configs**
   - Backs up `cordis.patch.yml`, `cordis.yml`, `package.json`, and `settings.yaml` under `~/.dsh/guard-snapshots` (**excluding** `.credentials.yaml` and secret files).
   - Diffs live SHA256 hashes against disk and restores whitelisted YAML/JSON files in one click (creating a `pre-restore` backup first).
5. **Agent tools, `/guard` slash command, and CLI**
   - Agent tools: `dsh_guard_status`, `dsh_guard_snapshots`, `dsh_guard_backup`, `dsh_guard_open`
   - Slash command: `/guard [status|snapshots|backup [web|desktop|all]|open]`
   - CLI: `dsh-guard status [--json]`, `dsh-guard snapshots`, `dsh-guard backup [web|desktop]`, `dsh-guard repair [web|desktop]`

---

## When You Also Need the Windows Desktop App (`DSHGuard.exe`)

Everyday toggles, symlink fixes, one-way API key entry, and YAML snapshots stay inside `@gluztm/dsh-guard`. A few workflows cannot run safely inside a live Node host plugin and belong to **[DSHGuard Desktop (Windows)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)**:

| Area | Handled in Companion Plugin | Desktop App Only (Why) |
| :--- | :--- | :--- |
| **Crash Recovery & Install** | Toggle plugins/MCP and fix symlinks while DSH is up | **Startup crash logs / fresh install / full uninstall / force-kill locked processes**: if a broken plugin stops DSH from booting, in-process plugins cannot load, and deleting `node_modules` from inside Node hits file locks. |
| **Secret Viewing & Migration** | Key presence check + one-way `.credentials.yaml` write | **Drop URL+Key files / import plaintext non-subscription keys from Claude Code, Codex, Gemini**: browser-connected plugins are barred from reading back existing plaintext secrets. |
| **Full Tree Rollback** | Diff & restore whitelisted YAML/JSON files | **Full `node_modules` ZIP restore & DSH version downgrade**: swapping full package trees and restarting DSH must run outside the host process. |

- On Windows, if `DSHGuard.exe` is present, the panel header shows an **Open Desktop** shortcut.
- Grab [DSHGuard-Setup-2.4.2.exe](https://github.com/gluztm/DSHGuard/releases/download/2.4.2/DSHGuard-Setup-2.4.2.exe) from [Release v2.4.2](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2) when you need those out-of-band recovery tools.

## License

MIT © gluztm
