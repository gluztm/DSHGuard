# `@gluztm/dsh-guard` 伴生插件长期演进计划书（DSH Control Plane）

> **定位宣言**：将 `@gluztm/dsh-guard` 建设成为 **DeepSeek Harness (DSH)** 的跨平台运行时底座总管与治理控制面（DSH Control Plane）。  
> **核心分工**：监督型插件/主代理专注**任务流程、上下文记忆与执行纪律**；`@gluztm/dsh-guard` 专注 **当前运行端底座健康、扩展负载、MCP/路由稳定性、存储生命周期与可逆安全护栏**。两者职责正交，通过标准化低 Token 工具与脱敏状态文件自然协同。

---

## 一、全局设计原则

1. **三阶风险分级模型（自然兼容人类审批与监督 Agent）**
   - **L0 · 只读态势层**：当前运行端自动锁定扫描、上下文负担画像、MCP 安全探活、日志浓缩故障雷达、存储热力图、按需显式配置 Diff 对比、安全基线审计。任何只读角色均可零风险调用。
   - **L1 · 可逆自愈层**：创建快照、快照置顶保护、修复断裂符号链接、导出脱敏环境蓝图、将冗余备份与旧会话移入可恢复回收区（带 Dry-Run 预览）。
   - **L2 · 底座变更层**：插件/MCP/Skills 开关、MCP 超时改写、场景套件一键切换、`web` / `desktop` 按需选择性同步、无密钥模型路由微调、快照还原。一律遵循 **`Dry-Run 预览 / Diff → 自动前序快照 → 原子写入 → 脱敏审计流水`** 四步事务。
2. **严格通用性与隐私安全红线**
   - 所有发现、扫描、预设与建议完全基于 DSH 官方标准运行端（`desktop` 与 `web`）和标准协议规范动态推导，绝不扫描无关自定义目录或依赖特定机器私有组件。
   - 严守零密钥泄密：对 `.credentials.yaml` 永不做明文读取、切片或回显，仅核验键名存在性并单向写入；严格保护 `*_ACCOUNT_*` OAuth 授权段。
   - 网络操作克制合规：本地服务探活严格限制回环地址（`127.0.0.1` / `localhost` / `::1`），消除 SSRF 风险；不引入第三方外部运行时依赖。
   - 低 Token 信息密度：工具与命令输出默认“结论先行 + 异常优先 + 可钻取”，避免向模型上下文倾倒大段原始 YAML 或全量日志；常规巡检零跨端冗余。

---

## 二、分阶段演进路线

### Phase 1（v2.4.3 · 已完成落地）：单端聚焦 · 瘦身提效 · 探活与容错自愈
聚焦“只看当前正在用的端、管住开机上下文膨胀、提前发现坏掉的 MCP”：
- **单端智能识别与零冗余界面**：启动时自动识别宿主运行端——桌面端只检视 `desktop`，Web 端只检视 `web`，不扫描无关实验性或私有目录，并移除无意义的跨端漂移冗余告警。
- **断链容错自愈**：检测并修复当前端 `node_modules` 失效符号链接 / NTFS Junction；对本地无法找到目标源的孤立断链以 HTTP 200 返回精确清单与处理建议，避免界面报 HTTP 400。
- **MCP 服务超时调优与安全本地探活**：支持对 `@deepseek-ai/dsh-mcp-client` 无损调整 `toolCallTimeoutMs`（`5000`–`600000 ms`）；提供回环端口 TCP 探活与 `stdio` 命令/入口脚本文件校验，输出紧凑绿/黄/红诊断视图。
- **上下文负担体检与 Skills 可见性治理**：
  - 汇总当前端启用扩展、启用 MCP、常驻提示词文件（`prompt-inject.md`、`MEMORY.md`、`prompts/*.md`、`system-prompt`）估算体积与开机基线 Token 负荷。
  - 统一扫描 `~/.dsh/skills`、`~/.agents/skills` 与插件内置只读 `SKILL.md`，支持对用户技能无损切换 `disable-model-invocation` 与 `user-invocable`（带 SHA256 指纹防冲突检查）。
- **凭据预设通用化 & 快照置顶与脱敏审计流水**：
  - 凭据新增抽屉集成 7 套主流通用模型平台环境变量预设；
  - 插件原生快照支持 **📌 置顶保护**（自动清理轮转跳过置顶快照与手动快照）；
  - 全变更动作写入 `~/.dsh/guard-state/audit.jsonl`（纯结构化脱敏记录）。
- **扩展 Agent 能力与 CLI**：新增 L0 只读工具 `dsh_guard_context_audit` 与 `dsh_guard_mcp_probe`、`/guard context|mcp` 斜杠命令及 `dsh-guard context|mcp-probe|audit` CLI 命令（支持 `--target desktop|web`）。

---

### Phase 2（v2.5.0 · 已完成落地）：场景一键换装 · 显式按需双端同步 · 存储卫生与日志雷达
聚焦“根据任务随时切换 DSH 工作形态、治理长期使用产生的磁盘膨胀与隐性报错”：
1. **工作场景套件（Scene / Loadout Manager，`lib/scenes.mjs`）**
   - 内置 `lean-coding`（轻量编码）、`creative-full`（全量创作）、`safe-mode`（安全极简）3 套预设模板及 `~/.dsh/guard-state/scenes.json` 自定义场景保存/删除；依托通用扩展分类器动态识别扩展类别，支持 L0 Dry-Run 预览与 L2 事务切换（自动 `auto-scene` 快照 + 单条 `scene.apply` 聚合审计），始终保护 `@deepseek-ai/*` 核心组件与插件自身。
2. **显式按需 `web` ↔ `desktop` 配置 Diff 与无损合并（`lib/sync.mjs`）**
   - 保持常规巡检零跨端冗余；仅在显式发起 `profile-diff` / `/guard diff` / 面板比对时比较 `llm-providers`、`mcp-servers`、`permissions` 区块，无损保留 `!!js` 标签、行内注释与目标端独有条目，零明文密钥处理，合并前自动写入 `auto-sync` 快照与审计记录。
3. **`~/.dsh` 存储空间热力图与可逆回收站（`lib/hygiene.mjs`）**
   - 有界盘点 10 个标准分区（`cache`、`profiles/*/*.bak*`、`attachments`、`sessions-offline`、`sessions`、`rewind-snapshots`、`undo-snapshots`、`guard-snapshots`、`dsh-runtimes`、`guard-recycle`），强制跳过软链接、置顶快照与全部凭据/密钥备份文件（`.credentials.yaml.bak-*`）；支持 Dry-Run 预估、可逆移入 `~/.dsh/guard-recycle/<batchId>` 与一键还原。
4. **运行时低 Token 日志故障雷达（`lib/hygiene.mjs`）**
   - 有界读取 `~/.dsh/logs` 与桌面端宿主日志（单文件末尾 256 KB、最多 40 文件），严格脱敏 API Key / Bearer Token / 家目录路径，过滤分隔符噪音，按错误签名聚合去重并给出针对性处置建议；新增 `dsh_guard_scenes`、`dsh_guard_profile_diff`、`dsh_guard_storage_audit`、`dsh_guard_log_radar`（累计 10 个 Agent 工具）、22 条带鉴权 HTTP 路由、侧边栏第 6 页签「维护 / Ops」及对应 `/guard` 与 CLI 子命令。

---

### Phase 3（v2.6.0 · 深度治理与可迁移底座）：模型路由无密钥微调 · 安全基线 · 环境蓝图
聚焦“把运行底座管成可版本化、可校验、可迁移的工程资产”：
1. **无密钥模型路由微调与通道健康矩阵**
   - 检查所有 `llm-pi-ai` 提供商的协议与 `baseURL` 规范匹配度（如路径后缀一致性告警、孤儿凭据键识别）。
   - 支持在不触碰任何 API Key 明文的前提下，启用/停用单个模型、微调推理档位与缓存保留参数，并自动记录变更快照。
2. **宿主安全基线体检**
   - 只读审计各 Profile 中权限预设（`read-only` / `workspace-write` 等沙箱级别与审批策略）、开发者开关与外连桥接状态，产出客观合规简报。
3. **脱敏环境蓝图导出与引导导入（Sanitized Blueprint）**
   - 一键生成零密钥、零账号令牌的纯结构化环境配方文件（Profile 清单、扩展依赖清单、MCP 模板、模型路由结构、Skill 可见性开关态），用于跨机器快速重建一致的 DSH 工作环境或社区排障交流。

---

### Phase 4（未来开放协同）：面向监督型 Agent / 参谋长的标准化治理接口契约
本阶段**不预先耦合具体外部插件的私有 API**，通过清晰边界与标准化脱敏状态文件实现天然可插拔接入：
1. **标准化只读态势投影**：在 `~/.dsh/guard-state/health-summary.json` 维护最近一次脱敏体检摘要（Profile 列表、异常项、上下文负荷等级、MCP 探活状态、近期审计时间戳），任何授权插件均可零开销读取。
2. **L0–L2 能力与审批流对接预留**：
   - 上层协调者（如 `dsh-chief`）可直接调用 **L0 只读工具** 感知底座瓶颈（例如在开启高强度任务前先检查所需 MCP 是否存活、当前上下文是否过载）；
   - 需要执行 **L2 底座变更**（如切换安全排障模式、停用拖累上下文的 MCP、回滚配置）时，伴生插件输出标准结构化 `{ planId, summary, diffPreview, rollbackSnapshotId }` 变更单，可由人类审批确认，也可在未来经明确授权机制交由监督型 Agent 协同编排。
3. **带外重型动作双向工单升级**：对于必须进程外处理的动作（强杀文件锁占用进程、冷安装/卸载 npm 包、Windows DPAPI 保险箱加密），伴生插件输出标准建议工单，由用户或原生桌面端（`DSHGuard.exe`）接力闭环。
