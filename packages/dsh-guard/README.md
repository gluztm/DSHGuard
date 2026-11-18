# `@gluztm/dsh-guard`

> **DeepSeek Harness（DSH）的跨平台配置开关、断链修复与 YAML 快照回滚伴生插件**：不用装额外桌面软件，在 DSH 网页右侧栏和终端里就能直接开关插件和 MCP、修好失效的 `node_modules` 软链接、检查模型提供商并单向填入 `.credentials.yaml` API Key，或者在 `cordis.patch.yml` 改乱后按文件哈希对比一键还原。

[English](./README.en.md) · [主仓库 README](../../README.md) · [Releases (`2.4.2`)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)

![@gluztm/dsh-guard v2.4.2 侧边栏界面：插件开关、凭据只写与快照回滚](https://raw.githubusercontent.com/gluztm/DSHGuard/main/docs/screenshots/242-companion-sidebar.png)

## 什么时候适合直接装这个伴生插件

如果你用的是 macOS / Linux，或者在 Windows 上暂时不想单独装桌面端，又经常遇到下面这些琐事，装上这个插件就能直接在侧边栏里解决：

1. **想快速启用 / 停用某个插件或 MCP 服务，但不想手动改 YAML 缩进**
   - 自动解析各插件 `dsh.bundle.patch` / `cordis.patch.yml` 的 `loaderId` 与 `mcp-client` 服务条目，勾选或关闭即可。
   - 保护 `@deepseek-ai/*` 核心组件与插件自身，防止手滑停用。
   - 每次修改前自动保存一份 `auto-toggle` 快照。
2. **更新或移动目录后 `node_modules` 软链接 / NTFS Junction 断链**
   - 对照检查 `profiles/web` 与 `profiles/desktop` 的 `dependencies`、`dsh.bundles` 与 `cordis.patch.yml` 哈希。
   - 检测到失效软链接后，从 `.dsh-module-fallback` 或兄弟环境自动重新挂好，不依赖额外工具。
3. **检查 `llm-pi-ai` 模型协议并单向写入 `.credentials.yaml` API Key**
   - 一览各提供商协议（`anthropic-messages` / `openai-completions` / `google-generative-ai`）、`baseURL`、模型数量和环境变量是否就绪。
   - **纯单向写入不回显明文**：输入密钥只往 `.credentials.yaml` `refs:` 写入并保留 `version`/`records`/`*_ACCOUNT_*` OAuth 登录态，接口响应绝不回传明文。
4. **改乱配置后想对比差异、退回上一份正常配置**
   - 将 `cordis.patch.yml`、`cordis.yml`、`package.json`、`settings.yaml` 备份到 `~/.dsh/guard-snapshots`（备份严格排除 `.credentials.yaml` 等密钥文件）。
   - 对比磁盘实时 SHA256 状态（`一致` / `已变更` / `已删除`），回滚前还会再存一份 `pre-restore` 备份。
5. **在对话里或终端直接查健康度**
   - Agent 工具：`dsh_guard_status`、`dsh_guard_snapshots`、`dsh_guard_backup`、`dsh_guard_open`
   - 斜杠命令：`/guard`、`/guard status`、`/guard snapshots`、`/guard backup [web|desktop|all]`、`/guard open`
   - 终端 CLI：`dsh-guard status [--json]`、`dsh-guard snapshots`、`dsh-guard backup [web|desktop]`、`dsh-guard repair [web|desktop]`

---

## 什么情况需要搭配 Windows 桌面端 (`DSHGuard.exe`)

日常开关插件/MCP、填密钥、修断链与回滚 YAML，在伴生插件里就能做完；遇到下面几类受操作系统安全模型或进程生命周期限制的操作时，需要运行进程外的 **[DSHGuard Desktop (Windows)](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2)**：

| 使用场景 | 伴生插件可以做的 | 仅 Windows 进程外桌面端支持（原因） |
| :--- | :--- | :--- |
| **装坏插件导致 DSH 启动报错 / 卡死** | 正常启动后开关插件、修复软链接 | **全新安装 / 彻底卸载 / 强杀占用进程 / 带外日志排错**：DSH 起不来时进程内插件自己也打不开，而且在运行中删装 `node_modules` 容易触发文件锁死。 |
| **凭据明文查看与跨工具批量导入** | 键名审计 + 单向写入新密钥 | **明文编辑 / 拖入 URL+Key 文件解析 / 一键读取本机 Claude Code、Codex、Gemini 非订阅明文密钥**：带 `fetch` 能力的网页插件受安全规则限制不能读取/回传已有明文密钥。 |
| **整套目录归档恢复** | 对比并还原白名单 YAML/JSON 文件 | **整包 `node_modules` ZIP 快照回滚与 DSH 版本回退**：涉及整棵依赖树替换与进程重启的操作只能在宿主机进程之外完成。 |

- Windows 上如果本机装了 `DSHGuard.exe`，侧边栏右上角会出现「打开桌面端」快捷按钮。
- 需要桌面端可在 [GitHub Release v2.4.2](https://github.com/gluztm/DSHGuard/releases/tag/2.4.2) 下载 [DSHGuard-Setup-2.4.2.exe](https://github.com/gluztm/DSHGuard/releases/download/2.4.2/DSHGuard-Setup-2.4.2.exe)（基于 .NET 10 + WPF 构建）。

## 许可协议

MIT © gluztm
