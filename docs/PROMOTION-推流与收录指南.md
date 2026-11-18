# DSHGuard 推流与生态收录指南

**DSHGuard** 本体是 Windows 进程外桌面程序（`.exe`），为了方便只在 **DSH（DeepSeek Harness）插件市场**里搜工具的用户（包括 macOS / Linux 用户）也能直接用上插件管理、MCP 开关、断链修复和配置回滚，仓库在 `packages/dsh-guard` 下附带了跨平台伴生插件 [`@gluztm/dsh-guard`](../packages/dsh-guard/README.md)。

---

## 第一步：更新 GitHub 仓库右上角 About（Description + Topics）

进入 `https://github.com/gluztm/DSHGuard`，点击右侧 **About** 齿轮图标：

1. **Description（简介）**建议填写：
   > Windows launcher, crash recovery, Web/Desktop plugin manager & .credentials.yaml key tool for DeepSeek Harness (DSH), plus cross-platform companion plugin @gluztm/dsh-guard.

2. **Website**：`https://github.com/gluztm/DSHGuard/releases/latest`

3. **Topics（含 `dsh` / `dsh-plugin` 市场硬要求 + 痛点搜索词）**：
   ```text
   deepseek deepseek-harness dsh dsh-plugin dsh-desktop dshguard startup-recovery crash-recovery yaml-rollback config-backup plugin-manager mcp-manager broken-symlink credentials-yaml api-key cc-switch launcher windows wpf dotnet
   ```

---

## 第二步：同步 `awesome-dsh-plugin` 官方插件市场索引

DSHGuard 内部的「寻找插件」以及社区 DSH 插件市场均由 `https://github.com/awesome-dsh-plugin/awesome-dsh-plugin` 驱动。

在 `data/plugins/gluztm__DSHGuard--packages-dsh-guard.yml` 维护条目（严格匹配 `scripts/lib/entries.mjs` 允许的字段：`url`、`name`、`category`、`description`、`icon`、`tarball`）：

```yaml
url: https://github.com/gluztm/DSHGuard/tree/main/packages/dsh-guard
name: gluztm/DSHGuard#dsh-guard
category: tools
description:
  en: "Web sidebar & CLI manager for DSH: toggle plugins and MCP servers with backups, fix broken node_modules symlinks, check llm-pi-ai providers, write .credentials.yaml keys one-way, and roll back cordis.patch.yml diffs."
  zh: "跨平台 DSH 伴生插件：直接在网页侧边栏开关插件和 MCP（改前自动备份）、修复失效的 node_modules 软链接、单向填入 .credentials.yaml API Key，并在配置改乱后按 SHA256 一键还原。"
icon: https://raw.githubusercontent.com/gluztm/DSHGuard/main/packages/dsh-guard/assets/icon.png
tarball: https://github.com/gluztm/DSHGuard/releases/download/2.4.2/dsh-guard-2.4.2.tgz
```

> [!TIP]
> 提交 `awesome-dsh-plugin` PR 前请确认 `2.4.2` Release 的 `dsh-guard-2.4.2.tgz`、`DSHGuard-Setup-2.4.2.exe` 与 `icon.png` 均在线返回 HTTP 200。

---

## 第三步：在同生态项目 README 互链（可选）

```markdown
[![DSHGuard](https://img.shields.io/badge/Recovery-DSHGuard-2563EB?style=flat-square)](https://github.com/gluztm/DSHGuard)
```

---

## 社区发帖参考模板（平实分享口吻）

> 顺手给 DeepSeek Harness（DSH）写了个图形化启动器与故障恢复工具（`DSHGuard 2.4.2`），另外打包了一个跨平台伴生插件 `@gluztm/dsh-guard`。
>
> 平时用 DSH 经常碰到三件麻烦事：
> 1. 装错插件或者 `cordis.patch.yml` 缩进写错，DSH 启动直接崩溃，插件面板也进不去；
> 2. 同时用网页版和官方桌面版（Desktop），两边目录各管各的，官方 CLI 不接管桌面版目录；
> 3. 手改 `.credentials.yaml` 很容易改坏格式，换用 Claude Code / Codex / Gemini 的 Key 还得一条条复制黏贴。
>
> 所以按“能留在插件里解决的放插件，必须要进程外处理的放独立客户端”拆成了两套：
> - **跨平台伴生插件 `@gluztm/dsh-guard`（Windows / macOS / Linux 都能装）**：直接在 DSH 网页侧边栏里开关插件和 MCP（改前自动备份）、修复 `node_modules` 失效软链接、检查 `llm-pi-ai` 并单向填写入 `.credentials.yaml`（接口不回传明文），配置改坏了按 SHA256 差异一键回滚 YAML。
> - **Windows 独立启动器 `DSHGuard.exe`（免管理员安装）**：进程挂了也能照常打开看尾部失败日志并导出诊断包，点击左上角图标切换 Web 版和官方桌面版、支持 `node_modules` 整包还原与版本降级、拖入 URL+Key 文件或直接读取本机其他 Agent 的非订阅明文密钥。
>
> 仓库与下载：https://github.com/gluztm/DSHGuard
