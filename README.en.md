# DSHGuard

[中文](README.md) ｜ **English**

**A Windows desktop "guard shell" (守护壳) for the DeepSeek Harness (DSH) web engine.** It snapshots your config before every change and can roll it back in one click, so a destructive upstream update or a plugin that breaks your config is always reversible; it finds, installs and manages plugins locally, with no separate plugin marketplace to set up; and it keeps logs on hand and ready to export as a diagnostics bundle — all from a GUI, with no command line involved.

It is for people who already run DSH but would rather not babysit it from a terminal: install it, launch it, click a button. When the engine fails to start, stalls, or a plugin lands in a strange state, you can inspect it, archive it, and roll back from the same window.

---

## Language

**The application's interface is Chinese only.** There is no localization layer in this codebase — no resource files, no culture switching — and every label, button, tooltip, dialog and installer string is hardcoded Chinese. That is what you get regardless of your Windows display language.

**This document is an English entry point, not an English interface.** It tells you what DSHGuard does, whether it will run on your machine, and how to install it. It does not change what the app looks like.

If you want an English interface, please open an issue or submit a pull request — see [Contributing](#contributing). If there is enough demand, an English UI will be added; nothing is scheduled today, so treat the Chinese UI as the current state of the project.

Page names below are given in English with the Chinese label in parentheses, so you can match them against what is actually on screen.

---

## What it solves

DSHGuard grew out of everyday use of DSH, one annoyance at a time:

| Friction | What DSHGuard does |
| --- | --- |
| Starting the engine means typing commands in a terminal, then hunting for the port and opening the page yourself | One-click start on the Status（状态）page |
| The engine installs by upstream tag, so a tag move silently swaps out the version you were running | Version memory, plus upgrade and rollback under Settings（设置）→ Version（版本） |
| With a pile of plugins installed, it is impossible to tell what is enabled, what is off, and what is due for an update | The Plugins（插件）page: local inventory plus the community index |
| Editing a plugin manifest or config breaks something and there is no restore point | The Snapshots（快照）page archives before every change and rolls back individual items or whole groups |
| The engine will not start and there is no evidence of why | The Logs（日志）page: live output, historical logs, and Export Diagnostics |

The guiding principle is **supervise, don't take over**: DSHGuard is a separate program from the DSH engine. The engine can still be started from anywhere; DSHGuard starts it, watches it, and preserves the evidence when it breaks.

---

## What's in it

The window is a left-hand navigation bar with six pages, plus a right-hand column that always shows service control, the current DSH version, and a recent-events feed. The title bar toggles light and dark themes.

| Page | What it covers |
| --- | --- |
| Status（状态） | Whether the engine is running, on which port, its recent output and events; the right-hand column carries one-click start and stop |
| Logs（日志） | Live output for this session, historical log files, Export Diagnostics, copy-to-clipboard |
| Snapshots（快照） | Automatic archives taken before changes; roll back selected items, or everything, at any time |
| Plugins（插件） | Find Plugins（寻找插件）browses the community index and installs; Local Plugins（本地插件）enables, disables, updates and uninstalls |
| Settings（设置） | General（常规）for toggles and the port, Paths（路径）for every directory, Version（版本）for upgrade and rollback |
| About（说明） | In-app help and a short FAQ |

### The Plugins（插件）page

Two tabs: **Local Plugins（本地插件）** for what is already installed, and **Find Plugins（寻找插件）** for the community index.

- **Local Plugins** searches by name, author and description, and sorts by **install time** (the default, newest first), **creation date**, **update date** or **compatibility**. Clicking the same field again reverses the direction, and the selected row shows `↑` or `↓` on its right.
- Compatibility always runs **incompatible → partly usable → fully compatible → undeclared**, in that fixed order — it does **not** flip with the sorting direction.
- Each card shows the plugin name, author, current version, enabled state and a description. The **creation date** (the author's first release) sits after the name, and the **update date on this machine** in the top-right corner; both use the `xxxx年xx月xx日` format.
- Whether a newer version exists is now read off the **一键更新 N 个** (Update N) counter at the top of the page. The older filters it replaced — compatibility, enabled state, and "new version only" — are gone, because they duplicated what multi-select already does.
- **Find Plugins** browses the community index with its own sorting (downloads, favourites, update time) and filtering. Clicking a screenshot thumbnail opens the image viewer: **Ctrl + mouse wheel** zooms around the cursor as the anchor, **holding the mouse button and dragging** pans around a detail (a small movement counts as a click, a large one as a drag), hovering the left and right edges — 20% of the width each, including the blank space beside the image — reveals translucent paging arrows, and clicking the middle 60% opens the repository page.

---

## Screenshots

The screenshots below show the Chinese interface — that is what you will actually see.

![Status page: engine running](docs/screenshots/01-状态-引擎运行中.png)

![Snapshots page](docs/screenshots/04-快照与回滚.png)

---

## Requirements

- Windows 10 or 11, 64-bit
- Node.js (LTS). The installer can install it for you: per-user, no administrator rights required
- pnpm, optional — only needed by plugins that depend on it
- Git, optional — only needed for plugins whose source is a Git repository. DSHGuard does **not** install Git for you, and such a plugin will fail to install without it
- A network connection the first time you start the engine (the engine is fetched and cached through npx)

Building from source additionally requires the .NET 10 SDK.

---

## Install

1. Download the latest `DSHGuard-Setup-<version>.exe` from the [Releases](https://github.com/gluztm/DSHGuard/releases) page.
2. Run it and follow the wizard. It installs per-user, under your own program directory, and needs no administrator rights.
3. Optionally tick the extra tasks: create a desktop shortcut, and start DSHGuard at login (both changeable later inside the app). If Node.js is not found, the wizard also offers to install it — that task appears only when Node is actually missing.
4. Launch DSHGuard from the Start menu or the desktop icon. The installer deliberately does **not** launch the app for you at the end.

Then click **一键启动引擎** (Start Engine) in the top-right. The first start is slower because the engine has to be downloaded and cached. After that, launches follow the version pinned in version memory, so an upstream tag move no longer changes what you run.

If the engine is already running somewhere else, DSHGuard detects it, shows it as running, and will not start a second one or take the port.

---

## Download and verify

- Download installers **only** from this repository's [Releases](https://github.com/gluztm/DSHGuard/releases) page.
- Verify the SHA256 checksum of the installer against the value published in the release notes before running it. The build prints the checksum of the package it produced, and that is the value quoted in the release notes.
- If you suspect the binary on your machine has been swapped out, uninstall it and download again.

The full security policy, including how to report a vulnerability, is in [SECURITY.md](SECURITY.md).

---

## Design decisions

- **It supervises, it doesn't take over.** The engine keeps its own lifecycle; DSHGuard only drives the start, watches the process, and keeps the evidence.
- **It doesn't grab ports.** DSHGuard checks the port before starting and never seizes one that another process holds.
- **It leaves DSH's own config directory (`%USERPROFILE%\.dsh`) alone.** That is where the engine's config, plugins and session data live, and it is treated as read-only. There are exactly three exceptions, each of which requires something already broken and blocking startup; in all three DSHGuard backs the file up first and says what it changed in the event feed:
  - the patch-layer config file (`cordis.patch.yml`) has invalid syntax and the engine would ignore it wholesale;
  - a package name in the plugin manifest is malformed, so the engine cannot resolve that plugin;
  - a component link left behind by a previous DSHGuard run has gone stale and would make the engine refuse to start — those links, which DSHGuard itself created, are removed.
- **Plugin install and update times are kept locally, and only locally.** DSHGuard records when each plugin was installed and when it was last updated in its own `Config\plugin-times.json`, so those dates can be shown on the plugin cards. That file never leaves your machine, is not sent anywhere, and can be deleted at any time. Recording starts with 1.3, so plugins installed before that show as unknown rather than a guessed date.
- **Exiting the UI is not stopping the engine.** The 退出UI (Exit UI) button in the bottom-left closes only the window; the engine keeps running. Use 终止引擎 (Stop Engine) to actually end it. The title-bar close button minimizes to the system tray instead, and the tray menu can bring the window back or quit for real.

---

## Contributing

**If you hit a problem or have an idea for improvement, an issue or a pull request is very welcome.** Bug reports, feature suggestions and documentation fixes all count; you do not need to write code to help — describing the problem clearly is already a contribution.

The full process is in [CONTRIBUTING.md](CONTRIBUTING.md) — note that it is written in Chinese. In short:

- **One change, one thing.** A fix should only fix — no opportunistic refactoring, no drive-by rewording. Small diffs are quicker to verify and cleaner to revert.
- **Check `git status` first.** Do not mix your changes with work already sitting in the tree.
- **Self-test is the acceptance baseline, not an option.** After any change, the self-test must pass (exit code 0) before packaging. It opens no window, touches no engine and writes no real config:
  ```powershell
  bin\Release\net10.0-windows\win-x64\DSHGuard.exe --selftest
  ```
- **Interface changes need screenshots.** The self-test covers structure and values, not whether something looks right; use `--shot` and check the result by eye in both light and dark themes.
- **Packaging changes need a real install and uninstall**, both uninstall modes, with no leftover shortcuts or registry entries.
- **Interface copy is Chinese**, always — that is the project's hardest constraint, and the self-test has assertions that enforce it.

**If DSHGuard is useful to you, a Star is welcome** — it is the most direct encouragement for the author, and it helps other people who need this tool find the project.

Build commands:

```powershell
# Publish a single self-contained executable
dotnet publish -c Release

# Build
dotnet build -c Release

# Package the installer (output in dist\)
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Contributions are released under the project's MIT license.

---

## License

MIT — see [LICENSE](LICENSE).

```
Copyright (c) 2026 DSHGuard contributors
```

You are free to use, modify and redistribute this software, including commercially, as long as you keep the copyright and license notice. It is provided "as is", without warranty of any kind.
