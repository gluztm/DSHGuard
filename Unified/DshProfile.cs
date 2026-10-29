using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSHGuard.Unified;

/// <summary>
/// 综合管理功能区的**目标维度**：DSH 的 profile。
///
/// ── 为什么是 profile，不是「AI 编码工具」────────────────────────────────
/// 上一版（2026-10-26 首版）照搬了 CC Switch 的「13 个第三方 AI 编码工具」矩阵
/// （Claude Code / Codex / Gemini …），目标路径全指向 ~/.claude.json、~/.codex/…
/// 那些**DSH 之外的别人的配置**。那是个方向性错误：
///   · DSHGuard 是 DSH 的专用管理器，它的职责是管 DSH 自己
///   · 用户装了 DSHGuard 不是为了顺便管 Claude Code / Codex
///   · DSH 的多目标维度本来就是 profile（web / desktop / headless），
///     而不是「接了多少个外部工具」
///
/// 现版本（2.3.0 重做）把矩阵的「列」换成 DSH profile：
/// 一行 = 一个资源（技能 / 插件 / 提示词片段），一列 = 一个 profile，
/// 勾选 = 该资源在这个 profile 里生效。
///
/// ── 与既有代码的关系 ────────────────────────────────────────────────────
/// profile 的寻址**完全复用** <see cref="GuardPaths"/>：
///   · <c>GuardPaths.ProfileDirFor(target)</c> 给出 profile 目录
///   · <c>GuardPaths.PatchFileFor(target)</c> 给出该 profile 的 cordis.patch.yml
/// 绝不在这里自己拼 ~/.dsh/... 路径 —— 那是上一版犯的第二个错
/// （自己用 Environment.SpecialFolder.UserProfile 拼路径，绕开了全仓唯一可改根目录的控制点）。
/// </summary>
public enum DshProfileKind
{
    /// <summary>Web 引擎 profile（npx dsh web）—— 主目标。</summary>
    Web,
    /// <summary>DSH 桌面版 profile。</summary>
    Desktop,
    /// <summary>无头 profile（CI / 脚本调用）。</summary>
    Headless,
}

/// <summary>
/// 一个 profile 的描述。
///
/// <see cref="PatchFile"/> 与 <see cref="ProfileDir"/> 是**懒求值**的委托，
/// 因为 GuardPaths 的根目录可被用户配置覆盖（GuardPaths.Apply），
/// 静态构造时机取到的是默认值而非实际值。
/// </summary>
public sealed record DshProfileInfo(
    DshProfileKind Kind,
    string Id,                       // 稳定标识：写存档、写日志用，永不本地化
    string DisplayName,              // 界面上给人看的名字
    string Glyph,                    // Segoe MDL2 Assets 字符
    Func<string> ProfileDir,         // 走 GuardPaths
    Func<string> PatchFile,          // 走 GuardPaths
    bool Editable)                   // false = 该 profile 不在本程序管理范围（如 node_modules）
{
    /// <summary>该 profile 是否存在（目录存在且含 cordis 配置）。</summary>
    public bool Exists
    {
        get
        {
            try { return Directory.Exists(ProfileDir()); }
            catch { return false; }
        }
    }
}

/// <summary>
/// DSH profile 清单的唯一注册表。
///
/// ⚠ 新增 profile 时要同步的两处（本文件之外）：
///   1) <c>Projector</c> 里该 profile 的投影能力（技能目录 / 提示词文件如何写到它下面）
///   2) <c>SelfTest.Unified.cs</c> 的 UT-PROF-01（会红，强制你确认）
/// 矩阵列由本注册表自动生成，**不需要手改 XAML**。
/// </summary>
public static class DshProfileRegistry
{
    /// <summary>
    /// 全部 profile，按界面列顺序排列（web 在前，它是主目标）。
    ///
    /// 注意：这里**只列本程序确实能投影的 profile**。
    /// 上一版把 node_modules 也当成一个 profile 列出来，是纯粹的错误。
    ///
    /// ⚠ 另一条更硬的红线（2026-10-28 用户反馈）：**只列 DSH 官方自带、
    /// 每台机器上都存在的 profile**。曾经这里有一个 `mc-companion`（MC 伴生），
    /// 那是某台机器上自己建的 profile，换台机器就空着，分组头还在、内容全空，
    /// 属于「本机特有」被写死进了产品。要加 profile，先确认它是 DSH 的标准形态。
    /// </summary>
    public static IReadOnlyList<DshProfileInfo> Registry { get; } = Build();

    private static List<DshProfileInfo> Build()
    {
        return new List<DshProfileInfo>
        {
            new(DshProfileKind.Web, "web", "Web 引擎", "\uE774",
                ProfileDir: () => GuardPaths.ProfileDirFor(GuardTarget.Web),
                PatchFile: () => GuardPaths.PatchFileFor(GuardTarget.Web),
                Editable: true),

            new(DshProfileKind.Desktop, "desktop", "桌面版", "\uE7F4",
                ProfileDir: () => GuardPaths.ProfileDirFor(GuardTarget.Desktop),
                PatchFile: () => GuardPaths.PatchFileFor(GuardTarget.Desktop),
                Editable: true),

            new(DshProfileKind.Headless, "headless", "无头", "\uE756",
                ProfileDir: () => Path.Combine(GuardPaths.DshHome, "profiles", "headless"),
                PatchFile: () => Path.Combine(GuardPaths.DshHome, "profiles", "headless", "cordis.patch.yml"),
                Editable: true),
        };
    }

    /// <summary>按稳定标识查。</summary>
    public static DshProfileInfo? ById(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return Registry.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按枚举查。</summary>
    public static DshProfileInfo Get(DshProfileKind kind) =>
        Registry.First(p => p.Kind == kind);

    /// <summary>当前实际存在的 profile（矩阵只显示这些列）。</summary>
    public static IEnumerable<DshProfileInfo> Existing() => Registry.Where(p => p.Exists);

    /// <summary>可编辑的 profile（投影只往这些写）。</summary>
    public static IEnumerable<DshProfileInfo> Editable() => Registry.Where(p => p.Editable);
}

/// <summary>
/// DSH 侧资源的种类。**这是 DSH 真实有的东西**，不是 CC Switch 的那四个域。
///
/// 依据（2026-10-27 实测本机 ~/.dsh）：
///   · 插件树：cordis.patch.yml（144 行，YAML 层级结构，禁用靠 disabled: true）
///   · 技能：~/.dsh/redteam/skills/*.md（23 条），以及各 bundle 自带的 skills
///   · 提示词片段：~/.dsh/prompt-inject.md、~/.dsh/MEMORY.md
///   · 会话：~/.dsh/sessions/<项目>/<uuid>/session.v4.jsonl.zstd（65 个，zstd 压缩）
///   · Agent 预设：~/.dsh/.agent-presets/（presets.json + redteam/）
///
/// ⚠ 关于 MCP 的更正（2.3.0 修订）：DSH **有** MCP，走官方插件包
///   `@deepseek-ai/dsh-mcp-client`。每台服务器是 cordis.patch.yml 里的一条
///   `- id: mcp-*` / `name: '@deepseek-ai/dsh-mcp-client'` / `config: {serverName, transport, ...}`，
///   工具名暴露为 `mcp__{serverName}__{tool}`。形态**不是** Claude Code / Codex 的
///   `mcpServers` JSON —— 早期版本照搬那套字段是错的，但「DSH 没有 MCP」这个结论同样错。
///   证据：profiles/desktop/node_modules/@deepseek-ai/dsh-mcp-client/README.zh.md。
/// </summary>
public enum DshResourceKind
{
    /// <summary>插件树条目（cordis.patch.yml 里的 id/name/disabled）。</summary>
    Plugin,
    /// <summary>MCP 服务器（cordis.patch.yml 里 name=@deepseek-ai/dsh-mcp-client 的条目）。</summary>
    Mcp,
    /// <summary>技能（skills 目录下的 .md）。</summary>
    Skill,
    /// <summary>提示词片段（prompt-inject.md / MEMORY.md）。</summary>
    Prompt,
    /// <summary>会话记录。</summary>
    Session,
}
