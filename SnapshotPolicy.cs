namespace DSHGuard;

/// <summary>
/// 改动类动作与"是否先存快照"的策略表。
/// 只在会真正改动**插件依赖**的动作前存快照：
///   更新插件 ✓、卸载插件 ✓
///   安装插件 ✗、禁用插件 ✗、启用插件 ✗
///   升级 DSH ✗、回滚 DSH ✗ —— 自 2026-09-18 用户指令起关停「自动-版本」预存：
///     换版本就一条启动命令的事；回退 DSH 版本的功能本身不依赖它
///     （SnapshotManager.Restore 有自己的 pre-restore 素材 + 用户勾选）。
///     历史遗留的 before-switch 快照仍可读、可回滚，只是不再新增。
/// 调用方一律先问 <see cref="NeedSnapshot"/>，避免各处各写一套判断。
/// </summary>
internal enum GuardAction
{
    InstallPlugin,
    UpdatePlugin,
    UninstallPlugin,
    DisablePlugin,
    EnablePlugin,
    UpgradeDsh,
    RollbackDsh
}

internal static class SnapshotPolicy
{
    /// <summary>该动作执行前是否需要先保存快照。</summary>
    internal static bool NeedSnapshot(GuardAction action) => action switch
    {
        GuardAction.UpdatePlugin => true,
        GuardAction.UninstallPlugin => true,
        _ => false          // 安装、停用、启用、升级/回滚 DSH：都不存
    };

    /// <summary>
    /// 动作 ⇒ 快照类型。
    /// 插件动作记「自动-插件」（auto）；升级/回滚 DSH 原先在这里单独记 before-switch（「自动-版本」），
    /// 该档自 2026-09-18 用户指令起关停 ⇒ 那两个分支已删除，现在**任何动作都归 auto**。
    /// 参数 <paramref name="action"/> 保留不删：全部调用点都写成 KindFor(动作)，形状不必跟着改，
    /// 将来若要重新分档也不必回头改调用方。
    /// before-switch 常量与标签仍然保留，专供**历史遗留**快照显示与清理（不删老档，只停新增）。
    /// </summary>
    internal static string KindFor(GuardAction action)
    {
        _ = action;                 // 不再按动作分档；显式吃掉参数，避免"未使用参数"的告警
        return SnapshotManager.KindAuto;
    }
}
