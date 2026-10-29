# DSHGuard 2.2.3

> 发布日期：2026-10-24
>
> 本次更新由 Codex、GLMZCode、DeepSeek 联合完成。

2.2.3 是一次面向桌面版插件更新可靠性的修复版本，重点解决“更新失败、再次更新被锁、更新后版本不变”以及桌面版被误报缺少 DSH 本体的问题。

## 本次更新贡献者

- **Codex（OpenAI）**：现场故障定位、修复实现、自检、打包与发布统筹。
- **GLMZCode**：独立审计、交叉复核与修复建议。
- **DeepSeek**：跨版本审计、回归复核与联合修复。

## 重点修复

- **桌面版本体误告警**：同时识别 Web 版与桌面版安装，桌面版用户不再反复看到“缺 DSH 本体”。
- **pnpm 工作区历史配置阻塞**：安装、更新、卸载、批量更新和按清单重装共用自愈流程；失效 patch、`allowBuilds` 占位值和 scoped 包名 YAML 键会被安全修复。
- **半截安装树导致反复失败**：更新前检查锁文件、`node_modules`、pnpm 元数据和每个直接依赖；缺包、空目录、缺 `.modules.yaml` 会先被点名并恢复。
- **文件占用提示更准确**：识别 `os error 32/5`、`EPERM`、`EBUSY`、中文“拒绝访问/另一个程序正在使用此文件”等，明确提示先完全退出 DSH。
- **半更新不再误报成功**：安装目录、`package.json`、`pnpm-lock.yaml` 根 importer 与 pnpm 元数据必须完整提交。
- **范围声明不再假失败**：支持 `*`、`1.x`、`1.2.x`、`1.2`、`^`、`~`、`~>`、hyphen range、比较器组和 `||`，并用 npm semver 语义核验实际安装版本。
- **旧锁文件不会被放行**：锁文件 `specifier` 必须与清单逐字一致；`NotSatisfied` 永不 rescue，只有无法静态还原的 dist-tag `Unknown` 可在锁文件事实成立时放行。
- **一键更新、单插件更新、批量更新、按清单重装统一判据**，避免不同入口对“成功”的定义漂移。

## 验证

- Release build：0 warning / 0 error
- 源码自检：**966 项，963 PASS / 0 FAIL / 3 SKIP**
- 重建 payload 后自检：**966 项，963 PASS / 0 FAIL / 3 SKIP**（退出码 0）
- npm semver 外部对照：**2380 组，0 个硬不一致**；35 个空目标按 DSHGuard 既有设计返回 `Unknown`，其余 **2345/2345** 一致
- 2.2.3 与 2.3.0 双向符号扫描均为 0 命中

## 下载文件

发布时附带：

- `DSHGuard-Setup-2.2.3.exe`
  - SHA-256：`F0D1A264E8ACA175116BE9482EBBE9D5BEB2216E5569724C0EFF55EAB33DD09C`

安装包内主程序：

- `DSHGuard.exe`
  - SHA-256：`8C4CF38B71BCA7ABE48B8069D7BB957A819F3BF6A566F6FB5D76C437393FF5D3`
  - Version：`2.2.3.0+a999ba670936812563f680bcca54ff27532f9ede`

## 升级提示

1. 更新或覆盖安装前，先完全退出 DSH 与 DSHGuard。
2. 如果插件更新过程中提示 profile 文件被占用，请完全退出 DSH 后重试；不要把该提示当作网络错误。
3. 3 个自检 SKIP 属于既有环境项，不影响本次发布门禁。

## 已知限制

- 真实 desktop profile 的写更新未在本机执行；当前验证基于源码、临时夹具和现场只读证据。
- `RunPluginCmdAsync` 在命令退出 0 但无法确认完成时不会立即回滚 `package.json`，由后续完整提交判据失败关闭。
- `PnpmUsesHoistedNodeLinker` 对 JSON 引号键的解析仍有回退路径依赖。
- 便携版和探针包本版本未重建。

## 完整性

发布前请以本文件的 SHA-256 和构建日志为准；任何重新构建后必须重新计算并更新校验值。