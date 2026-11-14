#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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

        // 其他本机 Agent 沙盒样本（仅明文 API Key 可读，OAuth 订阅登录态严格跳过）
        string claudeDir = Path.Combine(sandboxRoot, ".claude");
        Directory.CreateDirectory(claudeDir);
        File.WriteAllText(Path.Combine(claudeDir, "settings.json"),
            "{\"env\":{\"ANTHROPIC_BASE_URL\":\"https://relay-demo.example.com\",\"ANTHROPIC_AUTH_TOKEN\":\"sk-ant-demo-claude-112233445566\",\"ANTHROPIC_MODEL\":\"claude-sonnet-4-5\"}}",
            new UTF8Encoding(false));

        string codexDir = Path.Combine(sandboxRoot, ".codex");
        Directory.CreateDirectory(codexDir);
        File.WriteAllText(Path.Combine(codexDir, "config.toml"),
            "model = \"gpt-4o\"\nmodel_provider = \"demo_relay\"\n[model_providers.demo_relay]\nbase_url = \"https://openai-demo.example.com/v1\"\nenv_key = \"OPENAI_API_KEY\"\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(codexDir, "auth.json"),
            "{\"OPENAI_API_KEY\":\"sk-proj-demo-codex-998877665544\"}",
            new UTF8Encoding(false));

        string gemDir = Path.Combine(sandboxRoot, ".gemini");
        Directory.CreateDirectory(gemDir);
        File.WriteAllText(Path.Combine(gemDir, "settings.json"),
            "{\"security\":{\"auth\":{\"selectedType\":\"oauth-personal\"}}}",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(gemDir, "oauth_creds.json"),
            "{\"access_token\":\"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.demo.payload\",\"refresh_token\":\"1//demo-refresh\"}",
            new UTF8Encoding(false));
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

            // ── F24-4: .credentials.yaml 无损往返 + 账号态只读保护 + 零额外密钥副本 ──
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
                bool noExtraCredBak = Directory.GetFiles(sandbox, ".credentials.yaml.bak*").Length == 0;
                var after = DshCredentialManager.ScanCredentials(credPath);
                bool afterStateOk = after.Entries.Any(e => string.Equals(e.Key, "DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase))
                                 && after.Entries.All(e => !string.Equals(e.Key, "CUSTOM_TEST_KEY", StringComparison.OrdinalIgnoreCase));

                check("2.4.0 · .credentials.yaml 官方表单直写：保留 records 与多行账号令牌、账号态禁写删、不生成 .bak-cred 密钥副本",
                    hasMultiLineAccount && maskedSafe && upExisting.Ok && !rejOverwrite.Ok && !rejDelete.Ok
                    && upNew.Ok && delNew.Ok && recordsPreserved && noExtraCredBak && afterStateOk,
                    $"多行账号={hasMultiLineAccount} 脱敏={maskedSafe} 账号拒写={!rejOverwrite.Ok} 账号拒删={!rejDelete.Ok} 保底records={recordsPreserved} 零额外副本={noExtraCredBak}");
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

            // ── F24-6: 自由格式 URL+API 文件读取 + 其他 Agent 非订阅明文读取（严格跳过 OAuth 订阅）──
            string freeDropFile = Path.Combine(sandbox, "my-api-drop.txt");
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

                // 自由格式文本文件（装着 URL 与 API Key）一键解析
                File.WriteAllText(freeDropFile,
                    "地址: https://drop-relay.example.com/v1/chat/completions\n" +
                    "密钥: sk-drop-demo-abcdef1234567890\n" +
                    "model: deepseek-chat\n",
                    new UTF8Encoding(false));
                var fileReport = DshCcSwitchImporter.ImportFromFile(freeDropFile);
                bool fileOk = fileReport.Available && fileReport.Candidates.Count == 1
                           && fileReport.Candidates[0].BaseUrl == "https://drop-relay.example.com/v1"
                           && fileReport.Candidates[0].ApiKey == "sk-drop-demo-abcdef1234567890"
                           && fileReport.Candidates[0].Models.Contains("deepseek-chat");

                // 独立 Agent 沙盒测试：Claude 明文 + Codex 明文 + Codex ChatGPT 订阅(无明文key应跳过) + Gemini OAuth 订阅(应跳过)
                string agentOnlyRoot = Path.Combine(sandbox, "agent-only-home");
                Directory.CreateDirectory(Path.Combine(agentOnlyRoot, ".claude"));
                Directory.CreateDirectory(Path.Combine(agentOnlyRoot, ".codex"));
                Directory.CreateDirectory(Path.Combine(agentOnlyRoot, ".gemini"));
                File.WriteAllText(Path.Combine(agentOnlyRoot, ".claude", "settings.json"),
                    "{\"env\":{\"ANTHROPIC_BASE_URL\":\"https://claude-plain.example.com\",\"ANTHROPIC_AUTH_TOKEN\":\"sk-ant-plain-9988776655443322\"}}",
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(agentOnlyRoot, ".codex", "config.toml"),
                    "base_url = \"https://codex-sub.example.com/v1\"", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(agentOnlyRoot, ".codex", "auth.json"),
                    "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"access_token\":\"eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.sub.token\"}}",
                    new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(agentOnlyRoot, ".gemini", "oauth_creds.json"),
                    "{\"access_token\":\"ya29.demo-oauth\"}", new UTF8Encoding(false));
                var agentRep = DshCcSwitchImporter.ScanLocalAgents(agentOnlyRoot);
                bool agentOk = agentRep.Candidates.Count == 1
                            && agentRep.Candidates[0].Source == "Claude Code"
                            && agentRep.Candidates[0].BaseUrl == "https://claude-plain.example.com"
                            && agentRep.Summary.Contains("跳过", StringComparison.Ordinal);

                var fromCurl = DshCcSwitchImporter.ParseSmartText(
                    "curl https://api.stepfun.com/v1/chat/completions -H \"Authorization: Bearer sk-curl-demo-abcdef123456\" -d '{\"model\":\"step-2-16k\"}'");
                var fromEnv = DshCcSwitchImporter.ParseSmartText(
                    "ANTHROPIC_BASE_URL=https://relay-c.example.com\nANTHROPIC_AUTH_TOKEN=sk-env-demo-xyz7890123");

                bool parseOk = fromCurl.Count == 1 && fromCurl[0].Models.Contains("step-2-16k")
                            && fromEnv.Count == 1 && fromEnv[0].Api == "anthropic-messages" && fromEnv[0].ApiKey == "sk-env-demo-xyz7890123";

                check("2.4.0 · 凭据一键读取：自由 URL+API 文件解析、本机其他 Agent 非订阅明文提取且严格跳过 OAuth/ChatGPT 订阅",
                    dbOk && fileOk && agentOk && parseOk,
                    $"File={fileOk} · Agent明文={agentRep.Candidates.Count}({agentRep.Summary}) · DB={dbOk}({dbErr}) · Parse={parseOk}");
            }

            // ── F24-7: 标题栏「凭据」快捷入口已关闭、Ctrl+7 / 左栏导航可达、切离凭据页立刻清空内存瞬态 ──
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
                    bool topQuickRemoved = w.FindName("QuickVaultBtn") == null;
                    w.InvalidateCredentialCacheForTest();
                    w.LoadCredentialFileForPreviewForTest(freeDropFile);
                    w.LayoutForTest(1000, 660);
                    bool credShown = w.FindName("CredentialsView") is UIElement cv && cv.Visibility == Visibility.Visible;
                    bool previewLoaded = w.IsCredentialImportOpenForTest && w.TransientImportCandidateCountForTest == 1;
                    bool unifiedHidden = w.FindName("UnifiedView") is UIElement uv && uv.Visibility != Visibility.Visible;
                    var rendered = w.SectionBarRenderedForTest();
                    bool unifiedFive = rendered != null && rendered.Count == 5
                                    && rendered.All(x => !string.Equals(x.Label, DshText.SectionCredential, StringComparison.Ordinal));

                    // 切离凭据页：瞬态导入缓存必须立刻归零
                    w.ShowViewForTest("status");
                    bool transientCleared = !w.IsCredentialImportOpenForTest && w.TransientImportCandidateCountForTest == 0;

                    check("2.4.0 · 顶栏「凭据」入口已关闭，独立凭据区由左栏/Ctrl+7直达且切页零缓存清零",
                        topQuickRemoved && credShown && previewLoaded && unifiedHidden && unifiedFive && transientCleared,
                        $"顶栏移除={topQuickRemoved} · CredentialsView={credShown} · 预览瞬态={previewLoaded} · 切页清零={transientCleared} · 综合分区={rendered?.Count ?? -1}");
                }
                finally
                {
                    GuardPaths.FixtureDefaultRoot = oldFixture;
                    GuardPaths.Apply(prevLog, prevSnap, prevProf, prevDeskInst, prevDeskProf);
                    w.InvalidateCredentialCacheForTest();
                }
            }

            // ── F24-8: 四种显示模式（Dark/Light × Glass/Solid）全域视图一致性 ──
            {
                var rootBorder = w.FindName("RootBorder") as Border;
                bool prevDark = ThemeManager.IsDark;
                var matrixErrors = new List<string>();
                string[] allViews = { "status", "logs", "trends", "plugins", "unified", "credentials", "settings", "about" };

                foreach (bool dark in new[] { true, false })
                {
                    // 用临时隐藏 Handle 验证 DWM 沉浸式深色随主题同步（不污染主窗状态）
                    var probeWin = new Window
                    {
                        Width = 10,
                        Height = 10,
                        WindowStyle = WindowStyle.None,
                        ShowInTaskbar = false
                    };
                    try
                    {
                        new System.Windows.Interop.WindowInteropHelper(probeWin).EnsureHandle();
                        BlurHelper.SyncThemeChrome(probeWin, dark);
                        if (BlurHelper.LastChromeDarkForTest != dark)
                            matrixErrors.Add($"SyncThemeChrome({dark}) -> {BlurHelper.LastChromeDarkForTest}");
                    }
                    finally
                    {
                        try { probeWin.Close(); } catch { }
                    }

                    foreach (bool acrylic in new[] { true, false })
                    {
                        w.SetAcrylicStateForTest(acrylic);
                        w.ApplyThemeForTest(dark);

                        // 验证 RootBorder 渐变停点与 WindowStops(dark, acrylic) 逐项吻合
                        var expectedStops = ThemeManager.WindowStops(dark, acrylic);
                        if (rootBorder?.Background is LinearGradientBrush lg && lg.GradientStops.Count >= 3)
                        {
                            for (int si = 0; si < 3; si++)
                            {
                                if (lg.GradientStops[si].Color != expectedStops[si])
                                    matrixErrors.Add($"Stop[{dark},{acrylic}][{si}]={lg.GradientStops[si].Color}!={expectedStops[si]}");
                            }
                        }
                        else
                        {
                            matrixErrors.Add($"RootBorder.Background missing gradient ({dark},{acrylic})");
                        }

                        // 验证 Glass（高可读磨砂 [0x60, 0xC8]）与 Solid（实底 == 0xFF）卡片语义令牌显著区分
                        if (Application.Current?.Resources["Brush.Surface.Card"] is SolidColorBrush cardTokenBrush)
                        {
                            byte a = cardTokenBrush.Color.A;
                            if (acrylic && (a < 0x60 || a > 0xC8))
                                matrixErrors.Add($"Glass Brush.Surface.Card alpha={a} (expected 0x60..0xC8)");
                            if (!acrylic && a != 0xFF)
                                matrixErrors.Add($"Solid Brush.Surface.Card alpha={a} (!=0xFF, not solid)");
                        }
                        else
                        {
                            matrixErrors.Add($"Brush.Surface.Card missing ({dark},{acrylic})");
                        }

                        // 验证 Glass 模式下次级灰色按钮语义令牌稳重磨砂、Solid 模式下全不透明
                        if (Application.Current?.Resources["Brush.Surface.Button"] is SolidColorBrush btnTokenBrush)
                        {
                            byte a = btnTokenBrush.Color.A;
                            if (acrylic && (a < 0x60 || a > 0xD0))
                                matrixErrors.Add($"Glass Brush.Surface.Button alpha={a} (expected 0x60..0xD0)");
                            if (!acrylic && a != 0xFF)
                                matrixErrors.Add($"Solid Brush.Surface.Button alpha={a} (!=0xFF, not solid)");
                        }
                        else
                        {
                            matrixErrors.Add($"Brush.Surface.Button missing ({dark},{acrylic})");
                        }

                        // 验证 Glass 模式下输入框语义令牌适度透感且暗夜不发白泛灰；Solid 模式下全不透明
                        if (Application.Current?.Resources["Brush.Surface.Input"] is SolidColorBrush inputTokenBrush)
                        {
                            byte a = inputTokenBrush.Color.A;
                            if (acrylic && (a < 0x48 || a > 0xC8))
                                matrixErrors.Add($"Glass Brush.Surface.Input alpha={a} (expected 0x48..0xC8)");
                            if (acrylic && dark && inputTokenBrush.Color.R >= 0x40)
                                matrixErrors.Add($"Dark Glass Brush.Surface.Input R={inputTokenBrush.Color.R} (>=0x40, cloudy)");
                            if (!acrylic && a != 0xFF)
                                matrixErrors.Add($"Solid Brush.Surface.Input alpha={a} (!=0xFF, not solid)");
                        }
                        else
                        {
                            matrixErrors.Add($"Brush.Surface.Input missing ({dark},{acrylic})");
                        }

                        void CheckSurfaceControl(string label, Brush? bg, Thickness thickness, bool requireBorder)
                        {
                            if (bg is not SolidColorBrush sb)
                            {
                                matrixErrors.Add($"{label}({dark},{acrylic}) Background missing");
                                return;
                            }
                            if (acrylic && (sb.Color.A < 0x48 || sb.Color.A > 0xD0))
                                matrixErrors.Add($"Glass {label}({dark}) alpha={sb.Color.A} (expected 0x48..0xD0)");
                            if (!acrylic && sb.Color.A != 0xFF)
                                matrixErrors.Add($"Solid {label}({dark}) alpha={sb.Color.A} (!=0xFF)");
                            if (requireBorder && thickness.Left < 1.0)
                                matrixErrors.Add($"{label}({dark},{acrylic}) BorderThickness.Left={thickness.Left} (<1)");
                        }

                        if (w.FindName("PluginSearchBox") is TextBox psb)
                            CheckSurfaceControl("PluginSearchBox", psb.Background, psb.BorderThickness, false);
                        else
                            matrixErrors.Add("PluginSearchBox missing");

                        if (w.FindName("MarketSearchBox") is TextBox msb)
                            CheckSurfaceControl("MarketSearchBox", msb.Background, msb.BorderThickness, false);
                        else
                            matrixErrors.Add("MarketSearchBox missing");

                        if (w.FindName("LogBox") is TextBox lb)
                            CheckSurfaceControl("LogBox", lb.Background, lb.BorderThickness, true);
                        else
                            matrixErrors.Add("LogBox missing");

                        var sampleMarketPlugin = new PluginMarket.MarketPlugin
                        {
                            Name = "demo-guard-plugin",
                            Owner = "gluztm",
                            DescZh = "用于全模式视觉与毛玻璃令牌核验的样本卡片",
                            Npm = "dsh-demo-plugin",
                            Version = "1.0.0",
                            Stars = 42,
                        };
                        var mCard = w.BuildMarketCardForTest(sampleMarketPlugin);
                        CheckSurfaceControl("BuildMarketCard", mCard.Background, mCard.BorderThickness, true);

                        // 日间模式下校验 MascotFooter 常态前景色对比度足够（亮度 <= 120）
                        if (!dark)
                        {
                            Color normalFooter = ThemeManager.ForCurrentTheme(Color.FromRgb(0x8E, 0x8E, 0x93));
                            double lum = 0.299 * normalFooter.R + 0.587 * normalFooter.G + 0.114 * normalFooter.B;
                            if (lum > 120)
                                matrixErrors.Add($"Light normalFooter lum={lum:0.0} (>120)");
                            if (!Mascot.State().IsEgg && w.FindName("MascotFooter") is TextBlock mf
                                && mf.Foreground is SolidColorBrush mfb)
                            {
                                double mfLum = 0.299 * mfb.Color.R + 0.587 * mfb.Color.G + 0.114 * mfb.Color.B;
                                if (mfLum > 120)
                                    matrixErrors.Add($"Light MascotFooter lum={mfLum:0.0} (>120, color={mfb.Color})");
                            }
                        }

                        // 遍历全站 9 大视图之一级区域：RoundBtn 必无遗漏
                        foreach (string view in allViews)
                        {
                            w.ShowViewForTest(view);
                            w.LayoutForTest(1000, 660);
                            var unrounded = RoundBtn.FindUnroundedForTest(w);
                            if (unrounded.Count > 0)
                                matrixErrors.Add($"Unrounded({dark},{acrylic},{view})={unrounded.Count}: {string.Join(",", unrounded.Take(3))}");
                        }

                        // 凭据双页签分别检查圆角按钮
                        foreach (string credTab in new[] { "providers", "keys" })
                        {
                            w.ShowCredentialsTabForTest(credTab);
                            w.LayoutForTest(1000, 660);
                            var unroundedCred = RoundBtn.FindUnroundedForTest(w);
                            if (unroundedCred.Count > 0)
                                matrixErrors.Add($"UnroundedCred({dark},{acrylic},{credTab})={unroundedCred.Count}: {string.Join(",", unroundedCred.Take(3))}");
                        }
                        w.ShowCredentialsTabForTest("providers");
                    }
                }

                // 验证桌面版设置 → 常规自动隐藏「启动方式」卡片，Web 版恢复可见
                try
                {
                    w.ShowViewForTest("settings");
                    w.ShowSettingsTabForTest("general");
                    w.SetTargetForTest(true);
                    w.LayoutForTest(1000, 660);
                    if (w.FindName("LaunchModeCard") is not Border lmDesk || lmDesk.Visibility != Visibility.Collapsed)
                        matrixErrors.Add("Desktop settings should collapse LaunchModeCard");

                    w.SetTargetForTest(false);
                    w.LayoutForTest(1000, 660);
                    if (w.FindName("LaunchModeCard") is not Border lmWeb || lmWeb.Visibility != Visibility.Visible)
                        matrixErrors.Add("Web settings should show LaunchModeCard");
                }
                finally
                {
                    try { w.SetTargetForTest(false); } catch { }
                    // 恢复初始主题与视图
                    w.SetAcrylicStateForTest(true);
                    w.ApplyThemeForTest(prevDark);
                    w.ShowViewForTest("status");
                    w.LayoutForTest(1000, 660);
                }

                // 验证四角/四边缩放手柄齐全且拖拽增量+最小尺寸钳制准确
                string[] gripNames = { "ResizeLeft", "ResizeRight", "ResizeTop", "ResizeBottom", "ResizeTopLeft", "ResizeTopRight", "ResizeBottomLeft", "ResizeBottomRight" };
                int gripCount = gripNames.Count(n => w.FindName(n) is FrameworkElement);
                if (gripCount != 8)
                    matrixErrors.Add($"ResizeGrips count={gripCount} != 8");

                double origL = w.Left, origT = w.Top, origW = w.Width, origH = w.Height;
                w.ResizeWindowByDeltaForTest("SE", 140, 90);
                if (Math.Abs(w.Width - (origW + 140)) > 1.5 || Math.Abs(w.Height - (origH + 90)) > 1.5)
                    matrixErrors.Add($"Resize SE expected {origW + 140}x{origH + 90}, got {w.Width}x{w.Height}");

                double seRight = w.Left + w.Width, seBottom = w.Top + w.Height;
                w.ResizeWindowByDeltaForTest("NW", 600, 600); // 向内推超最小尺寸，应钳制在 MinWidth(860) x MinHeight(560) 并保持右下锚定
                if (Math.Abs(w.Width - 860) > 1.5 || Math.Abs(w.Height - 560) > 1.5
                    || Math.Abs((w.Left + w.Width) - seRight) > 1.5 || Math.Abs((w.Top + w.Height) - seBottom) > 1.5)
                    matrixErrors.Add($"Resize NW clamp failed: {w.Width}x{w.Height} R={w.Left + w.Width}!={seRight}");

                w.Left = origL;
                w.Top = origT;
                w.Width = origW;
                w.Height = origH;

                check("2.4.0 · 四态矩阵（日/夜 × 透感毛玻璃/全实纯色）与四角缩放：语义透感对比 / 8 向手柄 / 零未圆角按钮全部达标",
                    matrixErrors.Count == 0,
                    matrixErrors.Count == 0 ? "4 modes × 8 views + 8 resize grips OK" : string.Join(" ; ", matrixErrors.Take(6)));
            }

            // ── F24-9: 鲸鸣音频资源完整可读且有效波形达标 ──
            {
                var (readable, samples, peak) = MainWindow.ProbeWhaleCallResourceForTest();
                check("2.4.0 · 左上角鲸鱼切换音效已替换为 Assets/whale-call.wav（可读、时长与峰值有效）",
                    readable && samples > 20000 && peak > 0.25,
                    $"Readable={readable} · Samples={samples} · Peak={peak:0.000}");
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
