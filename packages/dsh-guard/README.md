# dsh-guard

**简体中文** ｜ [English](README.en.md) ｜ [主仓库 DSHGuard](../../README.md)

**`dsh-guard` 是 [DSH 守护壳（DSHGuard）](https://github.com/gluztm/DSHGuard) 的官方伴生 DSH 插件与 CLI 只读巡检工具。**

当你想在 DSH 对话框内（Web 版或桌面版）直接检查双轨环境是否健康、API 凭据环境变量是否漏配、或查看本机守护壳已经备份了哪些快照时，它负责把 **100% 只读巡检、脱敏对账与一键唤起守护壳** 带进你的 DSH 工作流——而真正危险的回滚、进程外启动抢修与凭据导入，则交由独立运行的 Windows 桌面客户端 **DSHGuard** 兜底完成。

> [!IMPORTANT]
> **为什么主程序是 Windows 独立 `.exe` 而不是纯插件？**
> 当引擎配置 `cordis.patch.yml` 语法写坏、插件依赖损坏或端口冲突导致 DSH 根本起不来时，**所有进程内插件都会随宿主一起失效**。`DSHGuard` 作为进程外安全气囊，在 DSH 崩溃时依然活着：随时回滚历史快照、修复配置结构并导入 CC Switch 凭据；而本伴生插件 `dsh-guard` 则是你留在 DSH 内部的"前哨探头"，二者合璧体验最佳。

---

## 提供哪些能力

在 DSH 启用本插件后，会向 Agent 与会话命令面注册 **3 个工具**与 **1 个斜杠命令**：

| 类型 | 名称 | 作用与安全边界 |
| --- | --- | --- |
| Agent 工具 | `dsh_guard_status` | **只读体检**：同时扫描 `web` 与 `desktop` 双轨的 `package.json` 依赖/bundles 漂移、`cordis.patch.yml` 结构完整度、`.credentials.yaml` 引用环境变量就绪状态（**严格脱敏，绝不读取或输出任何密钥明文**），以及本机 `DSHGuard.exe` 安装状态。 |
| Agent 工具 | `dsh_guard_snapshots` | **只读清单**：按 Web / 桌面双轨列出本机 DSHGuard 已保存的变更前快照时间、项数与存档原因（支持按 `target` 过滤与限制条数）。 |
| Agent 工具 | `dsh_guard_open` | **守护壳联动**：探测本机 `DSHGuard.exe` 路径；仅当传入 `launch: true` 时在 Windows 桌面拉起守护壳窗口；若尚未安装，返回官方 GitHub Releases 最新版下载地址。 |
| 斜杠命令 | `/guard` | 无需消耗对话工具调用，直接输入 `/guard`、`/guard snapshots` 或 `/guard open` 快速查看状态或拉起守护壳。 |

---

## 如何安装

### 方式 A：通过 DSH 插件市场一键搜索安装（推荐）

在 **DSH 守护壳 → 插件 → 寻找插件**（或任意接入 `awesome-dsh-plugin` 索引的 DSH 客户端）中搜索 **`dsh-guard`** 即可一键安装。

### 方式 B：命令行安装（Web 版 Profile）

```powershell
dsh plugin --profile web add https://github.com/gluztm/DSHGuard.git#main:packages/dsh-guard
```

> 或使用 **DSHGuard（Windows 客户端）** 的 **插件页 → 寻找插件**，直接在图形界面点「安装」，同时兼容 Web 版与桌面版双轨管理。

### 方式 C：独立 CLI 快速体验

无需进入 DSH，在本仓库或装包目录即可用 Node.js（`>= 18`）直接跑只读巡检：

```powershell
node packages/dsh-guard/bin/dsh-guard.mjs status
node packages/dsh-guard/bin/dsh-guard.mjs snapshots
node packages/dsh-guard/bin/dsh-guard.mjs open
```

---

## 配套下载主程序：DSHGuard（Windows 守护壳）

想要完整体验以下功能，请前往下载官方免管理员安装器：

- **进程外一键启停引擎 & Web / 官方桌面版双轨切换**
- **变更前自动快照 & 出事按项或整组回滚**
- **2.4.0 凭据与提供商中枢 & CC Switch 多源一键导入**（直读本机 `cc-switch.db`、`ccswitch://` 深链、剪贴板提取与外部备份导入，搭配 8 套模型预设及 MCP / Skills / 提示词 / 会话六合一综合管理）
- **10,700+ 社区插件市场内置浏览器 + 生态趋势榜**

**[📥 下载最新 DSHGuard-Setup-x.y.z.exe（GitHub Releases）](https://github.com/gluztm/DSHGuard/releases/latest)**

---

## 许可证

遵循主仓库 [GNU GPL v3](../../LICENSE) 协议开源。
