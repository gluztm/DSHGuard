# @gluztm/dsh-guard

> 面向 **DeepSeek Harness (DSH)** 的跨平台伴生配置与扩展管理插件：无需额外安装独立软件，直接在 Web 侧边栏与全屏页面内完成 **插件 / MCP 开关（变更前自动备份）**、**断裂依赖修复**、**`llm-pi-ai` 巡检与 `.credentials.yaml` 单向盲写** 及 **本地 YAML/JSON 配置快照 + SHA256 差异回滚**。

[English Documentation](./README.en.md) · [GitHub 仓库](https://github.com/gluztm/DSHGuard) · [Releases](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)

![@gluztm/dsh-guard v2.4.2 侧边栏实景：插件启停、凭据盲写与快照回滚](https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/242-companion-sidebar.png)

## 伴生插件内置核心能力

1. **插件与 MCP 实时启停开关（Web / Desktop 双轨）**
   - 自动解析各插件包 `dsh.bundle.patch` / `cordis.patch.yml` 的 `loaderId` 及 `mcp-client` 条目。
   - 搜索过滤 MCP 服务与已安装插件；内置保护标记（禁止误关 `@deepseek-ai/*` 核心组件或插件自身）。
   - 每次切换开关前都会自动保存一份配置快照（`auto-toggle`），按 Cordis 规则安全引用 `- id:`。
2. **双轨健康巡检与一键断链修复**
   - 对比 `profiles/web` 与 `profiles/desktop` 的 `dependencies`、`dsh.bundles` 漂移与 `cordis.patch.yml` 哈希。
   - 检测并直接修复 `node_modules` 下失效的符号链接 / NTFS Junction，自动链接至 `.dsh-module-fallback` 或兄弟档案。
3. **模型提供商巡检与 `.credentials.yaml` 纯单向盲写**
   - 汇总 `llm-pi-ai` 中配置的各家协议（`anthropic-messages` / `openai-completions` / `google-generative-ai`）、`baseURL`、模型数量与环境变量就绪状态。
   - **零明文回显**：可在前端为提供商或自定义环境变量名设置 API Key，服务端采用「只写入 `refs:`、保留 `version`/`records`/`*_ACCOUNT_*` OAuth 块、响应绝不返回明文」的单向盲写模型（通过 `dsh-trust-check` 静态扫描零红线）。
4. **原生插件快照与实时文件 SHA256 差异回滚**
   - 将 `cordis.patch.yml`、`cordis.yml`、`package.json`、`settings.yaml` 备份至 `~/.dsh/guard-snapshots`（**严格排除** `.credentials.yaml` 等任何敏感文件）。
   - 与磁盘现行配置实时比对 SHA256 状态（`一致` / `已变更` / `已删除`），支持白名单内配置文件一键安全还原（还原前自动二次备份 `pre-restore`）。
5. **内置 4 个 Agent 工具 + `/guard` 斜杠命令 + CLI**
   - Agent 工具：`dsh_guard_status`、`dsh_guard_snapshots`、`dsh_guard_backup`、`dsh_guard_open`
   - 斜杠命令：`/guard`、`/guard status`、`/guard snapshots`、`/guard backup [web|desktop|all]`、`/guard open`
   - CLI：`dsh-guard status [--json]`、`dsh-guard snapshots`、`dsh-guard backup [web|desktop]`、`dsh-guard repair [web|desktop]`

---

## 技术与安全边界说明（何时需要原生桌面端）

本伴生插件已经覆盖日常高频的开关、密钥盲写与 YAML 配置备份回滚需求；以下能力由于操作系统或安全架构限制，**无法在运行中的 DSH Node 宿主插件内部安全实现**，仅在独立进程外原生程序 **[DSHGuard Desktop (Windows)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)** 中提供：

| 能力领域 | 伴生插件内置能力 | 仅独立原生桌面端提供（及原因） |
| :--- | :--- | :--- |
| **插件包生命周期** | 安全切换插件/MCP 开关、断链修复 | **全新安装 / 彻底卸载 / 强杀锁死进程**：运行时在宿主进程内删除或重装 `node_modules` 会撕裂已加载模块并触发文件占用锁死；且插件不应持有远端包拉取与进程强杀权。 |
| **凭据安全与导入** | `.credentials.yaml` 键名审计 + 单向盲写 | **明文编辑 / Windows DPAPI 硬件加密库 / CC Switch 导入**：Web 插件拥有前端请求能力，依据安全合规禁止反向回显明文；DPAPI 与跨工具配置迁移依赖 Windows 原生安全 API。 |
| **系统级备份恢复** | YAML/JSON 配置文件实时对比与回滚 | **整包扩展目录 ZIP 归档 + 主程序崩溃带外恢复**：涵盖整套 `node_modules` 与主程序启停的重型恢复必须脱离 DSH 宿主进程独立调度。 |

- 若本机已安装 Windows 原生桌面端，插件右上角会自动显示「打开桌面端」快捷入口。
- 仅需要上述带外桌面功能时，可在 [GitHub Release v2.4.2](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2) 获取 [DSHGuard-Setup-2.4.2.exe](https://github.com/gluztm/DSHGuard/releases/download/2.4.2/DSHGuard-Setup-2.4.2.exe)（源码可审计，基于 .NET 8 + WPF 原生构建）。

## 许可协议

MIT © gluztm
