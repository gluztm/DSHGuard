#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using DSHGuard.Frontend;
using DSHGuard.Unified;

namespace DSHGuard;

public static partial class SelfTest
{
    private static class TestSqliteWriter
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_open_v2(byte[] filenameUtf8, out IntPtr db, int flags, IntPtr vfs);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sqlUtf8, IntPtr callback, IntPtr arg, out IntPtr errMsg);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
        private static extern void sqlite3_free(IntPtr ptr);

        private static byte[] Utf8Z(string s)
        {
            byte[] raw = Encoding.UTF8.GetBytes(s);
            byte[] nul = new byte[raw.Length + 1];
            Buffer.BlockCopy(raw, 0, nul, 0, raw.Length);
            return nul;
        }

        internal static bool CreateFakeCcSwitchDb(string dbPath, out string error)
        {
            error = "";
            IntPtr db = IntPtr.Zero;
            try
            {
                string? dir = Path.GetDirectoryName(dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (File.Exists(dbPath)) File.Delete(dbPath);

                // SQLITE_OPEN_READWRITE (2) | SQLITE_OPEN_CREATE (4) = 6
                int rc = sqlite3_open_v2(Utf8Z(dbPath), out db, 6, IntPtr.Zero);
                if (rc != 0 || db == IntPtr.Zero)
                {
                    error = "sqlite3_open_v2 rc=" + rc;
                    return false;
                }

                string sql = @"
CREATE TABLE providers (
    id TEXT PRIMARY KEY,
    app_type TEXT NOT NULL,
    name TEXT NOT NULL,
    settings_config TEXT NOT NULL,
    website_url TEXT,
    sort_index INTEGER,
    is_current INTEGER NOT NULL DEFAULT 0
);
INSERT INTO providers (id, app_type, name, settings_config, website_url, sort_index, is_current) VALUES
('deepseek-demo', 'dsh', 'DeepSeek 官方(演示)', '{""displayName"":""DeepSeek"",""apiKeyEnv"":""DEEPSEEK_API_KEY"",""api"":""openai-completions"",""baseURL"":""https://api.deepseek.com/v1"",""models"":{""deepseek-chat"":{},""deepseek-reasoner"":{}},""options"":{""apiKey"":""sk-demo-deepseek-111122223333abcd""}}', 'https://platform.deepseek.com', 1, 1),
('claude-relay-demo', 'claude-desktop', 'Claude 中继(演示)', '{""env"":{""ANTHROPIC_BASE_URL"":""https://relay.example.com"",""ANTHROPIC_AUTH_TOKEN"":""sk-ant-demo-444455556666efgh"",""ANTHROPIC_MODEL"":""claude-sonnet-4-5""}}', '', 2, 0),
('codex-demo', 'codex', 'Codex OpenAI(演示)', '{""auth"":{""OPENAI_API_KEY"":""sk-codex-demo-777788889999ijkl""},""config"":""base_url = \""https://openai.example.com/v1\""\nmodel = \""gpt-4o\""""}', '', 3, 0),
('gemini-demo', 'gemini', 'Gemini Pro(演示)', '{""env"":{""GOOGLE_GEMINI_BASE_URL"":""https://gemini.example.com/v1beta"",""GEMINI_API_KEY"":""AIzaDemoKey000011112222"",""GEMINI_MODEL"":""gemini-2.5-pro""}}', '', 4, 0);
";
                int execRc = sqlite3_exec(db, Utf8Z(sql), IntPtr.Zero, IntPtr.Zero, out IntPtr errPtr);
                if (errPtr != IntPtr.Zero)
                {
                    error = Marshal.PtrToStringUTF8(errPtr) ?? ("rc=" + execRc);
                    sqlite3_free(errPtr);
                    return false;
                }
                return execRc == 0;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (db != IntPtr.Zero)
                {
                    try { _ = sqlite3_close_v2(db); } catch { }
                }
            }
        }
    }

    internal static void CreateDemoCredentialFixture(string sandboxRoot)
    {
        string webDir = Path.Combine(sandboxRoot, "profiles", "web");
        string deskDir = Path.Combine(sandboxRoot, "profiles", "desktop");
        Directory.CreateDirectory(webDir);
        Directory.CreateDirectory(deskDir);

        string credYaml =
            "version: 1\n" +
            "records:\n" +
            "  - kind: oauth\n" +
            "    provider: demo-oauth\n" +
            "refs:\n" +
            "  DEEPSEEK_API_KEY: \"sk-demo-deepseek-0000111122229a8b\"\n" +
            "  OPENROUTER_API_KEY: \"sk-or-demo-333344445555c7e9\"\n" +
            "  DSH_IMAGE_GEN_OPENAI_COMPAT_KEY: \"sk-img-demo-777788889999f102\"\n" +
            "  BUDDY_ACCOUNT_DEMO0001: \"{\\\"account\\\":\\\"demo-user\\\",\n" +
            "    \\\"accessToken\\\":\\\"demo-oauth-token-0000\\\"}\"\n";
        File.WriteAllText(Path.Combine(sandboxRoot, ".credentials.yaml"), credYaml, new UTF8Encoding(false));

        string webPatch =
            "- id: llm-pi-ai\n" +
            "  config:\n" +
            "    providers:\n" +
            "      deepseek:\n" +
            "        displayName: DeepSeek 官方\n" +
            "        apiKeyEnv: DEEPSEEK_API_KEY\n" +
            "        api: openai-completions\n" +
            "        baseURL: https://api.deepseek.com/v1\n" +
            "        models:\n" +
            "          - id: deepseek-chat\n" +
            "            name: deepseek-chat\n" +
            "          - id: deepseek-reasoner\n" +
            "            name: deepseek-reasoner\n" +
            "      openrouter:\n" +
            "        displayName: OpenRouter\n" +
            "        apiKeyEnv: OPENROUTER_API_KEY\n" +
            "        api: openai-completions\n" +
            "        baseURL: https://openrouter.ai/api/v1\n" +
            "        models:\n" +
            "          - id: anthropic/claude-sonnet-4\n" +
            "            name: anthropic/claude-sonnet-4\n";
        File.WriteAllText(Path.Combine(webDir, "cordis.patch.yml"), webPatch, new UTF8Encoding(false));

        string deskPatch =
            "- id: llm-pi-ai\n" +
            "  name: \"@deepseek-ai/dsh-llm-pi-ai\"\n" +
            "  config:\n" +
            "    providers:\n" +
            "      deepseek:\n" +
            "        displayName: DeepSeek 官方\n" +
            "        apiKeyEnv: DEEPSEEK_API_KEY\n" +
            "        api: openai-completions\n" +
            "        baseURL: https://api.deepseek.com/v1\n" +
            "        models:\n" +
            "          - id: deepseek-chat\n" +
            "            name: deepseek-chat\n";
        File.WriteAllText(Path.Combine(deskDir, "cordis.patch.yml"), deskPatch, new UTF8Encoding(false));

        string dbPath = Path.Combine(sandboxRoot, ".cc-switch", "cc-switch.db");
        _ = TestSqliteWriter.CreateFakeCcSwitchDb(dbPath, out _);
    }

    private sealed class SampleVm : ObservableObject
    {
        private string _title = "init";
        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value);
        }
    }

    internal static void RunFrontend240Checks(
        MainWindow w,
        Action<string, bool, string> check,
        Action<string, string>? skip = null)
    {
        string sandbox = Path.Combine(Path.GetTempPath(), "dshguard-240-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        try
        {
            // ── F24-1: 语义主题令牌日夜字典对称与动态切换 ──
            {
                string[] darkKeys = ThemeManager.TokenKeysForTest(true);
                string[] lightKeys = ThemeManager.TokenKeysForTest(false);
                bool parity = darkKeys.Length >= 30 && darkKeys.SequenceEqual(lightKeys, StringComparer.Ordinal);
                ThemeManager.SwitchTokenDictionary(false);
                var lightCard = DesignTokens.ResolveColor("Color.Surface.Card", default);
                ThemeManager.SwitchTokenDictionary(true);
                var darkCard = DesignTokens.ResolveColor("Color.Surface.Card", default);
                bool switched = lightCard != darkCard
                    && lightCard.R == 0xF2 && darkCard.R == 0x1E;
                check("2.4.0 · 语义令牌字典：日夜键集合严格对称，动态切换即刻生效",
                    parity && switched,
                    $"Dark={darkKeys.Length} 键 · Light={lightKeys.Length} 键 · 对称={parity} · Light卡={lightCard} / Dark卡={darkCard}");
            }

            // ── F24-2: 轻量 MVVM 基元（ObservableObject / RelayCommand / AsyncRelayCommand）──
            {
                var vm = new SampleVm();
                var fired = new List<string>();
                vm.PropertyChanged += (_, e) => { if (e.PropertyName != null) fired.Add(e.PropertyName); };
                vm.Title = "init"; // 相同值不应重复触发
                vm.Title = "updated";

                int count = 0;
                bool canRun = false;
                var cmd = new RelayCommand(() => count++, () => canRun);
                cmd.Execute(null); // 禁止态不触发
                canRun = true;
                cmd.RaiseCanExecuteChanged();
                cmd.Execute(null);

                bool asyncRan = false;
                var asyncCmd = new AsyncRelayCommand(() =>
                {
                    asyncRan = true;
                    return Task.CompletedTask;
                });
                asyncCmd.Execute(null);

                check("2.4.0 · 前端 MVVM 基元：去重 PropertyChanged / CanExecute 拦截 / 异步命令可执行",
                    fired.Count == 1 && fired[0] == "Title" && count == 1 && asyncRan,
                    $"属性事件={fired.Count} 同步命令={count} 异步命令={asyncRan}");
            }

            // ── F24-3: UiTrace 耗时聚合（Count / P95 / Max）──
            {
                string opName = "自检聚合样本-" + Guid.NewGuid().ToString("N")[..6];
                for (int i = 0; i < 4; i++)
                {
                    var e = UiTrace.Enter(opName);
                    UiTrace.Exit(e);
                }
                var summary = UiTrace.SummarySnapshot();
                var hit = summary.FirstOrDefault(x => x.Name == opName);
                bool ok = hit != null && hit.Count == 4 && hit.MaxMs >= 0 && hit.P95Ms >= 0 && UiTrace.DescribeSummary().Contains("最慢", StringComparison.Ordinal);
                check("2.4.0 · UiTrace 耗时聚合：正确累计次数与 P95/Max 读数且零阻塞",
                    ok,
                    hit == null ? "未查到样本" : $"Count={hit.Count} P95={hit.P95Ms:0.###}ms Max={hit.MaxMs:0.###}ms");
            }

            // ── F24-4: .credentials.yaml 无损往返 + 账号态只读保护 + .bak-cred 备份 ──
            {
                CreateDemoCredentialFixture(sandbox);
                string credPath = Path.Combine(sandbox, ".credentials.yaml");
                var initial = DshCredentialManager.ScanCredentials(credPath);
                bool hasMultiLineAccount = initial.Entries.Any(e => e.Key == "BUDDY_ACCOUNT_DEMO0001" && e.IsReadOnlyAccount && e.LineCount >= 2);
                bool maskedSafe = initial.Entries.Where(e => e.Key == "DEEPSEEK_API_KEY").All(e => !e.MaskedValue.Contains("0000111122229a8b", StringComparison.Ordinal));

                // 大小写不敏感更新已有 Key
                var upExisting = DshCredentialManager.UpsertCredentialRef("deepseek_api_key", "sk-updated-demo-99990000aaaa", credPath);
                // 试图覆写与删除 _ACCOUNT_ 必须被拒绝
                var rejOverwrite = DshCredentialManager.UpsertCredentialRef("BUDDY_ACCOUNT_DEMO0001", "sk-evil", credPath);
                var rejDelete = DshCredentialManager.DeleteCredentialRef("BUDDY_ACCOUNT_DEMO0001", credPath);
                // 新增后删除普通 Key
                var upNew = DshCredentialManager.UpsertCredentialRef("CUSTOM_TEST_KEY", "sk-custom-demo-1234567890", credPath);
                var delNew = DshCredentialManager.DeleteCredentialRef("CUSTOM_TEST_KEY", credPath);

                string finalRaw = File.ReadAllText(credPath);
                bool recordsPreserved = finalRaw.Contains("version: 1", StringComparison.Ordinal)
                                     && finalRaw.Contains("provider: demo-oauth", StringComparison.Ordinal)
                                     && finalRaw.Contains("BUDDY_ACCOUNT_DEMO0001", StringComparison.Ordinal);
                bool backupCreated = Directory.GetFiles(sandbox, ".credentials.yaml.bak-cred-*").Length >= 1;
                var after = DshCredentialManager.ScanCredentials(credPath);
                bool afterStateOk = after.Entries.Any(e => string.Equals(e.Key, "DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase))
                                 && after.Entries.All(e => !string.Equals(e.Key, "CUSTOM_TEST_KEY", StringComparison.OrdinalIgnoreCase));

                check("2.4.0 · .credentials.yaml 无损收纳：保留 records 与多行账号令牌、脱敏展示、账号态禁写删、留 .bak-cred",
                    hasMultiLineAccount && maskedSafe && upExisting.Ok && !rejOverwrite.Ok && !rejDelete.Ok
                    && upNew.Ok && delNew.Ok && recordsPreserved && backupCreated && afterStateOk,
                    $"多行账号识别={hasMultiLineAccount} 脱敏={maskedSafe} 账号拒写={!rejOverwrite.Ok} 账号拒删={!rejDelete.Ok} 保底records={recordsPreserved} 备份={backupCreated}");
            }

            // ── F24-5: cordis.patch.yml llm-pi-ai 双端投射与未知子块保留 ──
            {
                string webPatch = Path.Combine(sandbox, "profiles", "web", "cordis.patch.yml");
                string deskPatch = Path.Combine(sandbox, "profiles", "desktop", "cordis.patch.yml");
                string credPath = Path.Combine(sandbox, ".credentials.yaml");

                // 给 desktop 的 deepseek 增加一个 compat 块测试是否会在更新时无损保留
                string deskText = File.ReadAllText(deskPatch) + "        compat:\n          supportsStore: false\n";
                File.WriteAllText(deskPatch, deskText, new UTF8Encoding(false));

                var profiles = new[]
                {
                    new DshProfileInfo(DshProfileKind.Web, "web", "Web 版", "", () => Path.Combine(sandbox, "profiles", "web"), () => webPatch, true),
                    new DshProfileInfo(DshProfileKind.Desktop, "desktop", "桌面版", "", () => Path.Combine(sandbox, "profiles", "desktop"), () => deskPatch, true),
                };

                var addStep = new DshProviderDraft
                {
                    ProviderId = "stepfun",
                    DisplayName = "阶跃星辰",
                    ApiKeyEnv = "STEPFUN_API_KEY",
                    Api = "openai-completions",
                    BaseUrl = "https://api.stepfun.com/v1",
                    Models = new[] { "step-2-16k" },
                };
                var writeDesk = DshCredentialManager.ApplyProviderToProfile(profiles[1], addStep);
                var updateDeepSeekDesk = DshCredentialManager.ApplyProviderToProfile(profiles[1], new DshProviderDraft
                {
                    ProviderId = "deepseek",
                    DisplayName = "DeepSeek 更新",
                    ApiKeyEnv = "DEEPSEEK_API_KEY",
                    Api = "openai-completions",
                    BaseUrl = "https://api.deepseek.com/v1",
                    Models = new[] { "deepseek-chat", "deepseek-reasoner" },
                });
                var listAfterAdd = DshCredentialManager.ScanProviders(profiles, credPath);
                bool hasStepOnDesk = listAfterAdd.Any(p => p.ProviderId == "stepfun" && p.EnabledIn("desktop") && !p.EnabledIn("web"));
                bool keptCompat = File.ReadAllText(deskPatch).Contains("supportsStore: false", StringComparison.Ordinal);

                var remStep = DshCredentialManager.RemoveProviderFromProfile(profiles[1], "stepfun");
                var listAfterRem = DshCredentialManager.ScanProviders(profiles, credPath);
                bool goneStep = listAfterRem.All(p => p.ProviderId != "stepfun");

                check("2.4.0 · 模型提供商跨端投影：web / desktop 独立启用与移除，保留 compat 调优子块",
                    writeDesk.Ok && updateDeepSeekDesk.Ok && hasStepOnDesk && keptCompat && remStep.Ok && goneStep,
                    $"写桌面={writeDesk.Ok} 保留compat={keptCompat} 移除={remStep.Ok && goneStep}");
            }

            // ── F24-6: CC Switch 多源导入（winsqlite3 只读 DB + ccswitch:// 深链 + JSON/curl/.env）──
            {
                string fakeDb = Path.Combine(sandbox, ".cc-switch", "cc-switch.db");
                bool created = TestSqliteWriter.CreateFakeCcSwitchDb(fakeDb, out string dbErr);
                var dbReport = DshCcSwitchImporter.ScanCcSwitchDb(fakeDb);
                bool dbOk = created && dbReport.Available
                         && dbReport.Candidates.Count == 4
                         && dbReport.Candidates.Any(c => c.AppType == "dsh" && c.ApiKey.Contains("deepseek-1111", StringComparison.Ordinal))
                         && dbReport.Candidates.Any(c => c.AppType == "claude-desktop" && c.Api == "anthropic-messages")
                         && dbReport.Candidates.Any(c => c.AppType == "codex" && c.BaseUrl.Contains("openai.example.com", StringComparison.Ordinal))
                         && dbReport.Candidates.Any(c => c.AppType == "gemini" && c.Api == "google-generative-ai");

                var dl1 = DshCcSwitchImporter.ParseCcSwitchDeepLink(
                    "ccswitch://import?name=RelayA&endpoint=https://relay-a.example.com/v1&apiKey=sk-dl-demo-1234567890&protocol=openai&model=gpt-4o,o3-mini");
                string b64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    "{\"name\":\"RelayB\",\"baseUrl\":\"https://claude-b.example.com\",\"apiKey\":\"sk-b64-demo-9876543210\",\"api\":\"anthropic-messages\"}"));
                var dl2 = DshCcSwitchImporter.ParseCcSwitchDeepLink("ccswitch://v1/provider?data=" + Uri.EscapeDataString(b64Json));

                var fromCurl = DshCcSwitchImporter.ParseSmartText(
                    "curl https://api.stepfun.com/v1/chat/completions -H \"Authorization: Bearer sk-curl-demo-abcdef123456\" -d '{\"model\":\"step-2-16k\"}'");
                var fromEnv = DshCcSwitchImporter.ParseSmartText(
                    "ANTHROPIC_BASE_URL=https://relay-c.example.com\nANTHROPIC_AUTH_TOKEN=sk-env-demo-xyz7890123");

                bool parseOk = dl1.Count == 1 && dl1[0].Models.Count == 2 && dl1[0].BaseUrl == "https://relay-a.example.com/v1"
                            && dl2.Count == 1 && dl2[0].Api == "anthropic-messages"
                            && fromCurl.Count == 1 && fromCurl[0].Models.Contains("step-2-16k")
                            && fromEnv.Count == 1 && fromEnv[0].Api == "anthropic-messages" && fromEnv[0].ApiKey == "sk-env-demo-xyz7890123";

                check("2.4.0 · CC Switch 五合一导入：winsqlite3.dll 四端库直读、ccswitch:// 深链、curl 与 .env 智能识别",
                    dbOk && parseOk,
                    $"SQLite={dbOk}({dbReport.Candidates.Count}条, {dbErr}) · 深链={dl1.Count}+{dl2.Count} · curl={fromCurl.Count} · env={fromEnv.Count}");
            }

            // ── F24-7: 标题栏快速入口直达「综合管理 → 凭据」 ──
            {
                string? oldFixture = GuardPaths.FixtureDefaultRoot;
                string prevLog = GuardPaths.LogDir;
                string prevSnap = GuardPaths.SnapshotRoot;
                string prevProf = GuardPaths.ProfileDir;
                string prevDeskInst = GuardPaths.DesktopInstallDir;
                string prevDeskProf = GuardPaths.DesktopProfileDir;
                try
                {
                    GuardPaths.FixtureDefaultRoot = sandbox;
                    GuardPaths.Apply(null, null, null, "", Path.Combine(sandbox, "profiles", "desktop"));
                    w.InvalidateCredentialCacheForTest();
                    w.OpenCredentialVault();
                    w.LayoutForTest(960, 640);
                    var rendered = w.SectionBarRenderedForTest();
                    bool credActive = rendered != null && rendered.Any(x => x.Label == DshText.SectionCredential && x.Active);
                    check("2.4.0 · 标题栏「凭据」快捷胶囊可一键直达综合管理凭据分区并选中该格",
                        credActive,
                        rendered == null ? "视图树未构建" : string.Join(" / ", rendered.Select(r => (r.Active ? "*" : "") + r.Label)));
                }
                finally
                {
                    GuardPaths.FixtureDefaultRoot = oldFixture;
                    GuardPaths.Apply(prevLog, prevSnap, prevProf, prevDeskInst, prevDeskProf);
                    w.InvalidateCredentialCacheForTest();
                }
            }
        }
        catch (Exception ex)
        {
            check("2.4.0 · 前端与凭据集成自检未抛异常", false, ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
        }
    }
}
