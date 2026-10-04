# 贡献者

本项目由以下人员与 AI 协作完成。感谢每一方的投入。

| 贡献者 | 参与内容 |
| --- | --- |
| [gluztm](https://github.com/gluztm) | 项目发起人与维护者 |
| [deepseek-ai](https://github.com/deepseek-ai) | 1.0 版本总体架构设计与程序基础实现 |
| Claude | 2.0 起接手的顶层设计，功能区重构与新功能开发 |
| ChatGPT | 辅助开发与审计工作 |

---

## 关于 GitHub 的 Contributors 列表

GitHub 仓库页右侧的 **Contributors** 是按 **提交作者 / 共同作者的邮箱** 自动统计的，
它只认得「与某个 GitHub 账号关联的邮箱」。本项目历史提交（2.0 之前）的作者统一是
`DSHGuard <dshguard@users.noreply.github.com>`（仓库自身的标识邮箱，不对应任何 GitHub 账号），
那些提交不会被计入。

**当前做法**：自 2.1.0 起的提交在 `Co-authored-by` 尾注里带上各协作者对应的官方
noreply 邮箱，GitHub 会把它们一并计入 Contributors：

| 协作者 | 尾注邮箱 | 账号 |
| --- | --- | --- |
| Claude | `noreply@anthropic.com` | [@claude](https://github.com/claude) |
| ChatGPT | `noreply@openai.com` | [@codex](https://github.com/codex) |
| DeepSeek | `deepseek-ai@users.noreply.github.com` | [deepseek-ai](https://github.com/deepseek-ai) |

已核实：Claude 与 ChatGPT 在仓库 Contributors 列表中分别显示为 **claude** 与 **codex**
（列表显示的是账号自身名称，不是尾注里的文本）。**deepseek-ai 是 GitHub 组织账号**，
组织能否作为共同作者计入由 GitHub 的统计口径决定，本地无法验证；即使未计入，
下表与 `README.md` 的署名也已经把它写明。

本项目遵循「不改写已推送历史」的既定约定（历史提交已被 tag 与 Release 钉住），
不采用改写作者信息的方式补录。

本文件与 `README.md` 的「贡献者」表，是目前**确定性可见**的署名方式。
