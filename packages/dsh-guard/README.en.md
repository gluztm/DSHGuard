# dsh-guard

[简体中文](README.md) ｜ **English** ｜ [Main Repository (DSHGuard)](../../README.md)

**`dsh-guard` is the official read-only companion DSH plugin and CLI inspector for [DSHGuard](https://github.com/gluztm/DSHGuard).**

While `DSHGuard` itself runs **out-of-band** as a Windows desktop supervisor so it stays alive even when DeepSeek Harness fails to boot, `dsh-guard` runs **inside DSH** to give you and your agent instant read-only visibility into both Web and Desktop profiles, masked credential key readiness, stored snapshots, and a quick trigger to open the desktop supervisor.

---

## Features (3 Agent Tools + 1 Slash Command)

| Kind | Name | Behavior & Safety Boundary |
| --- | --- | --- |
| Tool | `dsh_guard_status` | **Read-only audit** across both `web` and `desktop` profiles: checks `package.json` bundle/dependency drift, `cordis.patch.yml` structural health, masked readiness of `apiKeyEnv` keys in `.credentials.yaml` (**never reads or returns any secret value**), and local `DSHGuard.exe` detection. |
| Tool | `dsh_guard_snapshots` | **Read-only list** of pre-change DSHGuard snapshots grouped/filtered by `web` or `desktop` track. |
| Tool | `dsh_guard_open` | Locates the local Windows `DSHGuard.exe` supervisor (and spawns it when `launch: true` is passed) or returns the official GitHub Releases URL when not installed. |
| Command | `/guard` | Run `/guard`, `/guard snapshots`, or `/guard open` directly from chat. |

---

## Installation & CLI

Install `dsh-guard` directly inside DSH or run it standalone with Node.js `>= 18`:

```powershell
# Standalone CLI check (read-only)
node packages/dsh-guard/bin/dsh-guard.mjs status
node packages/dsh-guard/bin/dsh-guard.mjs snapshots
node packages/dsh-guard/bin/dsh-guard.mjs open
```

For full out-of-band engine rescue, one-click snapshot rollback, Web/Desktop dual-track plugin management, and **CC Switch multi-source credential & provider import**, download the Windows installer from **[GitHub Releases](https://github.com/gluztm/DSHGuard/releases/latest)**.

---

## License

GPL-3.0 — see [LICENSE](../../LICENSE).
