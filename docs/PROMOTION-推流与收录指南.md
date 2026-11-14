# DSHGuard 推流与生态收录指南

因为 **DSHGuard** 本质上是一个 **Windows 进程外桌面守护程序（`.exe`）**，仅靠独立 GitHub 仓库很难被平时只在 **DSH（DeepSeek Harness）插件市场**里找工具的用户发现。为此，仓库内已经随仓搭建好合规、真实可用的官方只读伴生插件子包 [`packages/dsh-guard`](../packages/dsh-guard/README.md)，通过下面 3 步就能打通 DSH 生态官方流量池！

---

## 第一步：补充 GitHub 仓库 Topics 与简介（1 分钟完成）

进入 GitHub 仓库主页 `https://github.com/gluztm/DSHGuard`，点击右侧 **About** 齿轮图标：

1. **Description（简介）**建议填写：
   > 🛡️ Out-of-band Windows guard shell, Web/Desktop dual-track plugin manager, snapshot rollback & CC Switch credential vault for DeepSeek Harness (DSH). Includes official companion DSH plugin `dsh-guard`.

2. **Website**：填写 `https://github.com/gluztm/DSHGuard/releases/latest`（或项目首页）。

3. **Topics（标签，务必包含 `dsh-plugin` 与 `dsh`，这也是生态索引硬要求）**：
   ```text
   deepseek-harness dsh dsh-plugin cc-switch mcp ai-agent credentials plugin-manager windows wpf dotnet deepseek
   ```

---

## 第二步：提交 PR 到 `awesome-dsh-plugin` 官方插件市场（核心推流入口）

`DSHGuard` 内部的「寻找插件」以及社区 DSH 插件生态、生态趋势榜，全部由 `https://github.com/awesome-dsh-plugin/awesome-dsh-plugin` 索引驱动。

Fork `awesome-dsh-plugin/awesome-dsh-plugin` 后，在其 `data/plugins/` 目录下新增一个文件：
- **文件名**：`data/plugins/gluztm__DSHGuard--packages-dsh-guard.yml`
- **文件内容**（已按官方 `contributing.md` Schema 严格对齐：声明 monorepo 子包 `packages/dsh-guard`、附带有效 `dsh.bundle.patch`、真实工具/命令无虚构、附带 `2.4.0` 高清预览图）：

```yaml
name: gluztm/DSHGuard#dsh-guard
repo: gluztm/DSHGuard
package: packages/dsh-guard
url: https://github.com/gluztm/DSHGuard/tree/main/packages/dsh-guard
category: tools
description:
  zh: DSH 守护壳（DSHGuard）官方伴生插件：在对话内只读体检 Web/桌面双轨插件清单与 API 凭据键就绪态（严格脱敏）、查看变更前快照清单，并一键唤起 Windows 进程外守护壳（含凭据保险箱与 CC Switch 多源一键导入）。
  en: Official read-only companion plugin for DSHGuard (Windows out-of-band supervisor). Audits Web/Desktop profile health and masked credential readiness, lists pre-change snapshots, and bridges to the DSHGuard desktop app.
screenshots:
  - https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/240-status-running-dark.png
  - https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/240-vault-dark.png
  - https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/240-ccswitch-import-dark.png
```

> [!TIP]
> **注意推送时序**：请先将本次 `2.4.0`（含 `packages/dsh-guard` 与 `docs/screenshots/240-*.png`）推送到 `gluztm/DSHGuard` 的 `main` 分支，确保 `raw.githubusercontent.com` 截图链接和 `packages/dsh-guard/package.json` 已在线可访问，再向 `awesome-dsh-plugin` 提交该 PR，即可一次性通过自动化检查！

---

## 第三步：在其它 DSH 插件/项目末尾挂载互链徽章

如果你同时维护多个 DSH 相关开源项目，可以在它们的 README 末尾或安装注意事项里挂上一枚紧凑徽章，形成交叉导流：

```markdown
[![DSHGuard Supervisor](https://img.shields.io/badge/Protected_by-DSHGuard_守护壳-2563EB?style=flat-square&logo=whale&logoColor=white)](https://github.com/gluztm/DSHGuard)
```

---

## 社区发帖宣传模板（可直接复制发往技术社区 / 交流群）

> **给 DeepSeek Harness（DSH）做了个进程外“安全气囊”桌面管家：DSHGuard 2.4.0（支持 Web + 官方桌面版双轨、快照回滚 & CC Switch 一键导入）**
>
> 用 DSH 最头疼的三件事：
> 1. 装坏一个插件或改乱 `cordis.patch.yml`，DSH 自己都起不来了，进程内插件全灭谁来救？
> 2. Web 版和官方 Electron 桌面版插件各走各的目录，官方 CLI 还拒绝管理桌面版。
> 3. Claude Code / Codex / Gemini / DeepSeek 原本配置在 CC Switch 里，迁到 DSH 得一条条重填 API Key、Base URL 与模型列表。
>
> 于是做了这个 **Windows 独立进程外守护壳 `DSHGuard`**（免管理员一键安装，零门槛中文 GUI）：
> - 🐋 **左上角点一下小鲸鱼，Web 版 / 官方桌面版双轨无缝切换**；
> - ⏪ **改任何配置与插件前自动打快照**，引擎起不来照样进程外一键回滚 + 锁定长期稳定版；
> - 🔐 **2.4.0 全新凭据中枢**：API 密钥严格脱敏收纳 + 8 套模型预设一键套用 + **CC Switch 多源预览导入**（直读本机 `cc-switch.db`、`ccswitch://` 深链、剪贴板与外部备份文件）+ MCP / Skills / 提示词 / 会话六合一综合管理；
> - 🧩 还附带了官方只读伴生插件 `packages/dsh-guard`（在 DSH 里输入 `/guard` 就能体检并直接呼出守护壳）+ 内置 10,700+ 插件市场浏览器！
>
> 下载与源码：https://github.com/gluztm/DSHGuard
