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

| 协作者 | 尾注邮箱 | 计入的账号 |
| --- | --- | --- |
| Claude | `noreply@anthropic.com` | [@claude](https://github.com/claude) |
| ChatGPT | `noreply@openai.com` | [@codex](https://github.com/codex) |
| DeepSeek | `service@deepseek.com` | [deepseek-ai](https://github.com/deepseek-ai)（组织） |

### 为什么 DeepSeek 那行曾经不生效（2026-10-04 查清并修好）

GitHub 统计共同作者靠的是**把尾注里的邮箱反查到账号**，反查规则分两种：

- **个人账号**：邮箱与该账号的某个已验证邮箱一致即可。
- **组织账号**：必须用该组织在资料页上**公开的那个邮箱**（GitHub 文档里唯一被认可的组织归属途径）；
  组织没有 `users.noreply.github.com` 那种个人 noreply 地址。

原先 DeepSeek 用的是 `deepseek-ai@users.noreply.github.com`——那是**个人账号**的写法，
套在组织上反查不到任何东西，于是 2.1.0 前几次提交都没把它算进去（实测
`/graphs/contributors-data` 只有 gluztm / claude / codex 三人）。
查 `api.github.com/orgs/deepseek-ai` 拿到它的公开邮箱是 `service@deepseek.com`，
自本提交起尾注改用它。

已核实并会一直复核的三点：

1. Contributors 列表里显示的是**账号自身的名称**，不是尾注里写的文本——
   所以 ChatGPT 那条在列表里叫 **codex**，这与上表并不矛盾。
2. Claude 与 ChatGPT 一直是生效的（两个个人账号的邮箱都对得上）。
3. 历史提交已按既定约定保留原样，不做改写；DeepSeek 从本提交起计入。

本文件与 `README.md` 的「贡献者」表，是**不依赖 GitHub 统计口径**的确定性署名方式；
Contributors 列表只是额外的一层佐证。
