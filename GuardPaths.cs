using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DSHGuard;

/// <summary>
/// 可配置路径的单一来源（设置页「路径」页可改；空值 = 使用默认探测位置）。
/// Logger / SnapshotManager / PluginManager 均从此处取路径，避免硬编码。
/// </summary>
public static class GuardPaths
{
    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string DshHome => Path.Combine(UserProfile, ".dsh");

    /// <summary>程序目录（exe 所在，固定不可改）。</summary>
    public static string ExeDir => AppContext.BaseDirectory;

    public static string DefaultLogDir => Path.Combine(AppContext.BaseDirectory, "Logs");
    /// <summary>快照仓库：程序目录下 Snapshots（完全自建，不读任何第三方插件目录）。</summary>
    public static string DefaultSnapshotRoot => Path.Combine(AppContext.BaseDirectory, "Snapshots");
    /// <summary>第三方 undo 插件的历史仓库位置：仅用于识别"设置里指向了它"这一情况，程序不读取其中内容。</summary>
    public static string ThirdPartySnapshotRoot => Path.Combine(DshHome, "undo-snapshots");
    public static string DefaultProfileDir => Path.Combine(DshHome, "profiles", "web");

    // ══════════════ 主目录下的 Config / Cache ══════════════
    // · 配置集中于 Config 目录；运行缓存集中于 Cache 目录并按用途分级。
    // · 主目录下文件夹名首字母大写（Logs / Tools / Config / Cache / Assets）。
    // · 旧位置 %APPDATA%\DSHGuard 仅在首次启动时迁移一次（MigrateLegacyConfig）。
    // · 旧的小写文件夹名由 MigrateFolderCasing 就地改名。

    /// <summary>自检/测试用的整体改根（<c>DSHGUARD_DATA_DIR</c>）：config 与 cache 都落到它下面。</summary>
    private static string? Override
    {
        get
        {
            try
            {
                string? over = Environment.GetEnvironmentVariable("DSHGUARD_DATA_DIR");
                return string.IsNullOrWhiteSpace(over) ? null : over!.Trim();
            }
            catch { return null; }
        }
    }

    /// <summary>配置目录（主目录\Config）：settings.json、versions.json。</summary>
    public static string ConfigDir =>
        Override != null ? Path.Combine(Override, "Config") : Path.Combine(ExeDir, "Config");

    /// <summary>插件本地记账（订阅时间 / 更新时间的包名→时间两列）：与 settings.json 同目录，可随时删除重建。</summary>
    public static string PluginTimesFile => Path.Combine(ConfigDir, "plugin-times.json");

    /// <summary>缓存根目录（主目录\Cache），下面按用途分文件夹。</summary>
    public static string CacheDir =>
        Override != null ? Path.Combine(Override, "Cache") : Path.Combine(ExeDir, "Cache");

    /// <summary>插件目录缓存（收录清单 + 版本校验信息）。</summary>
    public static string CacheDirMarket => Path.Combine(CacheDir, "Market");

    /// <summary>截图缓存。</summary>
    public static string CacheDirImages => Path.Combine(CacheDir, "Images");

    /// <summary>
    /// 将主目录下的小写文件夹改名为首字母大写（logs→Logs、tools→Tools、config→Config、cache→Cache）。
    /// 必须在 <c>Logger.Init()</c> 之前调用：一旦日志目录被创建或占用，改名会因文件占用失败。
    /// 返回已改名的文件夹列表（未改名返回 null）。自检模式（存在 Override）不执行。
    /// </summary>
    public static string? MigrateFolderCasing()
    {
        try
        {
            if (Override != null) return null;
            var moved = new List<string>();
            foreach (var (oldName, newName) in new[]
                     {
                         ("logs", "Logs"), ("tools", "Tools"), ("config", "Config"), ("cache", "Cache")
                     })
            {
                string oldDir = Path.Combine(ExeDir, oldName);
                if (!Directory.Exists(oldDir)) continue;

                // 大小写不敏感：Directory.Exists 对仅大小写不同的路径同样返回 true，需读取 DirectoryInfo.Name 获取磁盘上的真实大小写。
                string actual = new DirectoryInfo(oldDir).Name;
                // 仅大小写不同 ⇒ 在 Windows 上本来就是同一个目录，改名纯属多余；
                // 而且日志文件正被自己占用，改名必然 "Access denied"（现场每次启动都报一次）。
                // 因此这里按"忽略大小写"判定：相同即视为已完成，直接跳过。
                if (string.Equals(actual, newName, StringComparison.OrdinalIgnoreCase)) continue;

                string newDir = Path.Combine(ExeDir, newName);
                string tmpDir = Path.Combine(ExeDir, oldName + ".case-tmp");
                try
                {
                    // 仅改大小写的改名会被系统拒绝（源与目标为同一目录），需经临时名分两步完成。
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                    Directory.Move(oldDir, tmpDir);
                    Directory.Move(tmpDir, newDir);
                    moved.Add($"{actual}→{newName}");
                }
                catch (Exception ex)
                {
                    Logger.LogError($"MigrateFolderCasing({oldName})", ex);
                    try { if (Directory.Exists(tmpDir) && !Directory.Exists(oldDir)) Directory.Move(tmpDir, oldDir); }
                    catch { }
                }
            }
            return moved.Count > 0 ? string.Join("、", moved) : null;
        }
        catch (Exception ex) { Logger.LogError("MigrateFolderCasing", ex); return null; }
    }

    /// <summary>旧位置（%APPDATA%\DSHGuard），仅用于一次性迁移。</summary>
    public static string LegacyDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHGuard");

    /// <summary>
    /// 首次启动时将旧位置的配置文件迁移到 Config（仅一次；目标已存在则跳过）。
    /// 旧位置的缓存直接删除（可重新生成）；旧位置的配置保留为 .moved-backup 备份。
    /// 返回迁移的文件名列表（未迁移返回 null）。自检模式（存在 Override）不迁移。
    /// </summary>
    public static string? MigrateLegacyConfig()
    {
        try
        {
            if (Override != null) return null;
            if (!Directory.Exists(LegacyDataDir)) return null;
            if (Path.GetFullPath(LegacyDataDir).Equals(Path.GetFullPath(ConfigDir), StringComparison.OrdinalIgnoreCase))
                return null;

            Directory.CreateDirectory(ConfigDir);
            var moved = new List<string>();
            foreach (string name in new[] { "settings.json", "versions.json" })
            {
                string dst = Path.Combine(ConfigDir, name);
                string src = Path.Combine(LegacyDataDir, name);
                if (File.Exists(dst) || !File.Exists(src)) continue;
                File.Copy(src, dst, true);
                moved.Add(name);
                try { File.Move(src, src + ".moved-backup", true); } catch { }
            }

            // 删除旧缓存（插件目录缓存 + 截图缓存），新缓存写入 Cache 目录
            foreach (string name in new[] { "catalog.json", "catalog-meta.json" })
            {
                try
                {
                    string f = Path.Combine(LegacyDataDir, name);
                    if (File.Exists(f)) File.Delete(f);
                }
                catch { }
            }
            try
            {
                string oldImg = Path.Combine(LegacyDataDir, "imgcache");
                if (Directory.Exists(oldImg)) Directory.Delete(oldImg, true);
            }
            catch { }

            Logger.Log($"配置迁移完成：{string.Join("、", moved)} → {ConfigDir}");
            return moved.Count > 0 ? string.Join("、", moved) : null;
        }
        catch (Exception ex) { Logger.LogError("MigrateLegacyConfig", ex); return null; }
    }

    /// <summary>预创建 Cache 下的分类子目录。</summary>
    public static void EnsureCacheDirs()
    {
        foreach (string dir in new[] { CacheDirMarket, CacheDirImages })
        {
            try { Directory.CreateDirectory(dir); } catch { }
        }
    }

    /// <summary>缓存目录当前占用（字节）。</summary>
    public static long CacheBytes()
    {
        try
        {
            if (!Directory.Exists(CacheDir)) return 0;
            long sum = 0;
            foreach (string f in Directory.GetFiles(CacheDir, "*", SearchOption.AllDirectories))
            {
                try { sum += new FileInfo(f).Length; } catch { }
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>清空缓存（保留 Cache 目录本身与分类子目录），返回释放的字节数。</summary>
    public static long ClearCache()
    {
        long before = CacheBytes();
        try
        {
            foreach (string sub in new[] { CacheDirMarket, CacheDirImages })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (string f in Directory.GetFiles(sub, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (string d in Directory.GetDirectories(sub))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
            // Cache 根目录下可能存在的其它文件一并删除
            if (Directory.Exists(CacheDir))
                foreach (string f in Directory.GetFiles(CacheDir))
                {
                    try { File.Delete(f); } catch { }
                }
        }
        catch (Exception ex) { Logger.LogError("ClearCache", ex); }
        return Math.Max(0, before - CacheBytes());
    }

    /// <summary>将字节数格式化为可读单位（如 1.2 MB / 340 KB）。</summary>
    public static string HumanSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";
        if (bytes >= 1024L * 1024) return $"{bytes / 1024.0 / 1024:0.#} MB";
        if (bytes >= 1024) return $"{bytes / 1024.0:0.#} KB";
        return bytes + " B";
    }

    /// <summary>
    /// 程序主目录的目录契约：一个文件夹只干一件事，名字首字母大写。
    /// 程序读写的所有落盘数据都必须落在这里面的某一个目录里。
    /// </summary>
    public static IReadOnlyList<(string Name, string Purpose)> LayoutContract => new[]
    {
        ("Config", "设置与版本记忆"),
        ("Cache", "插件目录与市场图片缓存（可随时清空）"),
        ("Logs", "异常日志（只在真出错时创建，平时是空的）"),
        ("Snapshots", "本程序保存的配置快照"),
        ("Tools", "外部脚本：清理日志 / 端口检查 / 插件更新检查 / 运行环境安装"),
    };

    /// <summary>
    /// 启动时把目录建好（幂等）：返回本次确认就绪的目录清单。
    /// 目的：让"文件应存放于何处"有唯一答案，不依赖任何功能首次运行时的顺带创建。
    /// </summary>
    public static List<string> EnsureLayout()
    {
        var ready = new List<string>();
        void Make(string dir, string name)
        {
            try
            {
                Directory.CreateDirectory(dir);
                ready.Add(name);
            }
            catch (Exception ex) { Logger.LogError("EnsureLayout:" + name, ex); }
        }

        Make(ConfigDir, "Config");
        Make(CacheDir, "Cache");
        Make(CacheDirMarket, "Cache\\Market");
        Make(CacheDirImages, "Cache\\Images");
        Make(LogDir, "Logs");
        Make(SnapshotRoot, "Snapshots");
        return ready;
    }

    /// <summary>
    /// 「日志」页 / 「导出诊断」/ 设置页「路径」认定并展示的**用户真实日志目录**（默认 &lt;exe&gt;\Logs）。
    /// ⚠ 语义固定不变：它永远指向用户看得见的那个目录，不受"本次运行把日志临时写到哪儿"影响。
    /// 测试夹具（<c>--selftest</c> 等）的临时落点走 <see cref="Logger.UseLogDirForRun"/>，
    /// 那只改 Logger 落盘的落点，**不改这里** —— 否则自检会把日志页的诊断包指向临时目录。
    /// </summary>
    public static string LogDir { get; private set; } = DefaultLogDir;

    /// <summary>
    /// 被 <see cref="Apply"/> **显式指定**过的日志目录；从未显式指定过时为空串。
    ///
    /// 与 <see cref="LogDir"/> 的区别：<c>LogDir</c> 是"最终值"（显式值或默认值，分不出哪个），
    /// 而本属性只表达"有没有人显式点名要某个目录"。Logger 据此实现
    /// 「显式设置 &gt; 运行期覆盖 &gt; 默认」的优先级 —— 否则自检那条
    /// 「日志保留策略」用例（临时 <c>Apply(testLogDir, …)</c> 后调 <c>CleanupOldLogs()</c>）
    /// 会被夹具的临时目录覆盖顶掉，导致清理的目录与断言的目录不是同一个。
    /// ⚠ 传空/空白值**不算显式指定**（那是"回落默认值"的意思，仍可被运行期覆盖接住）。
    /// </summary>
    public static string LogDirExplicit { get; private set; } = "";

    public static string SnapshotRoot { get; private set; } = DefaultSnapshotRoot;
    public static string ProfileDir { get; private set; } = DefaultProfileDir;

    /// <summary>启动时应用设置里的自定义路径（空则回落默认值）。</summary>
    public static void Apply(string? logDir, string? snapshotRoot, string? profileDir)
    {
        LogDir = Normalize(logDir, DefaultLogDir);
        // 只有真的传了非空的自定义目录才算"显式指定"；空/空白 = 回落默认，交由 Logger 的运行期覆盖接管
        LogDirExplicit = IsBlank(logDir) ? "" : LogDir;
        // 设置里若还指向第三方插件的历史仓库，视为未自定义：快照一律落在程序目录下的 Snapshots
        string snap = string.IsNullOrWhiteSpace(snapshotRoot)
                      || IsSamePath(snapshotRoot, ThirdPartySnapshotRoot) ? "" : snapshotRoot!;
        SnapshotRoot = Normalize(snap, DefaultSnapshotRoot);
        ProfileDir = Normalize(profileDir, DefaultProfileDir);
    }

    /// <summary>空/空白/纯引号都算"没给值"（与 <see cref="Normalize"/> 的判据保持一致）。</summary>
    private static bool IsBlank(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return true;
        return s!.Trim().Trim('"').Length == 0;
    }

    private static bool IsSamePath(string? a, string b)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(a)) return false;
            return string.Equals(Path.GetFullPath(a!.Trim().Trim('"')), Path.GetFullPath(b),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Normalize(string? custom, string fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(custom))
            {
                string p = custom!.Trim().Trim('"');
                if (p.Length > 0) return Path.GetFullPath(p);
            }
        }
        catch { }
        return fallback;
    }
}
