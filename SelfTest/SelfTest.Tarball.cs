using System;

namespace DSHGuard;

/// <summary>
/// 2026-11-04 F-04：归档直链（.tgz / .tar.gz）升为独立一档的回归断言。
/// 全部是纯函数，不联网、不动 profile。
///
/// 为什么必须有这组断言：catalog 实测 801/8655 条是 github.com 的 release 归档直链，旧版
///   Classify 让它落 Registry ⇒ 被 IsValidPackageName 拒 ⇒ 一直装不上。加档只是第一步，
///   真正会回归的是「归档窜进 git 语义」——ClassifyRef 就是第一处实测到的（加档后它从
///   Invalid 变成 DefaultBranch，等于替用户宣布「这条跟默认分支走」）。
/// 既有口径不许动（本文件反向钉死）：
///   · SelfTest.cs 里 PackageNameFromSource("https://example.com/x.tgz") == ""（归档取不出包名）；
///   · PluginManager.cs 的 UpdateVerdictCase 把 tgz 直链判 VersionCheck.Unknown（归档没有可查的新版）；
///   · NeedsGitFor 对归档必须 false（装归档不该拉 git）。
/// </summary>
public static partial class SelfTest
{
    internal static void RunTarballSourceChecks(Action<string, bool, string> check)
    {
        const string ghTgz =
            "https://github.com/ccll/dsh-advisor-flow/releases/download/v0.2.1/dsh-advisor-flow-0.2.1.tgz";
        // catalog 的真实写法：install 字段把 URL 又包了一层引号，InstallSource 取末 token 会带下来
        const string quoted = "\"" + ghTgz + "\"";
        const string latest = "https://github.com/x/y/releases/latest/download/z.tar.gz";

        check("归档直链：识别为 Tarball 档（不再落 Registry 被包名白名单误杀）",
            PluginSource.Classify(ghTgz) == PluginSource.Kind.Tarball &&
            PluginSource.Classify(latest) == PluginSource.Kind.Tarball,
            $"实际={PluginSource.Classify(ghTgz)} / {PluginSource.Classify(latest)}");

        check("归档直链：外层带引号也要认（catalog 的 install 字段就是这个形态）",
            PluginSource.Classify(quoted) == PluginSource.Kind.Tarball,
            $"实际={PluginSource.Classify(quoted)}");

        check("归档直链：安装闸放行，且返回值已剥引号（判定与执行同一份值）",
            PluginManager.ValidatedAddSource(quoted, null) == ghTgz,
            $"实际=[{PluginManager.ValidatedAddSource(quoted, null)}]");

        check("归档直链：白名单外的 host 必须拒（归档会下载解压，放行任意 host = 远程代码投递口）",
            PluginManager.ValidatedAddSource("https://evil.example.com/a.tgz", null).Length == 0 &&
            PluginManager.ValidatedAddSource("https://github.com.evil.com/o/a.tgz", null).Length == 0,
            "两条恶意 host 都应返回空串");

        check("归档直链：明文 http 必须拒（与 git 安装闸同口径，那边也拒 http://）",
            PluginSource.IsArchiveSource("http://github.com/o/a.tgz") == false &&
            PluginManager.ValidatedAddSource("http://github.com/o/a.tgz", null).Length == 0,
            "http 归档应判非归档并被拒");

        check("归档直链：host 精确相等，不放宽到子域（安装闸语义，别新增第三种匹配规则）",
            PluginSource.IsArchiveSource("https://codeload.github.com/o/a.tgz") == false,
            "子域 host 应判非归档");

        check("归档直链：NeedsGitFor 必须 false（装归档不该拉 git）",
            PluginManager.NeedsGitFor(ghTgz) == false,
            $"实际={PluginManager.NeedsGitFor(ghTgz)}");

        // 这条是本文件存在的理由：加 Tarball 档后若不显式列进 ClassifyRef 的挡板，
        //   归档会一路穿到「没写 # ⇒ DefaultBranch」那一支，界面就敢报「跟默认分支」。
        check("归档直链：ClassifyRef 判 Invalid，不得穿进 git 的「跟默认分支」（实测回归过）",
            PluginSource.ClassifyRef(ghTgz) == PluginSource.RefKind.Invalid,
            $"实际={PluginSource.ClassifyRef(ghTgz)}");

        check("归档直链：取不出包名（既有断言口径不许因加档而变）",
            PluginManager.PackageNameFromSource(ghTgz).Length == 0,
            $"实际=[{PluginManager.PackageNameFromSource(ghTgz)}]");

        check("归档直链：界面分类同步认归档（两套判据都要加档，否则界面仍当 npm 包刻画）",
            PluginManager.ClassifySource(ghTgz) == PluginManager.PluginSourceKind.Tarball &&
            PluginManager.ClassifySource(quoted) == PluginManager.PluginSourceKind.Tarball,
            $"实际={PluginManager.ClassifySource(ghTgz)}");

        check("归档直链：既有三类语义不变（npm 包 / git 简写 / 本地路径 不得被新档抢走）",
            PluginSource.Classify("@scope/pkg@1.2.3") == PluginSource.Kind.Registry &&
            PluginSource.Classify("github:o/r") == PluginSource.Kind.GitBare &&
            PluginSource.Classify("git+https://github.com/o/r.git") == PluginSource.Kind.GitBare &&
            PluginManager.ClassifySource("link:./plugins/x") == PluginManager.PluginSourceKind.Local,
            $"实际={PluginSource.Classify("@scope/pkg@1.2.3")} / {PluginSource.Classify("github:o/r")}");

        // 三个条件各自报实测值：合成一个布尔时，失败只能看见"有一条不对"、看不见哪条，
        //   本仓这两天反复在修的就是这种断言（detail 写死措辞 = 失败了也等于没说）。
        bool zipOk = PluginSource.IsArchiveSource("https://github.com/o/r/releases/download/v1/a.zip") == false;
        bool bareOk = PluginSource.IsArchiveSource("https://github.com/o/r") == false;
        bool dotdotOk = PluginSource.IsArchiveSource("https://github.com/o/../a.tgz") == false;
        check("归档直链：非归档的 http(s) 形态不得误判（.zip / 无后缀 / 含 .. 的路径）",
            zipOk && bareOk && dotdotOk,
            $".zip 判归档={PluginSource.IsArchiveSource("https://github.com/o/r/releases/download/v1/a.zip")} "
          + $"无后缀判归档={PluginSource.IsArchiveSource("https://github.com/o/r")} "
          + $"含 .. 判归档={PluginSource.IsArchiveSource("https://github.com/o/../a.tgz")}"
          + "（含 .. 若为 True 说明漏拦：Uri 会把 /o/../ 规范化掉，编码形态 /o/..%2f 又不会）");
    }
}
