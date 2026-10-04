# 贡献者

本项目由以下人员与 AI 协作完成。感谢每一方的投入。

| 贡献者 | 参与内容 |
| --- | --- |
| [gluztm](https://github.com/gluztm) | 项目发起人与维护者 |
| DeepSeek | 1.0 版本总体架构设计与程序基础实现 |
| Claude | 2.0 起接手的顶层设计，功能区重构与新功能开发 |
| ChatGPT | 辅助开发与审计工作 |

---

## 关于 GitHub 的 Contributors 列表

GitHub 仓库页右侧的 **Contributors** 是按 **提交作者 / 共同作者的邮箱** 自动统计的，
它只认得「与某个 GitHub 账号关联的邮箱」。本项目历史提交的作者统一是
`DSHGuard <dshguard@users.noreply.github.com>`（仓库自身的标识邮箱，不对应任何 GitHub 账号），
因此那三个 AI 协作者**不会**出现在自动生成的 Contributors 里 —— 这不是漏写，是那套统计口径决定的。

要让它们出现在 Contributors 列表中，只有两条路：

1. **提供各协作者对应的 GitHub 账号邮箱**，之后的新提交加上
   `Co-authored-by: 名字 <邮箱>` 尾注（GitHub 会把共同作者一并计入）；
2. **改写历史提交的作者信息**（`git filter-repo` 之类）。

本项目遵循「不改写已推送历史」的既定约定（历史提交已被 tag 与 Release 钉住），
因此不采用第 2 条。第 1 条随时可以做 —— 把邮箱给过来即可补上。

本文件与 `README.md` 的「贡献者」表，是目前**确定性可见**的署名方式。
