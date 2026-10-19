using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DSHGuard;

/// <summary>
/// 2.2.1 各批次的自检断言（由 RunCore 在审计修复断言之后调用）。
/// 每个批次一段，写明"哪一批、钉什么"，后面的批次继续往下加。
/// </summary>
public static partial class SelfTest
{
    internal static void RunBatch221Checks(MainWindow w, Action<string, bool, string> check)
    {
        // ── 第一批次 · 事件信息按目标分开显示 ──
        try
        {
            var panel = (StackPanel)w.FindName("StatusEventsPanel")!;
            string[] Texts() => panel.Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
            string tag = Guid.NewGuid().ToString("N").Substring(0, 6);

            w.SetTargetForTest(false);                                            // 站在 Web 这一边
            MainWindow.AddEvent($"自检-W-{tag}", MainWindow.EventKind.Info, GuardTarget.Web);
            MainWindow.AddEvent($"自检-D-{tag}", MainWindow.EventKind.Info, GuardTarget.Desktop);
            MainWindow.AddEvent($"自检-默认-{tag}");                               // 没写目标 ⇒ 记在此刻的 Web
            w.RefreshStatusEventsForTest();
            var web = Texts();

            w.SetTargetForTest(true);                                             // 换到桌面版
            w.RefreshStatusEventsForTest();
            var desk = Texts();
            w.SetTargetForTest(false);                                            // 还原
            w.RefreshStatusEventsForTest();

            bool Has(string[] ts, string key) => ts.Any(t => t.Contains(key + "-" + tag));
            check("2.2.1 第一批次 · 事件信息按目标分开：Web 只看到 Web 的（含没写目标、记在当时那一边的），桌面版只看到桌面版的",
                Has(web, "自检-W") && Has(web, "自检-默认") && !Has(web, "自检-D") &&
                Has(desk, "自检-D") && !Has(desk, "自检-W") && !Has(desk, "自检-默认"),
                $"Web 面板 {web.Length} 行 · 桌面版面板 {desk.Length} 行 · Web 见 W={Has(web, "自检-W")} 默认={Has(web, "自检-默认")} D={Has(web, "自检-D")} · "
                + $"桌面见 D={Has(desk, "自检-D")} W={Has(desk, "自检-W")} 默认={Has(desk, "自检-默认")}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第一批次 · 事件信息按目标分开", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第一批次 · 插件页作者头像 / 名字：点击能取到作者主页链接 ──
        try
        {
            const string url = "https://github.com/someone";
            var row = w.BuildAuthorLinkForTest("someone", url);
            var parts = row is Panel p ? p.Children.OfType<FrameworkElement>().ToList() : new();
            bool tagOk = MainWindow.AuthorClickUrl(row) == url;
            bool handOk = parts.Count >= 2 && parts.All(x => x.Cursor == System.Windows.Input.Cursors.Hand);
            bool noUrl = MainWindow.AuthorClickUrl(w.BuildAuthorLinkForTest("someone", "")) == "";
            check("2.2.1 第一批次 · 作者头像和名字可点：整行带链接，头像与名字都是手型；没有主页时不可点",
                tagOk && handOk && noUrl, $"链接取到={tagOk} · 部件手型={handOk}（{parts.Count} 个部件）· 无主页不可点={noUrl}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第一批次 · 作者头像和名字可点", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第一批次 · 分类标签栏：收起时选中项居中、左右各有邻居；靠边由另一侧补满 ──
        try
        {
            var cw = Enumerable.Repeat(60.0, 20).ToArray();      // 20 个等宽 chip，一行放 5 个（room = 300）
            var mid = MainWindow.PickChipWindow(cw, 10, 300);
            var slideR = MainWindow.PickChipWindow(cw, 11, 300);   // 点了右边的邻居 ⇒ 窗口整体右移一格
            var head = MainWindow.PickChipWindow(cw, 0, 300);
            var tail = MainWindow.PickChipWindow(cw, 19, 300);
            var edge = MainWindow.PickChipWindow(cw, 1, 300);      // 靠近左端：左边只有 1 个，右边补满
            var missing = MainWindow.PickChipWindow(cw, -1, 300);  // 当前分类不在列表里 ⇒ 当成第 0 项
            check("2.2.1 第一批次 · 分类栏收起态：选中项居中、点右邻居整体右滑一格、两端由另一侧补满（不再把选中项硬塞到「全部」后面）",
                mid == (8, 12) && slideR == (9, 13) && head == (0, 4) && tail == (15, 19) && edge == (0, 4) && missing == (0, 4),
                $"居中 {mid} · 右滑 {slideR} · 首 {head} · 尾 {tail} · 近左端 {edge} · 缺失 {missing}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第一批次 · 分类栏收起态窗口", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第三批次 · 分类栏界面：单行、「全部」常驻、选中项左右都有邻居、展开不换行 ──
        try
        {
            var order = w.CategoryOrderForTest();       // 含「全部」
            if (order.Count < 8)
            {
                check("2.2.1 第三批次 · 分类栏界面（目录分类不足 8 个，跳过）", true, $"分类 {order.Count} 个");
            }
            else
            {
                w.SetCatsExpandedForTest(false);
                int mid = order.Count / 2;
                w.ClickCategoryLinkForTest(order[mid]);
                w.LayoutForTest(960, 640);
                var v1 = w.VisibleCategoryChipsForTest().Select(x => x.Slug).ToList();
                int i1 = v1.IndexOf(order[mid]);
                bool pinned = w.CategoryAllChipTagForTest() == "全部";
                bool nb1 = i1 > 0 && i1 < v1.Count - 1 && v1[i1 - 1] == order[mid - 1] && v1[i1 + 1] == order[mid + 1];

                w.ClickCategoryLinkForTest(order[mid + 1]);          // 点右边的邻居 ⇒ 窗口向左滑一格
                w.LayoutForTest(960, 640);
                var v2 = w.VisibleCategoryChipsForTest().Select(x => x.Slug).ToList();
                int i2 = v2.IndexOf(order[mid + 1]);
                bool slid = i2 > 0 && i2 < v2.Count - 1 && v2[i2 - 1] == order[mid];

                w.ClickCategoryLinkForTest(order[mid]);
                w.StepMarketCategory(1);                               // 滚轮向下 = 下一个分类
                bool stepNext = w.CurrentCategoryForTest == order[mid + 1];
                w.StepMarketCategory(-1);
                bool stepPrev = w.CurrentCategoryForTest == order[mid];
                w.ClickCategoryLinkForTest("全部");
                w.StepMarketCategory(-1);                              // 已在第一个，再往前不动
                bool stepEdge = w.CurrentCategoryForTest == "全部";

                var (collH, collChipH) = w.CategoryRowHeightsForTest();
                bool oneRow = collChipH <= 0 || collH <= collChipH * 1.8;      // 收起时是单行
                int collapsedCount = w.VisibleCategoryChipsForTest().Count;
                w.SetCatsExpandedForTest(true);                        // 展开：全部标签铺开（可换行），按钮变「收起」
                w.LayoutForTest(960, 640);
                var (expH, expChipH) = w.CategoryRowHeightsForTest();
                bool allShown = w.VisibleCategoryChipsForTest().Count == order.Count - 1 && collapsedCount < order.Count - 1;
                bool wrapped = order.Count < 14 || expChipH <= 0 || expH > expChipH * 1.5;   // 标签多时展开后应换成多行
                bool toggle = w.CategoryToggleTextForTest().Contains("收起");
                w.SetCatsExpandedForTest(false);
                check("2.2.1 第四批次 · 分类栏界面：「全部」常驻、收起单行且选中项左右都有邻居、点右邻居后左滑、滚轮步进，展开后全部标签铺开换行",
                    pinned && nb1 && slid && stepNext && stepPrev && stepEdge && oneRow && allShown && wrapped && toggle,
                    $"全部常驻={pinned} · 居中有邻居={nb1}（{string.Join("|", v1)}）· 点右邻居后={slid}（{string.Join("|", v2)}）· "
                    + $"滚轮 下一个={stepNext} 上一个={stepPrev} 边界={stepEdge} · 收起单行={oneRow}（{collH:0.#}/{collChipH:0.#}）展开全显示={allShown} 展开换行={wrapped}（{expH:0.#}/{expChipH:0.#}）按钮收起={toggle}");
            }
        }
        catch (Exception ex)
        {
            check("2.2.1 第三批次 · 分类栏界面", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第六批次 · 窗口空白处可拖动，按钮 / 输入框 / 列表区不触发拖动 ──
        try
        {
            w.LayoutForTest(960, 640);
            bool badge = w.IsDragBlankForTest((DependencyObject)w.FindName("TargetBadge"));
            bool themeBtn = !w.IsDragBlankForTest((DependencyObject)w.FindName("ThemeBtn"));
            bool scroll = !w.IsDragBlankForTest((DependencyObject)w.FindName("MarketScroll"));
            bool box = !w.IsDragBlankForTest((DependencyObject)w.FindName("MarketSearchBox"));
            bool nav = !w.IsDragBlankForTest((DependencyObject)w.FindName("NavSettings"));
            check("2.2.1 第六批次 · 空白处（如标题旁的徽标）可拖动窗口；按钮、导航、输入框、列表区不会",
                badge && themeBtn && scroll && box && nav,
                $"徽标可拖={badge} · 主题按钮不拖={themeBtn} · 列表区不拖={scroll} · 搜索框不拖={box} · 导航不拖={nav}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第六批次 · 空白处拖动", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第五批次 · 桌面版总览只留必要信息（不再列日志目录和说明） ──
        try
        {
            w.SetTargetForTest(true);
            w.RenderDesktopHome();
            var home = (Panel)w.FindName("DesktopHomePanel")!;
            string homeText = string.Join("\n", home.Children.OfType<TextBlock>().Select(t => t.Text));
            w.SetTargetForTest(false);
            check("2.2.1 第五批次 · 桌面版总览不列日志目录和长说明",
                !homeText.Contains("日志目录") && !homeText.Contains("崩溃记录") && homeText.Contains("版本"),
                homeText.Replace('\n', '/'));
        }
        catch (Exception ex)
        {
            check("2.2.1 第五批次 · 桌面版总览", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第四批次 · 看图：初始比例（最多 80%、小图不放大过原始尺寸）与显示范围 ──
        try
        {
            double ratio = MainWindow.LightboxFitRatio;
            // 盒子 1000×600，图 2000×1000：Uniform 后 1000×500，再按比例缩放并居中
            var r = MainWindow.ImageDisplayRect(1000, 600, 2000, 1000, 1.0, ratio, 0, 0);
            bool centered = Math.Abs(r.Left + r.Width / 2 - 500) < 0.01 && Math.Abs(r.Top + r.Height / 2 - 300) < 0.01;
            bool sized = Math.Abs(r.Width - 1000 * ratio) < 0.01 && Math.Abs(r.Height - 500 * ratio) < 0.01;
            var big = MainWindow.ImageDisplayRect(1000, 600, 2000, 1000, 4.0, ratio, 0, 0);
            bool covers = big.Width > 1000;                       // 放大后可铺满并超出舞台
            double rBig = MainWindow.InitialRatio(1000, 600, 4000, 3000);   // 大图 ⇒ 上限 0.8
            double rSmall = MainWindow.InitialRatio(1000, 600, 300, 200);   // 小图 ⇒ 不放大过原始尺寸，下限 0.4
            double rMid = MainWindow.InitialRatio(1000, 600, 800, 500);     // 中图 ⇒ 1/fit = 0.8 以内取原始尺寸
            bool ratios = Math.Abs(rBig - 0.8) < 1e-9 && Math.Abs(rSmall - 0.4) < 1e-9 && rMid <= 0.8 + 1e-9 && rMid >= 0.4;
            check("2.2.1 第四批次 · 看图：初始图片居中、不超过 80%，小图不放大过原始尺寸，放大后可超出舞台",
                centered && sized && covers && ratios,
                $"初始 {r} · 4 倍宽 {big.Width:0.#} · 比例 大图={rBig:0.##} 小图={rSmall:0.##} 中图={rMid:0.##}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第四批次 · 看图显示范围", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }

        // ── 第一批次 · 插件页看图：滚轮直接缩放 / 随意拖动 / 独立大窗口 ──
        try
        {
            // 缩放锚点：光标下的那一点缩放前后必须落在光标下（屏幕坐标 s = z·p + t）
            double c = 137.0, t0 = -40.0, z0 = 1.0, z1 = 2.5;
            double p = (c - t0) / z0;
            double t1 = MainWindow.ZoomAboutPan(c, t0, z0, z1);
            double sAfter = z1 * p + t1;
            check("2.2.1 第一批次 · 看图缩放锚点：光标下的内容点缩放前后不动；倍数 / 坐标非法时原样返回",
                Math.Abs(sAfter - c) < 1e-9 &&
                MainWindow.ZoomAboutPan(100, 7, 0, 2) == 7 && MainWindow.ZoomAboutPan(100, 7, 1, -2) == 7 &&
                MainWindow.ZoomAboutPan(double.NaN, 7, 1, 2) == 7,
                $"s′={sAfter:0.###}（须 {c}）");

            // 拖动软限制：随意拖，但至少留 80px 在视野里
            double lo = MainWindow.ClampPanKeepVisible(-100000, 800, 1.0, 800, 80);
            double hi = MainWindow.ClampPanKeepVisible(+100000, 800, 1.0, 800, 80);
            double mid = MainWindow.ClampPanKeepVisible(123, 800, 1.0, 800, 80);
            double zoomLo = MainWindow.ClampPanKeepVisible(-100000, 800, 4.0, 800, 80);
            check("2.2.1 第一批次 · 看图拖动：不再被滚动范围夹住（没放大也能拖），只留「至少 80px 在视野里」的软限制",
                lo == -720 && hi == 720 && mid == 123 && zoomLo == 80 - 3200 &&
                MainWindow.ClampPanKeepVisible(5, 0, 1, 800, 80) == 5 && MainWindow.ClampPanKeepVisible(5, 800, 1, 0, 80) == 5,
                $"极左={lo} 极右={hi} 居中内=({mid}) 放大 4 倍极左={zoomLo}");

            // 倍数上下限
            check("2.2.1 第一批次 · 看图倍数：上限 16、下限 0.2，到顶后再滚不变",
                MainWindow.NextZoom(16.0, 120) == 16.0 && MainWindow.NextZoom(0.2, -120) == 0.2 &&
                MainWindow.NextZoom(1.0, 120) > 1.0 && MainWindow.NextZoom(1.0, -120) < 1.0,
                $"16→{MainWindow.NextZoom(16.0, 120)} 0.2→{MainWindow.NextZoom(0.2, -120)}");

            // 滚轮缩放入口不再需要 Ctrl（入口本身没有任何按键判断）；缩放后复位
            w.SetLightboxZoomPanForTest(1.0, 0, 0);
            w.ZoomLightboxAt(new System.Windows.Point(100, 100), 120);
            var zp = w.LightboxZoomPanForTest();
            bool zoomed = zp.zoom > 1.05 && zp.zoom < 1.2;
            w.SetLightboxZoomPanForTest(1.0, 0, 0);
            var zp0 = w.LightboxZoomPanForTest();
            check("2.2.1 第一批次 · 看图：直接滚轮（不按 Ctrl）就能缩放，并以光标为锚点平移；复位后回到 1.0 / 0 / 0",
                zoomed && zp.panX < 0 && zp.panY < 0 && zp0.zoom == 1.0 && zp0.panX == 0 && zp0.panY == 0,
                $"滚一格后 zoom={zp.zoom:0.###} pan=({zp.panX:0.#},{zp.panY:0.#}) · 复位后 ({zp0.zoom},{zp0.panX},{zp0.panY})");

            // 独立大窗口：搬家路径 + 自检模式不弹窗
            var rt = w.LightboxViewerRoundTripForTest();
            check("2.2.1 第一批次 · 看图独立窗口：浮层能挪进窗口、名字仍可解析、放回后位置不变；自检 / 截图模式不弹真窗口",
                rt.inViewer && rt.namesOk && rt.restored && MainWindow.LightboxInOwnWindow == false,
                $"挪进窗口={rt.inViewer} 名字可解析={rt.namesOk} 放回原位={rt.restored} 自检模式不弹窗={!MainWindow.LightboxInOwnWindow}");
        }
        catch (Exception ex)
        {
            check("2.2.1 第一批次 · 插件页看图", false, "抛了：" + ex.GetType().Name + " " + ex.Message);
        }
    }
}
