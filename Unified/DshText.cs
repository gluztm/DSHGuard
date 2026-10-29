using System;
using System.Collections.Generic;

namespace DSHGuard.Unified;

/// <summary>
/// 本功能区的界面文案表。
///
/// ── 为什么单独一个文件 ────────────────────────────────────────────────
/// 本功能区操作的是 DSH 的内部结构（cordis、patch、frontmatter、zstd…），
/// 相关术语默认面向开发者。界面文案需要把这些术语转写成可读的描述，
/// 例如「停用某个技能」而不是「给 SKILL.md 添加 disable-model-invocation」。
///
/// 因此所有面向用户的字符串集中定义在此，承担两项职责：
///   1) 术语转写：内部概念给出可读定义（见 <see cref="Explain"/>）；
///   2) 后果前置：破坏性操作先说明影响范围，再说明恢复方式。
///
/// 修改文案只改本文件，不要在界面代码里散写字面量 —— 散写会导致
/// 同一概念在不同位置使用不同措辞。
/// </summary>
public static class DshText
{
    // 分区名称与说明（悬停提示与空态引导共用）

    public const string SectionPlugin = "插件";
    public const string SectionMcp = "MCP";
    public const string SectionSkill = "Skills";
    public const string SectionPrompt = "提示词";
    public const string SectionSession = "会话";

    /// <summary>每个分区的一句话说明。</summary>
    public static string SectionHint(string sectionId) => sectionId switch
    {
        "plugin" => "DSH 内置的功能模块。开关决定该模块是否参与本次启动。",
        "mcp" => "MCP 服务为 DSH 提供外部能力（数据库、浏览器、自建服务等），其工具通过该通道注册给 DSH 调用。",
        "skill" => "供 DSH 读取的任务说明文件。DSH 命中对应任务时会加载并遵循其中的步骤。",
        "prompt" => "每次对话开始前注入的固定文本，用于约束 DSH 的行为与长期上下文。",
        "session" => "DSH 的历史会话记录。本页仅做索引与归档，查看内容请在 DSH 内打开。",
        _ => "",
    };

    // 术语转写

    /// <summary>把内部术语转写为可读定义。用于 ToolTip 与帮助文案。</summary>
    public static string Explain(string term) => term switch
    {
        "cordis" =>
            "DSH 的配置文件（cordis.patch.yml）。其中列出本次启动需要加载的模块；" +
            "未列出的模块不会被加载。本程序通过修改该文件实现开关，不需要手工编辑。",

        "frontmatter" =>
            "技能文件开头两个 --- 之间的元数据段，定义技能名称、触发条件，" +
            "以及是否对模型可见。",

        "disable-model-invocation" =>
            "控制模型是否可自主调用该技能。关闭后模型不可见，但仍可手工指定。",

        "user-invocable" =>
            "控制该技能是否出现在人工可调用的列表中。关闭后模型仍可能自动调用。",

        "serverName" =>
            "MCP 服务的标识名，DSH 以此区分不同服务。仅允许字母、数字、下划线与减号，最长 32 个字符。",

        "transport" =>
            "连接方式。stdio = 由 DSH 在本机启动一个进程并与之通信；" +
            "streamable-http = 连接一个已在运行的服务地址。",

        "zstd" =>
            "会话记录以 zstd 压缩存放，只能由 DSH 自行解压读取。" +
            "本程序不解析压缩格式，因此仅显示文件大小与时间。",

        "profile" =>
            "DSH 的运行配置集。Web 引擎与桌面版各自独立，互不影响。",

        "insert" =>
            "配置文件中的追加写法。本程序以追加方式新增 MCP 服务，不修改已有内容。",

        "kebab-case" =>
            "全小写、单词之间以减号连接，例如 my-skill-name。",

        _ => "",
    };

    // 空状态：无数据时给出下一步操作

    public static (string Title, string Action) Empty(string sectionId) => sectionId switch
    {
        "mcp" => (
            "尚未配置 MCP 服务。",
            "点击「＋ 添加 MCP 服务」按向导填写必要字段；不确定的项可以留空跳过。"),
        "skill" => (
            "未找到技能。",
            "将技能目录放入 DSH 的技能路径即可被识别；可点击「打开技能目录」确认路径位置。"),
        "prompt" => (
            "尚无提示词文件。",
            "点击「＋ 新建提示词」生成模板；也可以打开目录后手工放入文件。"),
        "session" => (
            "未找到会话记录。",
            "请确认 DSH 中已产生至少一次对话；若刚清空过，此处为空属正常。"),
        "plugin" => (
            "未读取到插件。",
            "通常表示当前环境的插件清单为空，并非错误。可点击「刷新」重新扫描。"),
        _ => ("无数据。", ""),
    };

    // 危险操作确认：先说明影响，再说明恢复方式

    public static (string Title, string Body) ConfirmDeleteMcp(string name, string profile)
        => ($"确认删除 MCP 服务「{name}」？",
            $"删除后 DSH 将无法连接到该服务。\n\n" +
            $"影响范围：仅「{profile}」配置集。\n" +
            $"恢复方式：修改前会自动生成备份，文件名以 .bak 结尾，改回原名即可还原。\n\n" +
            $"若只是暂时不使用，建议改为「停用」，可随时重新启用。");

    public static (string Title, string Body) ConfirmTrashSession(string shortId, string project)
    {
        string warn = DshRuntime.DestructiveWarning("会话");
        string body =
            $"会话：{shortId}（项目 {project}）\n\n" +
            $"该会话不会被删除，而是移动到本程序的回收目录：\n" +
            $"  {DshSessionManager.TrashRoot}\n\n" +
            $"恢复方式：将对应子目录移回 DSH 的 sessions 目录即可。";

        if (warn.Length > 0) body += "\n\n" + warn;
        return ("确认移除该会话记录？", body);
    }

    /// <summary>
    /// 提示词保存前的并发检查结果。
    /// DSH 运行时也会写 MEMORY.md，用户编辑期间它可能追加过内容；
    /// 直接覆盖会静默丢失这部分内容。因此保存前须比对磁盘现状。
    /// </summary>
    public static (string Title, string Body) WarnPromptOverwritten(string name, long diskSize, long editBaseSize)
        => ($"「{name}」在编辑期间已被其他进程修改",
            $"打开编辑器时文件为 {editBaseSize:N0} 字节，磁盘当前为 {diskSize:N0} 字节，" +
            $"来源通常是 DSH 自身的写入。\n\n" +
            $"此时保存会以当前编辑内容覆盖这部分新增内容。\n\n" +
            $"· 需要保留 DSH 写入的内容 → 点击「取消」，关闭编辑器后重新打开再修改；\n" +
            $"· 确认覆盖 → 点击「确定」。（保存前仍会生成一份 .bak 备份）");

    // 写操作的结果反馈

    /// <summary>需要重启才生效的改动使用该提示。</summary>
    public const string RestartNeeded = "已写入配置，重启 DSH 后生效。";

    /// <summary>无需重启的改动（例如提示词文件）使用该提示。</summary>
    public const string TakeEffectImmediately = "已保存，DSH 下次读取时生效。";

    /// <summary>失败时的兜底说明：告知原有配置未被改动。</summary>
    public const string FailureSafeNote = "原有配置未被修改。";

    // 表单字段标签与占位提示（新增 MCP 服务用）

    public static class Form
    {
        public const string McpTarget = "目标配置集";
        public const string McpTargetTip = "选择该服务写入的配置集；Web 引擎与桌面版各自独立。";

        public const string McpName = "服务名";
        public const string McpNamePlaceholder = "例如 github";
        public const string McpNameTip = "仅允许字母、数字、下划线与减号，不能包含空格与中文。DSH 以此区分不同服务。";

        public const string McpMode = "连接方式";
        public const string McpModeTip = "本地进程选择「本机启动」，已有服务地址选择「连接网址」。";

        public const string McpCommand = "启动命令";
        public const string McpCommandPlaceholder = "例如 npx -y @modelcontextprotocol/server-github";
        public const string McpCommandTip = "按该服务文档给出的启动命令原样填写，参数以空格分隔。";

        public const string McpUrl = "服务地址";
        public const string McpUrlPlaceholder = "例如 http://127.0.0.1:8765/mcp";
        public const string McpUrlTip = "该服务的 MCP 端点地址，通常以 /mcp 结尾。";

        public const string McpPreset = "预置模板";
        public const string McpPresetTip = "选择预置项可自动填充启动命令与参数。";
    }

    // MCP 服务预置模板

    /// <summary>一条预置模板。<see cref="Args"/> 为空表示 http 型。</summary>
    public sealed record McpPreset(
        string Display,
        string ServerName,
        string Transport,
        string Command,
        string[] Args,
        string Url,
        string Note);

    /// <summary>
    /// 预置清单。选中后自动填充启动命令，无需查阅文档。
    ///
    /// ⚠ 收录门槛（评审 P0-1 的教训）：
    ///   只收**在 npm 上真实存在且未废弃**的包。`@modelcontextprotocol/server-fetch`
    ///   并不存在（registry 404）、`server-github` 已被官方标记 deprecated ——
    ///   这两类收进来会让用户「照着点一次」必然失败，且自检查不出来（离线时无从校验）。
    ///   <see cref="DeprecatedArgs"/> 里留了一份已知废弃包名黑名单，CM-19 会静态比对。
    /// </summary>
    public static IReadOnlyList<McpPreset> Presets { get; } = new List<McpPreset>
    {
        new("文件系统", "filesystem", "stdio",
            "npx", new[] { "-y", "@modelcontextprotocol/server-filesystem", "." }, "",
            "为 DSH 提供指定目录的读写权限。最后一个参数为目录路径，按需改为本机路径。"),

        new("本地记忆", "memory", "stdio",
            "npx", new[] { "-y", "@modelcontextprotocol/server-memory" }, "",
            "跨会话保存事实数据。数据保存在本机，不上传。"),

        new("我自己的服务", "my-service", "streamable-http",
            "", Array.Empty<string>(), "http://127.0.0.1:8765/mcp",
            "连接已在本机或局域网运行的 MCP 服务，填写地址即可。"),
    };

    /// <summary>
    /// 已知不可用的包名（不存在 / 官方废弃）。CM-19 用它做静态校验，
    /// 保证「预置里的命令一定指向能装上的包」。离线也能跑。
    /// </summary>
    public static IReadOnlyList<string> DeprecatedArgs { get; } = new[]
    {
        "@modelcontextprotocol/server-fetch",     // registry 404，包不存在
        "@modelcontextprotocol/server-github",    // 官方 deprecated（2025.4.8 起不再支持）
    };
}
