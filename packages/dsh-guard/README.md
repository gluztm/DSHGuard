# @gluztm/dsh-guard — DSH 守护壳官方伴生插件

[![License](https://img.shields.io/badge/license-Apache%202.0-blue?style=flat-square)](../../LICENSE)
[![DSH Bundle](https://img.shields.io/badge/DSH%20Plugin-Web%20%2B%20Backend-10B981?style=flat-square)](#安装到-dsh)

[English](README.en.md) · [DSHGuard 主仓主页](../../README.md)

`@gluztm/dsh-guard` 是 **[DSHGuard（DSH 守护壳）](https://github.com/gluztm/DSHGuard)** 的官方只读伴生插件，兼顾 **DSH Web UI 侧边栏可视化面板**、**Agent 只读工具**、**/guard 聊天命令** 与 **独立体检 CLI**，跨平台（Windows / macOS / Linux）均可使用：

- **Web UI 右侧栏 / 面板（`lib/client.js`）**：安装后自动出现在 DSH 右侧工具栏与扩展面板中，深浅色主题自适应（`--dsw-alias-*` 令牌）且支持一键中/英切换：
  - **Web / 桌面双轨配置对账**：检查 `package.json` 依赖清单与 `dsh.bundles` 漂移、禁用插件数及 `cordis.patch.yml` YAML 语法完整度；
  - **凭据脱敏就绪度（零秘密读取）**：仅核对 `.credentials.yaml` 中的键是否非空、是否覆盖 `cordis.patch.yml` 引用的 `apiKeyEnv`，**绝不读取、截断或传输任何真实密钥内容**；
  - **变更前快照历史**：展示本机 `~/.dsh-guard/snapshots/` 下的最近预检与操作前快照；
  - **守护壳联动**：在 Windows 本机可一键唤起进程外 `DSHGuard.exe`（执行崩溃救援、快照回滚、8 套模型预设与 CC Switch 导入）；在 macOS / Linux 或未安装桌面壳时显示 GitHub Releases 官方下载指引。
- **Agent 只读工具 & `/guard` 命令**：
  - `dsh_guard_status`：跨 Web / 桌面双轨的只读体检与诊断建议
  - `dsh_guard_snapshots`：读取按端分类的变更前快照列表
  - `dsh_guard_open`：定位或唤起本机 Windows `DSHGuard.exe`（未安装时返回官方下载链接）
  - `/guard [status|snapshots|open]`：聊天内直达命令

> 为什么包名是 `@gluztm/dsh-guard`？因为无作用域名称 `dsh-guard` 在 npm 上已被其他作者占用，为避免社区清单路由冲突，伴生插件统一采用 `@gluztm/dsh-guard` 命名并随 [GitHub Release 2.4.0](https://github.com/gluztm/DSHGuard/releases/tag/2.4.0) 直接分发签名 `.tgz` 安装包。

---

## 安装到 DSH

### 方式 A：通过 `awesome-dsh-plugin` 插件市场（推荐）

在 DSH 桌面端 / Web 端「插件市场」搜索 **`@gluztm/dsh-guard`** 一键安装，或在终端直接从 GitHub Release 安装：

```powershell
# 安装到 DSH Web 版（包含右侧栏面板 + 工具 + /guard）
dsh plugin --profile web add https://github.com/gluztm/DSHGuard/releases/download/2.4.0/dsh-guard-2.4.0.tgz

# 同步安装到 DSH 桌面版
dsh plugin --profile desktop add https://github.com/gluztm/DSHGuard/releases/download/2.4.0/dsh-guard-2.4.0.tgz
```

### 方式 B：从源码子包本地运行只读 CLI（Node.js >= 18）

```powershell
node packages/dsh-guard/bin/dsh-guard.mjs status
node packages/dsh-guard/bin/dsh-guard.mjs snapshots --json
```

---

## 隐私与安全边界

- **严格零秘密泄露**：插件仅检查 `.credentials.yaml` 中的环境键名与是否非空，从不读取、切片、打印或通过 HTTP/工具返回值输出任何 API Key、Token 或完整配置文件原文。
- **同源校验鉴权**：服务端暴露的 `/api/dsh-guard/overview` 与 `/api/dsh-guard/open` 路由复用 DSH 的 `connection.requestRejection` 校验，仅使用服务端 `DSH_HOME` / `DSHGUARD_HOME` 环境变量路径，不接受外部路径参数。
- **只读原则**：本插件不会改写 DSH 配置、卸载或更新第三方插件；所有高风险变更（装卸更新、快照回滚、CC Switch 多源导入）均交由进程外的 Windows 桌面壳 `DSHGuard.exe` 先自动存档后隔离执行。
