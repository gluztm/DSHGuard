; DSH 守护壳 安装包脚本（Inno Setup 6）
; 构建：installer\build-installer.ps1（会自动做 publish、收集 payload、调用 ISCC）
; 产物：dist\DSHGuard-Setup-<版本>.exe（自带标准卸载器 unins000.exe）

#ifndef AppVersion
  #define AppVersion "0.0"
#endif

#define AppName "DSH 守护壳"
#define AppExe "DSHGuard.exe"
#define AppPublisher "DSHGuard"
; 与 RegistryHelper.AppName 保持一致：安装包与程序内开关写同一个 HKCU\Run 项
#define RunValueName "DSHGuard"

[Setup]
; 固定的 AppId：升级/重装认同一份安装，不会出现两个卸载条目
; 验收专用：ISCC /DProbeBuild=1 产出另一个 AppId 的探针包，装/卸它都不会碰到真实安装的
; 注册表项与快捷方式（同 AppId 的探针卸载过一次真实安装，教训见版本 1.5 的交接文档）。
#ifdef ProbeBuild
AppId={{2C7B31E4-90A5-4E7D-9F1B-3D6E8A4C5B02}
#else
AppId={{9E1F7C34-5B2A-4D18-9C77-6A0E5D3B21F4}
#endif

AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
; 默认装到当前用户的程序目录（任何机器都存在、且一定有写权限）；安装时可以改
; 探针包（验收专用）固定装到专属目录：绝不能落进真实安装目录，
; 否则同一目录会出现两套卸载器（unins000 / unins001），现场已踩过。
#ifdef ProbeBuild
DefaultDirName={userpf}\DSHGuard-Probe
#else
DefaultDirName={userpf}\DSHGuard
#endif
DisableDirPage=no
DefaultGroupName={#AppName}
AllowNoIcons=yes
; 图标与主程序同一个文件：安装程序、卸载器、快捷方式都用它
SetupIconFile=payload\whale-girl.ico
; 用户级安装：不弹 UAC；程序把配置/日志/快照写在 exe 旁边，因此必须装到用户可写目录
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
OutputDir=..\dist
#ifdef ProbeBuild
OutputBaseFilename=DSHGuard-Setup-{#AppVersion}-probe
#else
OutputBaseFilename=DSHGuard-Setup-{#AppVersion}
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupLogging=yes
; 卸载/安装时**不要**去关闭或重启正在运行的程序：本程序有自己的"运行中就拦住"检查，
; 交给 Inno 的自动关闭会表现为"卸载器卡住等进程退出"（探针实测）。
CloseApplications=no
RestartApplications=no

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "installnode"; Description: "安装运行环境（Node.js 长期支持版，约 100 MB 下载，免管理员）"; GroupDescription: "首次安装建议保留："; Check: NodeMissing
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："
Name: "autostart"; Description: "开机自动启动本程序（之后可随时在程序内更改）"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
; 图标与 logo 已内嵌进 exe（pack URI 资源）；动画素材已整体移除，安装包只带 exe + 外部脚本
Source: "payload\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\Tools\*.ps1"; DestDir: "{app}\Tools"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
; 「卸载」走主程序自己的界面（--uninstall）：外观与主程序一致、文案更短
Name: "{group}\卸载 {#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 开机自启：与程序内开关写同一处（HKCU\Run\DSHGuard），避免两套状态打架
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#RunValueName}"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; 首次安装：缺少运行环境时先行安装，之后用户点「一键启动引擎」即可使用
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\Tools\install-node.ps1"""; \
  StatusMsg: "正在安装运行环境（Node.js），请稍候…"; Flags: waituntilterminated; Tasks: installnode

[Code]

var
  DataForm: TSetupForm;
  TaskFolder: TNewCheckBox;
  UninstallMode: String;
  ProceedUninstall: Boolean;

{ ── 安装：路径"建议"而非封堵 ──
  返回 0=没问题 / 1=在系统盘 / 2=系统目录、系统目录的截断名（C:\Program）、或裸盘根。
  任何路径都允许装：只在可能有麻烦时提醒一次并再确认（静默安装取默认值放行，不阻塞脚本）。 }
function PathVerdict(const dir: string): Integer;
var
  d, sys: string;
  i: Integer;
  under: array[0..2] of string;
begin
  Result := 0;
  d := LowerCase(Trim(dir));
  while (Length(d) > 0) and (d[Length(d)] = '\') do Delete(d, Length(d), 1);
  if Length(d) <= 3 then begin Result := 2; exit; end;                  { 裸盘根 }

  under[0] := LowerCase(ExpandConstant('{commonpf}'));
  under[1] := LowerCase(ExpandConstant('{commonpf32}'));
  under[2] := LowerCase(ExpandConstant('{win}'));
  for i := 0 to 2 do
  begin
    sys := under[i];
    while (Length(sys) > 0) and (sys[Length(sys)] = '\') do Delete(sys, Length(sys), 1);
    if (d = sys) or (Pos(sys + '\', d) = 1) then begin Result := 2; exit; end;
    { 系统目录名的截断：C:\Program 是 C:\Program Files 的开头（截断点在空格处，不带反斜杠） }
    if (Length(d) > 3) and (Length(d) < Length(sys)) and (Pos(d, sys) = 1) then begin Result := 2; exit; end;
  end;

  sys := LowerCase(ExpandConstant('{win}'));
  if (Length(d) >= 2) and (Copy(d, 1, 2) = Copy(sys, 1, 2)) then Result := 1;   { 落在系统盘 }
end;

function ConfirmWarnedDir(const dir: string): Boolean;
var
  v: Integer;
  msg: string;
begin
  v := PathVerdict(dir);
  if v = 0 then begin Result := True; exit; end;
  if v = 2 then
    msg := '这个位置是系统目录，或者是它的"截断名"（比如 C:\Program）——可能没有写入权限，也可能和 Windows 自己的目录撞名。' + #13#10#13#10
  else
    msg := '建议装到非系统盘（例如 D 盘）：装到 C 盘的话，系统盘做还原或出问题时这份程序也会受影响。' + #13#10#13#10;
  msg := msg + '仍然要装到下面这个位置吗？' + #13#10 + dir;
  Result := SuppressibleMsgBox(msg, mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

{ 从命令行里取出 /DIR 的值（仅在需要手工排查时使用；正常走官方的 param:DIR 展开） }
function DirParamFromCmdLine: string;
var
  tail, s: string;
  p, q: Integer;
begin
  Result := '';
  tail := GetCmdTail;
  p := Pos('/dir=', LowerCase(tail));
  if p = 0 then exit;
  s := Copy(tail, p + 5, Length(tail));
  if (Length(s) > 0) and (s[1] = '"') then
  begin
    Delete(s, 1, 1);
    q := Pos('"', s);
    if q > 0 then s := Copy(s, 1, q - 1);
  end
  else
  begin
    q := Pos(' ', s);
    if q > 0 then s := Copy(s, 1, q - 1);
  end;
  Result := s;
end;

{ 是否缺少运行环境（Node.js）：决定「安装运行环境」任务默认是否勾选 }
function NodeMissing: Boolean;
begin
  if FileExists(ExpandConstant('{localappdata}\Programs\nodejs\node.exe')) then
    Result := False
  else
    Result := FileSearch('node.exe', GetEnv('PATH')) = '';
end;

function InitializeSetup(): Boolean;
var
  dir: string;
begin
  Result := True;
  { 静默安装不走向导页，这里补一句提醒：只看命令行 /DIR（InitializeSetup 阶段 WizardForm 还没创建，
    用 WizardDirValue 会以"内部错误"直接崩掉安装）。用官方参数展开，保证与 Inno 自身的解析一致。
    交互模式下由目录页的检查负责，避免同一件事问两遍。 }
  dir := ExpandConstant('{param:DIR|}');
  if (dir <> '') and WizardSilent and (not ConfirmWarnedDir(dir)) then
    Result := False;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    { 只提醒、不封堵：点「是」继续安装，点「否」留在本页重新选 }
    if not ConfirmWarnedDir(WizardDirValue) then Result := False;
  end;
end;

{ ── 卸载：三种方式（与程序内「--uninstall」界面完全一致）──
   /MODE=all|cache|app 优先；兼容老参数 /DELETE_DATA=Config,Snapshots,Logs,Cache }
function ModeFromCmdLine: String;
var
  tail: String;
begin
  tail := LowerCase(GetCmdTail);
  if Pos('/mode=all', tail) > 0 then Result := 'all'
  else if Pos('/mode=cache', tail) > 0 then Result := 'cache'
  else if Pos('/mode=data', tail) > 0 then Result := 'data'
  else if Pos('/mode=app', tail) > 0 then Result := 'app'
  else if Pos('delete_data', tail) > 0 then
  begin
    { 老参数兼容：明确提到设置/快照/日志才算"全部清空"，其余一律按最保守的"只删主程序" }
    if (Pos('config', tail) > 0) or (Pos('snapshots', tail) > 0) or (Pos('logs', tail) > 0) then
      Result := 'all'
    else
      Result := 'app';
  end
  else Result := '';         { 命令行**没指定**就返回空 —— 绝不能返回 'app'：
                               本函数的返回值同时被 DataRequestedOnCommandLine 用来判断
                               "是否有人在命令行上做了选择"，一返回 'app' 就等于谎报
                               "命令行已指定"，询问页会被整个跳过（现场 bug：卸载时看不到选项）。
                               "认不出就按最保守执行"这条放到真正干活的地方去兜底。 }
end;


function DataRequestedOnCommandLine: Boolean;
begin
  Result := ModeFromCmdLine <> '';
end;



{ 先声明后使用（Pascal 要求） }
procedure LaunchSilentUninstall(const mode: String); forward;


{ 主程序还在跑就绝不允许卸载：以前从「设置 → 应用」卸载（走 unins000.exe）没有任何检查，
  结果正在运行的 exe 删不掉、其它文件却被清空，留下一个半死不拉的安装（现场 bug）。
  判据用主程序的单实例互斥体，跟程序内 --uninstall 的检查同源。 }
function GuardRunning: Boolean;
begin
  Result := CheckForMutexes('DSHGuard-SingleInstance');
end;
{ 卸载：两个勾选翻成模式。勾选框不挂事件 ⇒ 不存在自我递归。
    勾"连文件夹删除" → all；只勾"删除用户文件" → data；都不勾 → app }
procedure GoUninstallClick(Sender: TObject);
begin
  if TaskFolder.Checked then UninstallMode := 'all'
  else UninstallMode := 'app';
  ProceedUninstall := True;
  DataForm.Close;
end;

{ 取消：什么都不做（叉号与 Esc 同路） }
procedure CancelUninstallClick(Sender: TObject);
begin
  ProceedUninstall := False;
  DataForm.Close;
end;
function InitializeUninstall(): Boolean;
var
  Hint, Sub, Body, Body2: TNewStaticText;
  GoBtn, CancelBtn: TNewButton;
begin
  { 静默卸载、或命令行已指定（程序内「卸载」会带 /MODE=）：不弹窗口 }
  if UninstallSilent or DataRequestedOnCommandLine then
  begin
    Result := True;
    exit;
  end;

  { 主程序还在跑就先拦住 }
  if GuardRunning then
  begin
    SuppressibleMsgBox('DSH 守护壳 正在运行。请先从托盘退出本程序，再回来卸载。',
      mbError, MB_OK, IDOK);
    Result := False;
    exit;
  end;

  { 卸载页：仿安装向导的「选择附加任务」页——标题 + 说明 + 任务勾选 + 卸载/取消。
    只有一个出口叫「取消」（不再有"否/取消"这种同义重复）；叉号与 Esc 也走取消。 }
  Result := False;

  { 版面照安装向导那一页来：白底 + 大标题 + 副标题 + 说明 + 附加任务 + 右下角按钮，
    字号、行距、按钮位置都跟安装程序保持一致（与安装程序保持一致）。 }
  DataForm := CreateCustomForm(ScaleX(520), ScaleY(320), False, True);
  DataForm.Caption := '卸载 DSH 守护壳';
  DataForm.BorderStyle := bsDialog;
  DataForm.Position := poScreenCenter;
  DataForm.Color := clWhite;

  Hint := TNewStaticText.Create(DataForm);
  Hint.Parent := DataForm;
  Hint.Left := 21;
  Hint.Top := 20;
  Hint.Width := DataForm.ClientWidth - 42;
  Hint.Caption := '卸载 DSH 守护壳';
  Hint.Font.Size := 12;
  Hint.Font.Style := [fsBold];

  Sub := TNewStaticText.Create(DataForm);
  Sub.Parent := DataForm;
  Sub.Left := 21;
  Sub.Top := 52;
  Sub.Width := DataForm.ClientWidth - 42;
  Sub.Caption := '您想要卸载程序执行哪些附加任务？';
  Sub.Font.Size := 9;

  Body := TNewStaticText.Create(DataForm);
  Body.Parent := DataForm;
  Body.Left := 21;
  Body.Top := 82;
  Body.Width := DataForm.ClientWidth - 42;
  Body.WordWrap := True;
  Body.Caption := '选择您想要在卸载 DSH 守护壳 时执行的附加任务，然后点击「卸载」。';
  Body.Font.Size := 9;

  Body2 := TNewStaticText.Create(DataForm);
  Body2.Parent := DataForm;
  Body2.Left := 21;
  Body2.Top := 124;
  Body2.Width := DataForm.ClientWidth - 42;
  Body2.Caption := '附加任务：';
  Body2.Font.Size := 9;
  Body2.Font.Style := [fsBold];
  Body2.Font.Style := [fsBold];

  { 只留一个附加任务：勾上=彻底清空（数据 + 程序文件夹）；不勾=只卸载程序、数据全留。
    两个勾选会变成"两个选项一个意思"，现场反馈过，所以这里只留一个。 }
  TaskFolder := TNewCheckBox.Create(DataForm);
  TaskFolder.Parent := DataForm;
  TaskFolder.Left := 37;
  TaskFolder.Top := 152;
  TaskFolder.Width := DataForm.ClientWidth - 58;
  TaskFolder.Caption := '同时删除设置、快照、日志与程序文件夹（彻底清空）';
  TaskFolder.Font.Size := 9;

  GoBtn := TNewButton.Create(DataForm);
  GoBtn.Parent := DataForm;
  GoBtn.Caption := '卸载';
  GoBtn.Width := ScaleX(100);
  GoBtn.Height := ScaleY(30);
  GoBtn.Left := DataForm.ClientWidth - ScaleX(100) - ScaleX(124);
  GoBtn.Top := DataForm.ClientHeight - ScaleY(30) - ScaleY(18);
  GoBtn.OnClick := @GoUninstallClick;
  GoBtn.Default := True;

  CancelBtn := TNewButton.Create(DataForm);
  CancelBtn.Parent := DataForm;
  CancelBtn.Caption := '取消';
  CancelBtn.Width := ScaleX(100);
  CancelBtn.Height := ScaleY(30);
  CancelBtn.Left := DataForm.ClientWidth - ScaleX(100) - ScaleX(18);
  CancelBtn.Top := DataForm.ClientHeight - ScaleY(30) - ScaleY(18);
  CancelBtn.OnClick := @CancelUninstallClick;
  CancelBtn.Cancel := True;

  UninstallMode := '';
  ProceedUninstall := False;
  DataForm.ShowModal;

  if not ProceedUninstall then exit;   { 取消 / 叉号 / Esc：什么都不做 }

  if UninstallMode = '' then UninstallMode := 'app';
  LaunchSilentUninstall(UninstallMode);
  Result := False;
end;
procedure DeleteDirIf(const on: Boolean; const sub: string);
var
  dir: string;
begin
  if not on then exit;
  dir := ExpandConstant('{app}\') + sub;
  if DirExists(dir) then
    DelTree(dir, True, True, True);
end;

{ 延时拉起"静默卸载"：本实例要先退出（unins000 会把自己复制到临时目录再跑，双实例并行不安全），
  所以交给 cmd 等两秒后再启动；静默模式不会再弹任何确认框。 }
procedure LaunchSilentUninstall(const mode: String);
var
  exe, args: String;
  code: Integer;
begin
  exe := ExpandConstant('{uninstallexe}');
  args := '/SILENT /NORESTART';
  if mode <> '' then
    args := args + ' /MODE=' + mode;
  Exec(ExpandConstant('{cmd}'), '/C ping -n 3 127.0.0.1 >NUL & "' + exe + '" ' + args,
       '', SW_HIDE, ewNoWait, code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  mode: String;
  ResultCode: Integer;
begin
  mode := ModeFromCmdLine;
  if mode = '' then mode := 'app';   { 命令行没指定（交互页会带 /MODE= 重新进来）：按最保守的"只删主程序" }

  if CurUninstallStep = usUninstall then
  begin
    { 数据目录：全部清空=五个都删；删除缓存=只删 Cache；只删主程序=不删 }
    if mode = 'all' then
    begin
      DeleteDirIf(True, 'Cache');
      DeleteDirIf(True, 'Config');
      DeleteDirIf(True, 'Logs');
      DeleteDirIf(True, 'Snapshots');
      DeleteDirIf(True, 'Tools');
    end
    else if mode = 'data' then
    begin
      { 删数据、保留程序文件夹 }
      DeleteDirIf(True, 'Cache');
      DeleteDirIf(True, 'Config');
      DeleteDirIf(True, 'Logs');
      DeleteDirIf(True, 'Snapshots');
      DeleteDirIf(True, 'Tools');
    end
    else if mode = 'cache' then
      DeleteDirIf(True, 'Cache');
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { 收尾清理必须延迟做：卸载器自己还在运行，程序目录下的 unins000.dat 正被它占用，
      当场 DelTree 会以"另一个程序正在使用此文件（错误 32）"失败（探针实测）。
      改成让卸载器先退出、再由一个隐藏 cmd 补刀：
        · 全部删除 → 整个程序目录 rmdir
        · 只卸载   → 只清掉卸载器自己留下的 unins000.dat，数据一律保留 }
    if mode = 'all' then
      Exec('cmd.exe', '/c ping -n 4 127.0.0.1 >nul & rmdir /s /q "' + ExpandConstant('{app}') + '"',
        '', SW_HIDE, ewNoWait, ResultCode)
    else
      Exec('cmd.exe', '/c ping -n 4 127.0.0.1 >nul & del /f /q "' + ExpandConstant('{app}\unins000.dat') + '"',
        '', SW_HIDE, ewNoWait, ResultCode);
  end;
end;
