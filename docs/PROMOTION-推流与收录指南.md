# DSHGuard 推流与生态收录指南

因为 **DSHGuard** 本质上是一个 **Windows 进程外桌面守护程序（`.exe`）**，仅靠独立 GitHub 仓库很难被平时只在 **DSH（DeepSeek Harness）插件市场**里找工具的用户发现。为此，仓库内已经随仓搭建好合规、真实可用的跨平台官方只读伴生插件子包 [`@gluztm/dsh-guard`](../packages/dsh-guard/README.md)，通过下面 3 步就能打通 DSH 生态官方流量池！

---

## 第一步：补充 GitHub 仓库 Topics 与简介（1 分钟完成）

进入 GitHub 仓库主页 `https://github.com/gluztm/DSHGuard`，点击右侧 **About** 齿轮图标：

1. **Description（简介）**建议填写：
   > 🛡️ Out-of-band Windows guard shell, Web/Desktop dual-track plugin manager, snapshot rollback & CC Switch credential vault for DeepSeek Harness (DSH). Includes cross-platform companion plugin `@gluztm/dsh-guard`.

2. **Website**：填写 `https://github.com/gluztm/DSHGuard/releases/latest`（或项目首页）。

3. **Topics（标签，务必包含 `dsh-plugin` 与 `dsh`，这也是生态索引硬要求）**：
   ```text
   deepseek-harness dsh dsh-plugin cc-switch mcp ai-agent credentials plugin-manager windows wpf dotnet deepseek
   ```

---

## 第二步：提交 PR 到 `awesome-dsh-plugin` 官方插件市场（核心推流入口）

`DSHGuard` 内部的「寻找插件」以及社区 DSH 插件生态、生态趋势榜，全部由 `https://github.com/awesome-dsh-plugin/awesome-dsh-plugin` 索引驱动。

Fork `awesome-dsh-plugin/awesome-dsh-plugin` 后，在其 `data/plugins/` 目录下**仅新增一个条目文件**（禁止手改 `README.md` / `README.zh.md`，合并后由仓库工作流自动生成）：
- **文件名**：`data/plugins/gluztm__DSHGuard--packages-dsh-guard.yml`
- **文件内容**（严格遵循 `scripts/lib/entries.mjs` 允许的 Schema：仅含 `url`、`name`、`category`、`description`、`icon`、`tarball`，禁止填写 `repo`/`package`/`screenshots` 等非法键）：

```yaml
url: https://github.com/gluztm/DSHGuard/tree/main/packages/dsh-guard
name: gluztm/DSHGuard#dsh-guard
category: tools
description:
  en: "Cross-platform DSH profile manager & Web UI sidebar: toggle plugins & MCP servers with auto-backups, repair broken symlinks, audit llm-pi-ai & blind-write .credentials.yaml, and restore local YAML/JSON config snapshots."
  zh: "跨平台 DSH 伴生管理插件：直接在 Web 侧边栏开关插件与 MCP（变更前自动备份）、修复断裂依赖、单向盲写 .credentials.yaml、审计 llm-pi-ai 与一键回滚本地 YAML 配置快照。"
icon: https://raw.githubusercontent.com/gluztm/DSHGuard/main/packages/dsh-guard/assets/icon.png
tarball: https://github.com/gluztm/DSHGuard/releases/download/2.4.2/dsh-guard-2.4.2.tgz
```

> [!TIP]
> **注意推送时序与包名防重**：由于 npm 上无作用域名称 `dsh-guard` 已被其他作者占用，伴生包统一命名为 `@gluztm/dsh-guard` 并附带 `tarball` 指向 GitHub Release `.tgz`。提交收录 PR 前，请务必确保：
> 1. `main` 已包含最新 `packages/dsh-guard/package.json`、`screenshots.json` 与 `assets/icon.png`（保证 `icon` 与截图在线 HTTP 200 可访问）；
> 2. **`2.4.2` GitHub Release 已正式发布且已上传 `dsh-guard-2.4.2.tgz` 与 `DSHGuard-Setup-2.4.2.exe`**（保证 `tarball` 与校验产物可下载），再向 `awesome-dsh-plugin` 提交或更新 PR 即可一次性通过自动化检查！

---

## 第三步：在其它 DSH 插件/项目末尾挂载互链徽章

如果你同时维护多个 DSH 相关开源项目，可以在它们的 README 末尾或安装注意事项里挂上一枚紧凑徽章，形成交叉导流：

```markdown
[![DSHGuard Supervisor](https://img.shields.io/badge/Protected_by-DSHGuard_守护壳-2563EB?style=flat-square&logo=whale&logoColor=white)](https://github.com/gluztm/DSHGuard)
```

---

## 社区发帖宣传模板（可直接复制发往技术社区 / 交流群）

> **给 DeepSeek Harness（DSH）做了一套双形态效率工具：跨平台伴生扩展 `@gluztm/dsh-guard`（Web 侧边栏插件/MCP 开关、断链修复、凭据单向盲写与快照回滚）+ 进程外 Windows 守护壳 `DSHGuard 2.4.2`**
>
> 用 DSH 最头疼的三件事：
> 1. 装坏一个插件或改乱 `cordis.patch.yml`，DSH 自己都起不来了，进程内插件全灭谁来救？
> 2. Web 版和官方 Electron 桌面版插件各走各的目录，官方 CLI 还拒绝管理桌面版。
> 3. Claude Code / Codex / Gemini / DeepSeek 原本配置在 CC Switch 里，迁到 DSH 得一条条重填 API Key、Base URL 与模型列表。
>
> 于是做了这套 **内置能力优先 + 进程外安全兜底** 的组合方案：
> - 🧩 **直接安装伴生插件 `@gluztm/dsh-guard@2.4.2`（全平台可用，无需装桌面端）**：Web 右侧栏即刻管理双轨插件与 MCP 开关（修改前自动留存快照）、一键修复 `node_modules` 断裂软链接、审查 `llm-pi-ai` 并向 `.credentials.yaml` 纯单向盲写密钥、对比文件 SHA256 差异一键回滚 YAML 配置！
> - 🐋 **Windows 用户可选独立进程外守护壳 `DSHGuard`（免管理员一键安装）**：补充插件运行时物理上无法实现的重型能力 —— 点小鲸鱼抛硬币切双轨、`node_modules` 整包 ZIP 回滚、主程序崩溃带外急救、Windows DPAPI 凭据加密与 CC Switch 多源迁移！
>
> 下载与源码：https://github.com/gluztm/DSHGuard
