# DSH 守护壳 · 历代发布说明

这一页把历代版本的发布说明并排放在一起：一行一版，从新到旧，每行一句话说清这一版改了什么。

- **逐条内容**在 [CHANGELOG.md](CHANGELOG.md)：那里的版本段落沿用旧体例（1.4、1.5 等短名），本页按发布号写全（1.4.0、1.5.0）。
- **发布说明正本**在 GitHub Release 页面：点版本号即到该版 Release。
- 本页只做一览，不重复正文；发布后新版本只往表头加一行。

| 版本 | 日期 | 这一版改了什么 |
| --- | --- | --- |
| [2.3.2](https://github.com/gluztm/DSHGuard/releases/tag/2.3.2) | 2026-11-04 | 2.3.1 的补丁位：修复交付链的可复现性（产物带上提交短 hash、中文语言包与 SDK 钉进仓库、自检进打包门禁），并补齐一条一直没被真正验证过的自检断言。 |
| [2.3.1](https://github.com/gluztm/DSHGuard/releases/tag/2.3.1) | 2026-10-29 | 2.3.0 的补丁位：同一个 release 内的一次功能性增补，不改 2.3.0 的任何既有判据 —— GitHub 访问开关、插件市场按最近收录排序、桌面版若干界面修复。 |
| [2.3.0](https://github.com/gluztm/DSHGuard/releases/tag/2.3.0) | 2026-10-26 | 综合管理功能区落地：把 DSH 自己的资源（profile / patch / skills / prompts / sessions）收进一屏，从只读展示升级为可增删改启停的真管理。 |
| [2.2.5](https://github.com/gluztm/DSHGuard/releases/tag/2.2.5) | 2026-10-26 | 版本页可回滚 + 生态趋势条目显示精简。 |
| [2.2.4](https://github.com/gluztm/DSHGuard/releases/tag/2.2.4) | 2026-10-25 | 2.2.3 当晚现场二轮收口：桌面版卸载与禁用的三处结构性缺陷、生态趋势页跳转。 |
| [2.2.3](https://github.com/gluztm/DSHGuard/releases/tag/2.2.3) | 2026-10-24 | 桌面版用户启动、插件更新与 pnpm 安装树故障修复。 |
| [2.2.2](https://github.com/gluztm/DSHGuard/releases/tag/2.2.2) | 2026-10-19 | 提高安全性。 |
| [2.2.1](https://github.com/gluztm/DSHGuard/releases/tag/2.2.1) | 2026-10-17 | 插件页的看图和分类栏重做，界面文字精简，并修了几处显示和操作上的问题（自检 906 项全部通过）。 |
| [2.2.0](https://github.com/gluztm/DSHGuard/releases/tag/2.2.0) | 2026-10-16 | 一次大型审计之后的集中「健康行动」：以 2.1.0 的代码为对象做全量体检。 |
| [2.1.0](https://github.com/gluztm/DSHGuard/releases/tag/2.1.0) | 2026-10-04 | 设置 → 版本在桌面版下换掉 Web 引擎的两张卡，改为桌面版自己的版本 / 安装目录 / 日志目录 / 运行状态。 |
| [2.0.1](https://github.com/gluztm/DSHGuard/releases/tag/2.0.1) | 2026-10-01 | 修「检查更新」始终查不到新版本（仓库属主写错、接口 404）、下载页按钮指向不存在的网址、文件属性版本号与程序内不一致。 |
| [2.0.0](https://github.com/gluztm/DSHGuard/releases/tag/2.0.0) | 2026-09-30 | 左上角新增全局切换开关（Web / 桌面），插件页与快照页一起切，桌面版也能用「一键更新」。 |
| [1.5.0](https://github.com/gluztm/DSHGuard/tree/1.5.0) | 2026-09-29 | 本程序同时接管 DSH 桌面版：插件目录与配置和网页引擎分开，两边都能看、能存档回滚，桌面版也能全面管理插件。 |
| [1.4.1](https://github.com/gluztm/DSHGuard/releases/tag/1.4.1) | 2026-09-28 | 趋势页新增第四张榜「总计下载」（历史累计量，标注累计截止日），星标榜统一用 k 作单位，每行高度统一。 |
| [1.4.0](https://github.com/gluztm/DSHGuard/releases/tag/1.4.0) | 2026-09-28 | 左侧导航新增「趋势」一屏（涨星最快 / 本周下载 / 星标总数），数据来自 dsh.so，每小时最多取一次且切到该屏才联网。 |
| [1.3.8](https://github.com/gluztm/DSHGuard/releases/tag/1.3.8) | 2026-09-28 | 记录自己被打开的次数（第 10 次问要不要点 Star，之后每满 100 次再问），版本卡新增「发布页面」按钮，看图大图每次以整图适应窗口居中显示。 |
| [1.3.7](https://github.com/gluztm/DSHGuard/releases/tag/1.3.7) | 2026-09-25 | 守护壳自身的版本自动检查 + 查到新版本弹窗问要不要升级，并修「立即更新」点了没反应。 |
| [1.3.6](https://github.com/gluztm/DSHGuard/releases/tag/1.3.6) | 2026-09-23 | 看图界面版面调整：去滚动条、去深色底衬、铺满显示区，翻页箭头加大并加悬停提示。 |
| [1.3.5](https://github.com/gluztm/DSHGuard/releases/tag/1.3.5) | 2026-09-22 | 插件卡片创建时间移入悬停提示，修正插件兼容性判定偏宽（确切版本号按精确匹配）。 |
| [1.3.4](https://github.com/gluztm/DSHGuard/releases/tag/1.3.4) | 2026-09-22 | 更新进度窗新增「发布页面」按钮，超一分钟提示网络不佳建议手动下载，图片缓存加 256MB 上限。 |
| [1.3.3](https://github.com/gluztm/DSHGuard/releases/tag/1.3.3) | 2026-09-22 | 插件市场分类标签改一行显示（放不下收进「更多」弹可滚动下拉），分类栏随滚动自动收起。 |
| [1.3.2](https://github.com/gluztm/DSHGuard/releases/tag/1.3.2) | 2026-09-22 | 看图界面去深色底与描边，放大图以鼠标位置为锚点缩放，支持按住拖动查看局部。 |
| [1.3.1](https://github.com/gluztm/DSHGuard/releases/tag/1.3.1) | 2026-09-22 | 修应用内更新可能长时间停在某百分比不动（加总时长上限、超时如实说明并退下载页），取消信号覆盖更新全过程，进度窗不再强制置顶。 |
| [1.3.0](https://github.com/gluztm/DSHGuard/releases/tag/1.3.0) | 2026-09-21 | 看图界面支持 Ctrl + 滚轮缩放，翻页改为左右两侧热区，点图片中间打开仓库页。 |
| [1.2.3](https://github.com/gluztm/DSHGuard/releases/tag/1.2.3) | 2026-09-21 | 本地插件页「筛选」改「排序」（安装时间 / 创建日期 / 更新日期 / 兼容性），新增两项本地记账，插件卡片新增两处时间。 |
| [1.2.2](https://github.com/gluztm/DSHGuard/releases/tag/1.2.2) | 2026-09-20 | 修应用内更新线路表把显示名当请求地址（四条线路瞬间全败），下载短暂间歇不再当场失败，依次尝试多条线路，本地写入失败不再换线路重试。 |
| [1.2.1](https://github.com/gluztm/DSHGuard/releases/tag/1.2.1) | 2026-09-20 | 修下载连接中途被破坏，以及部分网络能连上却取不到数据时改为依次尝试多条线路。 |
| [1.2.0](https://github.com/gluztm/DSHGuard/releases/tag/1.2.0) | 2026-09-20 | 插件变动期间退出增加确认（说明中断后果），更新提前结束时如实说明原因。 |
| [1.1.0](https://github.com/gluztm/DSHGuard/releases/tag/1.1.0) | 2026-09-20 | 应用内更新（设置 → 版本新增「守护壳版本」卡，可查可下载可安装）+ 断电级安全冗余（保留已校验安装包 / 上一版副本 / 恢复脚本），卸载结论改四态如实报告。 |
| [1.0.0](https://github.com/gluztm/DSHGuard/releases/tag/1.0.0) | 2026-09-20 | 首个公开发布版。 |

> 1.5.0 那一版没有单独的 Release 页面，版本号链到对应的代码标签。
