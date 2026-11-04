# DSH 守护壳 2.3.0（2026-10-26）

这一版把 DSH 自己的资源收进一屏：**左侧导航新增「综合」功能区**，从只读展示升级为可增删改启停的真管理。完整条目、已知限制与验证数据见 [CHANGELOG.md](CHANGELOG.md) 的 `[2.3.0]` 段；发布说明正本在 GitHub Release 页面：https://github.com/gluztm/DSHGuard/releases/tag/2.3.0

- **五域分区**：插件 / MCP / Skills / 提示词 / 会话，分区记忆到 `\Config\Unified\dsh-unified.json`；**应用矩阵**行 = 一条资源、列 = 一个真实 profile（`web` / `desktop`），勾选框直接开关该资源在该 profile 里是否生效。
- **MCP 管理**：扫描并改写 `\profiles\<profile>\cordis.patch.yml` 里挂 `@deepseek-ai/dsh-mcp-client` 的条目，可新建 / 编辑 / 删除 / 启停，写前自动备份，写后重扫并如实回报。
- **Skills 管理**：按 DSH 真实 skill 根优先级扫描，启停改 frontmatter 的 `disable-model-invocation` 与 `user-invocable`，其余行逐字节保留。
- **提示词与会话管理**：`prompt-inject.md` 与 `MEMORY.md` 的读写（BOM 与换行风格按原文件保持）；会话按最后活跃时间倒序、可一键定位，删除是移到回收区，不在 sessions 根下的一律拒绝。
- **Skills 根纠正**：上一版把 `~/.dsh/redteam/skills` 当成 Skills 根，那不是 DSH 的任何 skill 根，展示的是幽灵数据；本版按 DSH 的 rank 表扫真实根。
- **三态回报**：每条资源区分「已生效 / 未生效 / 写入失败」，失败显示原因并可重试，不再把失败画成成功。
