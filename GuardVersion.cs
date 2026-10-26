namespace DSHGuard;

/// <summary>
/// 守护壳自身版本号。
/// 规则（2026-09-13 起，用户定）：
///   **前两位是 release 号**——1.0 是第一个 release，1.1 是第二个；
///   同一个 release 里的小修改累加第三位（第 5 次修改 → 1.1.5）；
///   release 正式对外发布之后，下一轮从 1.2.x 重新计数。
///   
///   2.0.0（2026-09-30）：架构重构，Web 引擎与桌面版完全分离为两个独立入口，
///   插件/快照操作按目标选链路（pnpm for desktop, npx for web），
///   在 DSH 桌面版正式发布后进行的重大架构升级，故升主版本号到 2。
///   
///   Major = 主版本号（1 = 1.x 系列，2 = 2.x 系列）
///   Minor = release 序号（0 = x.0，1 = x.1，依此类推）
///   Patch = 本 release 内的修改序号（0 表示首个 release）
///
///   2.0.1（2026-10-01）：修复 2.0.0 混入的仓库坐标错误 ——
///   <see cref="RepoOwner"/> 曾被误改为 <c>JetLua</c>（该账号下无此仓库，接口回 404），
///   导致「检查更新」始终问不出来、发布页按钮打开一个不存在的地址。
///
///   2.2.0（2026-10-16）：安全审计修复批次 —— 6 项 High（安装包执行前校验完整性、外部命令
///   超时真正生效、卸载不再整目录强删、回滚期间不误裁快照、pnpm 包龄策略收口、终止引擎按
///   进程树结束并核实端口）与 Medium / Low 全部条目，外加两处原有缺陷的修复（桌面版官方
///   清单 path 折叠写法解析、install-desktop.ps1 缺 UTF-8 BOM）。
///
///   2.2.1（2026-10-17，批次 164）：插件页看图与分类栏重做、界面文字精简、若干显示与操作问题修复。
///   逐条见 CHANGELOG.md 的 [2.2.1]。
///
///   2.2.2（2026-10-19，批次 165）：以 2.2.1 的代码为对象做的一次全量审计之后的收口 —— 一处
///   命令注入面（引擎启动兜底分支缺护栏）、六处自检"假通过 / 名实不符 / 静默吞错"、三处
///   真实写盘风险（自检还原动作指向产品目录、失败图片永久拉黑、市场分页只增不减）、一处
///   恒真断言。不改界面与功能布局。逐条见 CHANGELOG.md 的 [2.2.2]。
///
///   2.2.3（2026-10-24，批次 166）：修「本体存在」假告警、桌面版 pnpm 工作区配置错误，以及插件
///   更新时半截安装树/文件锁造成的“更新失败、再次更新被锁、更新后版本不变”。更新前会恢复并复核
///   依赖树，更新后以安装目录、清单、锁文件和 pnpm 元数据的完整提交判成功；兼容 hoisted 布局。
///   同日复审收口三处：「一键更新」补齐同一套四方提交判据（原先漏传，半截提交会被记成成功）、
///   文件锁识别补 os error 5 / EPERM 与中文占用文案、清理半截目录前拒绝符号链接/junction。
///   终审收口：锁文件 specifier 与清单的一致性收口到全部 registry 声明（精确/^/~ 不再漏网），
///   更新与安装/重装共用同一谓词；^/~ 以安装目录实际版本满足声明，不再拿下限冒充目标；
///   锁提示单点化；EBUSY 独立匹配。
///   ZCode P1/P2 复核收口：按清单重装改走 manifest+lock+pnpm 元数据的完整提交判据，
///   修复 ^/~ 与比较器范围实际装到范围内高版本时的假失败；重装命令层与最终判定共用入口。
///   范围解析补齐 wildcard/缺省补丁/`~>`/hyphen/比较器/`||` 与 npm 预发布语义，
///   Unknown 仅可为 dist-tag 在锁文件事实成立时 rescue，明确 NotSatisfied 永不 rescue。
///   逐条见 CHANGELOG.md 的 [2.2.3]。
///
///   2.2.4（2026-10-24，批次 167）：2026-10-24 晚间现场（16:18 安装版）二轮收口。
///   桌面版卸载改回「先 pnpm remove 后清 bundles 登记」（旧顺序必然
///   ERR_PNPM_CANNOT_REMOVE_MISSING_DEPS，包一个都删不掉，还留半卸载残局），
///   成败判定对当前目标的 profile 核对（旧判定在桌面版上误读 Web 侧）；
///   桌面版 loader id 改从插件包自己的补丁文件读取——按包 package.json 的
///   dsh.bundle.patch 声明定位（越界拒绝，无声明退回根部 cordis.patch.yml），
///   多行组件全禁/全启、行级幂等、无补丁文件按包名兜底并落盘记录，
///   「禁用」不再恒弹"无法读取内部标识"；Web 端拿不到 id 仍拒写。
///   生态趋势带 @scope/ 前缀的条目补"去前缀短名精确"匹配档，点名字进内置插件市场页
///   而不是甩去外部网站；市场搜索对带前缀查询做短名兜底。逐条见 CHANGELOG.md 的 [2.2.4]。
///
///   2.2.5（2026-10-25，批次 168）：版本页可回滚 —— 桌面版（DSH Desktop）与守护壳两张卡各加
///   「选择版本」下拉，与插件页同一套交互（懒加载/(当前)(最新)[预发布] 标记/选中确认）：
///   桌面版列表来自官方 npm 版本表（@deepseek-ai/dsh，与桌面版同一版本线）逐个 HEAD 验证
///   官方源安装包在位，安装/回退走官方直链 + 数字签名校验；守护壳列表来自本程序 GitHub
///   发行版（带 Setup 附件的条目），下载后启动安装器。生态趋势条目去掉 @scope/ 前缀
///   只显示插件名（仅显示层，点击/简介匹配仍用全名）。逐条见 CHANGELOG.md 的 [2.2.5]。
/// </summary>
public static class GuardVersion
{
    /// <summary>主版本号：1 = 1.x 系列，2 = 2.x 系列。</summary>
    public const int Major = 2;

    /// <summary>release 序号：0 = x.0，1 = x.1，依此类推。</summary>
    public const int Minor = 2;

    /// <summary>本 release 内的修改序号：0 表示首个 release。</summary>
    /// <remarks>
    /// 2.0.0 是架构重构：目标分离（Web/桌面双轨化深化）+ pnpm 构造器 + PluginOps 分发层。
    /// 这是在 DSH 桌面版正式发布后进行的重大升级，影响所有插件写操作，故升主版本号。
    /// </remarks>
    public const int Patch = 5;

    /// <summary>交付批次计数：只做内部记账（日志/自检提示），不参与版本号。</summary>
    public const int Batch = 168;

    /// <summary>
    /// 本程序的代码仓库所有者（检测新版本时去这里看发行版）。
    ///
    /// ⚠ 这是**对外坐标**，写错不会报错、只会静默失效：2.0.0 曾误写成 <c>JetLua</c>，
    ///   于是「检查更新」查的是一个不存在的仓库（接口 404 ⇒ 判定 Unknown），
    ///   发布页按钮也指向不存在的地址，而整个自检全绿。
    ///   改动此值时**必须**同步改 <c>SelfTest</c> 里钉住仓库坐标的那组断言（会强制你确认）。
    /// </summary>
    public const string RepoOwner = "gluztm";

    /// <summary>本程序的代码仓库名称。</summary>
    public const string RepoName = "DSHGuard";

    /// <summary>格式化版本号字符串（对外显示）。2.0 起主版本号始终显示。</summary>
    public static string Version => VersionFor(Major, Minor, Patch);

    /// <summary>生成版本号字符串（纯函数，便于自检）。2.x 起始终显示三段。</summary>
    public static string VersionFor(int major, int minor, int patch)
    {
        if (major >= 2) return $"{major}.{minor}.{patch}";
        // 1.x 保持旧口径：patch=0 不显示第三段
        return patch == 0 ? $"1.{minor}" : $"1.{minor}.{patch}";
    }

    /// <summary>
    /// 显示用的完整版本信息（含内部批次号）：「2.0.0（批次 150）」。
    /// 自检断言、日志、设置页显示用它；对外发布的 Release 页不带批次。
    /// </summary>
    public static string Display => $"{Version}（批次 {Batch}）";

    /// <summary>
    /// 远程标签规范化：去掉可选的 <c>v</c> 前缀（同一个仓库上两种写法都可能有：<c>1.0</c> 与 <c>v1.0</c>），
    /// 再去掉首尾空白。只在 <c>v</c> 后面紧跟数字时才去掉 —— 否则 <c>version-2</c> 这类名字会被削成
    /// <c>ersion-2</c>（削坏了就再也比不出来，比不出来的后果是"拿不准"，见 <see cref="Judge"/>）。
    /// </summary>
    public static string NormalizeTag(string? tag)
    {
        string s = (tag ?? "").Trim();
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1])) s = s.Substring(1);
        return s.Trim();
    }

    /// <summary>
    /// 远程标签比本机版本新不新（纯函数，不查网，自检可直接断言）。
    ///
    /// 比较器是 <see cref="VersionInfo.Compare"/> —— 项目里既有的那一份 semver 比较（判据只有一处，
    /// 不在这里另写一套）。它**按数字段比较**：<c>1.10</c> 大于 <c>1.9</c>（字符串比会得出相反的结论），
    /// 且两侧都先过 <see cref="VersionInfo.IsComparableVersion"/>，认不成版本号的取值一律不可比。
    ///
    /// 注意（失败关门）：任一侧认不成版本号 ⇒ <see cref="GuardUpdateVerdict.Unknown"/>；
    ///   绝不落到"已是最新" —— 比不出来就报最新，正是本项目反复栽过的"谎报"。
    /// </summary>
    public static GuardUpdateVerdict Judge(string? remoteTag)
    {
        string local = Version;
        string remote = NormalizeTag(remoteTag);
        if (!VersionInfo.IsComparableVersion(local) || !VersionInfo.IsComparableVersion(remote))
            return GuardUpdateVerdict.Unknown;
        return VersionInfo.Compare(local, remote) < 0
            ? GuardUpdateVerdict.NewerAvailable
            : GuardUpdateVerdict.UpToDate;
    }
}

/// <summary>
/// 守护壳自身版本检测的结论（三态；<see cref="GuardVersion.Judge"/> 的产物，纯函数、便于自检）。
///
/// 为什么必须有 <see cref="Unknown"/> 这一档：远程问不出来（超时 / 断网 / 站点限流 / 仓库还没有发行版）
/// 与"远程就是当前这个版本"对用户的含义完全不同 —— 前者是"这次没问成，稍后再试"，
/// 后者是"你不用管"。把它们挤进同一个出口，就会出现"断网时显示已是最新"这种谎报。
/// </summary>
public enum GuardUpdateVerdict
{
    /// <summary>问不出来或比不出来（超时 / 断网 / 远程没有可比的版本号 / 报文读不懂）—— 不是"已是最新"。</summary>
    Unknown,
    /// <summary>远程不比本机新（含两边相同）。</summary>
    UpToDate,
    /// <summary>远程比本机新。</summary>
    NewerAvailable
}
