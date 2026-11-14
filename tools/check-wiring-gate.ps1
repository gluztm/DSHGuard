# DSHGuard 2.3.3 wiring gate (ASCII only: Windows PowerShell 5 reads BOM-less files as ANSI).
#
# Why: a helper that exists but is never called from production code still makes --selftest green.
# That is the exact failure class this repo hit twice while fixing G-04 / P0-3: the assertion tests
# the pure function, the pure function is right, and the real call site was quietly reverted to a
# hard-coded value. A runtime hook cannot see it either, because the cmd fallback branch never runs
# inside --selftest (BuildLaunchSpec always resolves a real exe there).
#
# So this gate is textual and runs BEFORE the build, next to check-target-hygiene.ps1:
#   for every symbol below, production code must (a) declare it and (b) call it at least MinCalls times.
#
# Deliberate exclusions (each one is a false-green we are guarding against):
#   - SelfTest.cs and the SelfTest\ folder: the assertions themselves reference these symbols, so
#     counting them would let a never-called helper pass on the strength of its own test.
#   - bin\ and obj\: build products, including the shipped copies under installer\payload.
#   - comments and string literals: stripped before matching, so a mention in prose is not a call.
#
# Two channels, same verdict: C# symbols below, and PowerShell scripts under tools\.
#
# Exit code: 0 = every symbol is wired, 1 = at least one violation.
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

# Symbol table. Append one row per "must actually be called" judgement; MinCalls is the floor of
# production call sites we expect (a known count also catches a call site silently disappearing).
$wires = @(
    @{ Symbol = 'UninstallEvidenceText';   MinCalls = 2; Why = 'batch and single uninstall must share one evidence entry' }
    @{ Symbol = 'CmdFallbackOutputEncoding';   MinCalls = 1; Why = 'G-04 group B: cmd fallback output encoding' }
    @{ Symbol = 'ReadDecodedAsync';            MinCalls = 4; Why = 'G-04 group A: 2 capture sites x 2 streams (stdout + stderr)' }
    @{ Symbol = 'EnsureCodePagesRegistered';   MinCalls = 1; Why = 'G-04 hard prerequisite: CodePages provider must be registered at startup' }
    # P0-3 (2.3.3): the pnpm bootstrap must actually be reached from the UI. MinCalls=4 because
    # MainWindow.Tools.cs has exactly FOUR PnpmReady call sites and each one is a semantic floor
    # on its own. A plain count cannot tell them apart (the readiness check and the post-adopt
    # re-check are byte-identical text), so only the count pins all four:
    #   1) readiness first check  - "if (NodeReady() && PnpmReady()) return true;" BEFORE
    #                               AdoptUserNodeDir() has run
    #   2) post-adopt re-check    - the same "if (NodeReady() && PnpmReady()) return true;" AFTER
    #                               AdoptUserNodeDir() put the user runtime dir on PATH
    #   3) pnpm-only decision     - "bool pnpmOnly = NodeReady() && !PnpmReady();" selects the
    #                               light pnpm-only install branch
    #   4) post-install re-check  - "if (NodeReady() && PnpmReady())" after install-node.ps1 ran
    # Merging ANY ONE of these four call sites MUST update this MinCalls in the same commit.
    # Do not lower it: MinCalls=2 let two semantic floors be deleted while the gate stayed green.
    @{ Symbol = 'PnpmReady';                   MinCalls = 4; Why = 'P0-3: pnpm bootstrap trigger must stay wired in MainWindow.Tools' }
    # P0-4 (2.3.3): the uninstall guard must inspect, describe AND re-check. Inspect=2 because 1
    # would only catch "whole block deleted", not "the post-terminate re-check removed" -- which is
    # exactly the hole where the uninstaller proceeds while an exe is still running.
    # NOTE the dotted names: bare 'Describe' also matches FileTrust.Describe / PluginSource.Describe
    # / StartupCause.Describe / result.Describe(), which would make MinCalls trivially true (a fake
    # green). A dotted symbol matches calls only through that owner; the declaration is still found
    # by its last segment (see DeclName below).
    @{ Symbol = 'AppSiblings.Inspect';   MinCalls = 2; Why = 'P0-4: first check + re-check after terminate' }
    @{ Symbol = 'AppSiblings.Describe';  MinCalls = 2; Why = 'P0-4: message text + re-check verdict' }
    @{ Symbol = 'AppSiblings.Terminate'; MinCalls = 2; Why = 'P0-4: uninstall path + DesktopSide (one close-logic owner)' }
    # P1 (2.3.3): every GitHub API query must leave through GitApiCache, the one exit that owns
    # route order, the 10-minute success cache, the direct-fail negative cache and the 403/429
    # split. A second hand-rolled exit (it used to live in PluginSource.GetJsonAsync) means one
    # more place with no cache, no negative cache and no rate-limit handling -- and a caller that
    # silently degrades when GitHub is throttling. MinCalls=8 is the observed production count
    # after PluginSource.GetJsonAsync was moved onto GitApiCache; if a caller is merged away,
    # drop the count in the SAME commit that removes the call site.
    @{ Symbol = 'GitApiCache.GetStringAsync'; MinCalls = 8; Why = 'GitHub API queries must go through the single cached exit' }
    # 2.3.4: newly wired safety and diagnostic invariants
    @{ Symbol = 'InjectGitHubMirror';                         MinCalls = 2; MaxCalls = 2; Why = '2.3.4: git clone/install mirror injection must stay wired at both git launch sites' }
    @{ Symbol = 'GitInsteadOfEnv';                            MinCalls = 2; MaxCalls = 2; Why = '2.3.4: GitHubRoute.GitInsteadOfEnv must stay wired for log formatting and git env injection' }
    @{ Symbol = 'FormatInnerChain';                           MinCalls = 2; MaxCalls = 2; Why = '2.3.4: inner exception chain formatter must stay wired in NoteLogUnavailable and LogError' }
    @{ Symbol = 'LooksBrandNew'; File = 'MainWindow.Tools.cs'; MinCalls = 2; MaxCalls = 2; Why = '2.3.4: brand-new machine detection must stay wired in SilentDependencyHint and RunAutoConfigure' }
    @{ Symbol = 'LooksBrandNew'; File = 'MainWindow.xaml.cs';  MinCalls = 1; MaxCalls = 1; Why = '2.3.4: brand-new machine detection must stay wired in ShowStartupFailureDialog' }
    # 2.4.0: semantic token engine + credential vault & CC Switch native SQLite importer
    @{ Symbol = 'SwitchTokenDictionary';               MinCalls = 1; Why = '2.4.0: semantic theme token dictionary swap must stay wired in ThemeManager.Apply' }
    @{ Symbol = 'DshCredentialManager.ScanCredentials'; MinCalls = 1; Why = '2.4.0: credential vault scanner must stay wired in MainWindow.Credentials' }
    @{ Symbol = 'DshCcSwitchImporter.ScanCcSwitchDb';   MinCalls = 1; Why = '2.4.0: native winsqlite3 CC Switch reader must stay wired in MainWindow.Credentials' }
    @{ Symbol = 'PlayWhaleCallSound';                   MinCalls = 1; Why = '2.4.0: whale call sound must stay wired in top-left global switch click' }
    @{ Symbol = 'BlurHelper.SyncThemeChrome';           MinCalls = 2; Why = '2.4.0: DWM theme/corner chrome sync must stay wired in MainWindow backdrop & theme paths' }
    @{ Symbol = 'RefreshCredentialsView';               MinCalls = 1; Why = '2.4.0: standalone Credentials view lazy refresh must stay wired from ShowView' }
)

function Get-StrippedSource([string]$text) {
    # Remove // and block comments plus string/char literals, so only real code tokens remain.
    $out = New-Object System.Text.StringBuilder
    $i = 0
    $n = $text.Length
    while ($i -lt $n) {
        $c = $text[$i]
        $d = if ($i + 1 -lt $n) { $text[$i + 1] } else { [char]0 }
        if ($c -eq '/' -and $d -eq '/') {
            while ($i -lt $n -and $text[$i] -ne "`n") { $i++ }
        }
        elseif ($c -eq '/' -and $d -eq '*') {
            $i += 2
            while ($i + 1 -lt $n -and -not ($text[$i] -eq '*' -and $text[$i + 1] -eq '/')) {
                if ($text[$i] -eq "`n") { [void]$out.Append("`n") }
                $i++
            }
            $i += 2
        }
        elseif ($c -eq '@' -and ($d -eq '"' -or $d -eq "'")) {
            # verbatim string / verbatim char: only "" is an escape
            $i += 2
            while ($i -lt $n) {
                if ($text[$i] -eq '"') {
                    if ($i + 1 -lt $n -and $text[$i + 1] -eq '"') { $i += 2; continue }
                    $i++; break
                }
                if ($text[$i] -eq "`n") { [void]$out.Append("`n") }
                $i++
            }
        }
        elseif ($c -eq '"' -or $c -eq "'") {
            $q = $c
            $i++
            while ($i -lt $n) {
                if ($text[$i] -eq '\') { $i += 2; continue }
                if ($text[$i] -eq $q) { $i++; break }
                if ($text[$i] -eq "`n") { $i++; break }
                $i++
            }
        }
        else {
            [void]$out.Append($c)
            $i++
        }
    }
    return $out.ToString()
}

$bad = New-Object System.Collections.Generic.List[string]
$report = New-Object System.Collections.Generic.List[string]

$files = Get-ChildItem -Path $Root -Filter *.cs -File -Recurse |
    Where-Object {
        $_.FullName -notmatch '\\bin\\' -and $_.FullName -notmatch '\\obj\\' -and
        $_.FullName -notmatch '\\SelfTest\\' -and $_.Name -ne 'SelfTest.cs'
    }

foreach ($w in $wires) {
    $sym = $w.Symbol
    # A dotted symbol ("Owner.Method") is matched in full on the CALL side, so an unrelated member
    # with the same simple name cannot satisfy MinCalls. The DECLARATION side uses the last segment,
    # because a declaration never spells its own owner.
    $last = $sym
    if ($sym.Contains('.')) { $last = $sym.Substring($sym.LastIndexOf('.') + 1) }
    $callPat = '(?i)\b' + [regex]::Escape($sym) + '\s*\('
    $declPat = '(?i)^\s*(public|internal|private|protected)[^;{=]*\b' + [regex]::Escape($last) + '\s*\('
    $hasOwner = $sym.Contains('.')
    $owner = if ($hasOwner) { $sym.Substring(0, $sym.LastIndexOf('.')) } else { '' }
    # A dotted symbol's declaration must sit in a file that declares its owner type, otherwise
    # deleting AppSiblings.Describe still leaves decl>=1 from FileTrust.Describe /
    # PluginSource.Describe / StartupCause.Describe -- a declaration-side fake green.
    # (Brace-depth attribution was tried and is fragile here: this repo puts the opening brace of a
    # type on its own line, so a nested type like AppSiblings.Entry steals the enclosing name.)
    $hasFile = $w.ContainsKey('File')
    $fileFilter = if ($hasFile) { [string]$w.File } else { '' }
    $fileHits = 0
    $decls = 0
    $calls = 0
    $callAt = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $inTargetFile = (-not $hasFile) -or ($f.Name -ieq $fileFilter)
        if ($hasFile -and $inTargetFile) { $fileHits++ }
        $clean = Get-StrippedSource ([IO.File]::ReadAllText($f.FullName))
        $ownerHere = if ($hasOwner) { [regex]::IsMatch($clean, ('(?i)\b(?:class|struct|record)\s+' + [regex]::Escape($owner) + '\b')) } else { $true }
        $lines = $clean.Split("`n")
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $t = $lines[$i].Trim()
            if ($t -match $declPat) {
                if ($ownerHere) { $decls++ }
                continue
            }
            if (-not $inTargetFile) { continue }
            if ($t -notmatch $callPat) { continue }
            $calls++
            [void]$callAt.Add(('{0}:{1}' -f $f.Name, ($i + 1)))
        }
    }
    # Two-sided judgement (2.3.3): a floor alone can never fail a zero-count assertion, so a
    # symbol that must NOT appear (MaxCalls = 0) would stay green forever. The ceiling is
    # OPTIONAL and is read through ContainsKey, because (-not $w.MaxCalls) is True when
    # MaxCalls = 0 - that would skip the ceiling and make the guard a fake green.
    # Rows without a MaxCalls key keep exactly today's behaviour (ContainsKey = False).
    $hasMax = $w.ContainsKey('MaxCalls')
    $fileOk = (-not $hasFile) -or ($fileHits -ge 1)
    $ok = $fileOk -and ($decls -ge 1) -and ($calls -ge [int]$w.MinCalls) -and ((-not $hasMax) -or ($calls -le [int]$w.MaxCalls))
    $symLabel = if ($hasFile) { ('{0} [{1}]' -f $sym, $fileFilter) } else { $sym }
    [void]$report.Add(('{0,-38} decl={1} calls={2} (min {3} max {4}) => {5}' -f `
        $symLabel, $decls, $calls, $w.MinCalls, $(if ($hasMax) { $w.MaxCalls } else { 'n/a' }), $(if ($ok) { 'WIRED' } else { 'NOT WIRED' })))
    foreach ($c in $callAt) { [void]$report.Add(('      call at ' + $c)) }
    if (-not $ok) {
        $why = if (-not $fileOk) { 'target file not found: ' + $fileFilter } elseif ($decls -lt 1) { 'no declaration found' } elseif ($hasMax -and $calls -gt [int]$w.MaxCalls) { ('production calls ' + $calls + ' > ceiling ' + [int]$w.MaxCalls) } else { ('production calls ' + $calls + ' < expected ' + $w.MinCalls) }
        [void]$bad.Add(('{0}: {1} ({2})' -f $symLabel, $why, $w.Why))
    }
}

# --- PowerShell channel (2.3.3, N-1) -------------------------------------------------
# The .cs channel above only sees C#: its file filter is *.cs. The N-1 work lives in
# tools\*.ps1, so "declared but never called" needs a second judgement for PowerShell.
#
# PowerShell 5.1 ships no Roslyn parser, so the tokens come from the parser PS itself
# provides: [System.Management.Automation.PSParser]::Tokenize(). Two facts are load-bearing:
#   - the token text lives in .Content; .Text is always empty.
#   - PS 5.1 emits no Identifier token. In "function Foo(...)" the name is a CommandArgument,
#     and at a call site the same name is a Command. A function row therefore counts
#     CommandArgument + Command together (one definition + one call = 2).
#
# Match discipline (the same rules as the .cs channel, for the same reasons):
#   - member and literal names: exact (-eq). A glob would let Show-Port satisfy *Port*.
#   - URL fragments: wildcard (-like). A URL built by concatenation or -f is spread over
#     several tokens, so -eq would count zero and go red for the wrong reason.
#   - variables: exact on the variable name; never a glob.
#
# A row whose file is not present is reported as SKIP, not as a failure: a row may be written
# ahead of the script that will satisfy it. Rows are appended by the owner of that line.
$scriptWires = @(
    # N-1 (2.3.3): the registry reader must keep its two-call shape.
    # Call 1: one ranged read of /<enc> yields both the latest dist-tag and the time table.
    # Call 2: /<enc>/<version> yields the publish time and the declaration fields.
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-PackageHead'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'N-1: the head reader must be declared and called exactly once' }
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-VersionManifest'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'N-1: the per-version reader must be declared and called exactly once' }
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-DistTagsFromHead'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'N-1: the head dist-tag parser must be declared and called exactly once' }
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-TimeTableBody'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'N-1: the time-table extractor must be declared and called exactly once' }
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-EarliestVersionTime'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'N-1: the created-date scan must be declared and called exactly once' }
    # The head read must stay a RANGED read. A full download of the packument is what made the
    # old reader slow; AddRange is the one call that proves the range survived an edit.
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('Member'); Symbol = 'AddRange'; Match = 'eq'; MinCalls = 1; MaxCalls = 1; Why = 'N-1: the head read must stay a ranged read' }
    # The per-version call must stay on the REST path.
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('Command'); Symbol = 'Invoke-RestMethod'; Match = 'eq'; MinCalls = 1; MaxCalls = 1; Why = 'N-1: the per-version manifest must be fetched over REST' }
    # The whole-package index must NOT come back. Reading .versions means downloading the full
    # packument again (36 MB for billion-context), which is exactly the regression N-1 removed.
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('Member', 'String'); Symbol = 'versions'; Match = 'eq'; MinCalls = 0; MaxCalls = 0; Why = 'N-1: the full package index must not be read again' }
    # The head parser must still look for the dist-tags block.
    @{ File = 'tools/check-plugin-updates.ps1'; Types = @('String'); Symbol = '*dist-tags*'; Match = 'like'; MinCalls = 1; MaxCalls = 1; Why = 'N-1: the head parser must read the dist-tags block' }
    # G-01: port-check must resolve the engine port instead of trusting one hard-coded default.
    @{ File = 'tools/port-check.ps1'; Types = @('String'); Symbol = '*Config\settings.json*'; Match = 'like'; MinCalls = 1; MaxCalls = 1; Why = 'G-01: port resolution must read the app settings file' }
    @{ File = 'tools/port-check.ps1'; Types = @('Command'); Symbol = 'ConvertFrom-Json'; Match = 'eq'; MinCalls = 1; MaxCalls = 1; Why = 'G-01: settings must be parsed as JSON, exactly one call site' }
    @{ File = 'tools/port-check.ps1'; Types = @('Number'); Symbol = '3080'; Match = 'eq'; MinCalls = 1; MaxCalls = 1; Why = 'G-01: exactly one 3080 fallback literal; a second one means the old hard-coded param default came back' }
    # F-02: the WM_SETTINGCHANGE broadcast must survive as one function with two call sites.
    @{ File = 'tools/install-node.ps1'; Types = @('String'); Symbol = '*SendMessageTimeout*'; Match = 'like'; MinCalls = 1; MaxCalls = 1; Why = 'F-02: the broadcast import must survive' }
    @{ File = 'tools/install-node.ps1'; Types = @('Member'); Symbol = 'SendMessageTimeout'; Match = 'eq'; MinCalls = 1; MaxCalls = 1; Why = 'F-02: the broadcast must actually be sent' }
    @{ File = 'tools/install-node.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Notify-EnvironmentChanged'; Match = 'eq'; MinCalls = 3; MaxCalls = 3; Why = 'F-02: one broadcast function called from both PATH write sites' }
    # git hint: git is optional; the hint must fire on both normal exit paths.
    @{ File = 'tools/install-node.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Hint-GitOptional'; Match = 'eq'; MinCalls = 3; MaxCalls = 3; Why = 'git optional hint: one definition called on both exit paths' }
    @{ File = 'tools/install-node.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Add-ToUserPath'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'F-02: one PATH-writer helper with one call site; any new call site must also broadcast' }
    # payload anti-drift: the packaged copy must be re-synced whenever the tools copy changes.
    @{ File = 'installer/payload/Tools/check-plugin-updates.ps1'; Types = @('CommandArgument', 'Command'); Symbol = 'Get-PackageHead'; Match = 'eq'; MinCalls = 2; MaxCalls = 2; Why = 'payload anti-drift: the installer copy must carry the same parser as tools/check-plugin-updates.ps1' }
)

function Get-PsTokens([string]$path) {
    $errs = @()
    return [System.Management.Automation.PSParser]::Tokenize([IO.File]::ReadAllText($path), [ref]$errs)
}

function Test-PsSyntax([string]$path, [string]$label, [System.Collections.Generic.List[string]]$badList) {
    $tokErrs = @()
    $tokens = [System.Management.Automation.PSParser]::Tokenize([IO.File]::ReadAllText($path), [ref]$tokErrs)
    if ($tokErrs -and $tokErrs.Count -gt 0) {
        [void]$badList.Add(('{0}: PSParser tokenize errors={1}' -f $label, $tokErrs.Count))
    }
    $astErrs = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$astErrs)
    if ($astErrs -and $astErrs.Count -gt 0) {
        [void]$badList.Add(('{0}: PowerShell AST parse errors={1}' -f $label, $astErrs.Count))
    }
    return $tokens
}

function Resolve-PsTarget([string]$rootFull, [string]$rel) {
    # Match on the repo-relative path, case-insensitively, so 'tools/x.ps1' and 'Tools/x.ps1'
    # are the same file on a case-insensitive volume and are never counted twice.
    $want = $rel.Replace('\', '/').ToLowerInvariant()
    $all = Get-ChildItem -Path $rootFull -Filter *.ps1 -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\bin\\' -and $_.FullName -notmatch '\\obj\\' }
    foreach ($f in $all) {
        $relFull = $f.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/').ToLowerInvariant()
        if ($relFull -eq $want) { return $f.FullName }
    }
    return $null
}

$rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$psChecked = @{}
$psTokensCache = @{}
foreach ($w in $scriptWires) {
    $label = ('{0}:{1}' -f $w.File, $w.Symbol)
    $target = Resolve-PsTarget $rootFull $w.File
    if (-not $target) {
        [void]$report.Add(('{0,-44} => SKIP (file not found)' -f $label))
        continue
    }
    if (-not $psChecked.ContainsKey($target)) {
        $psChecked[$target] = $true
        $psTokensCache[$target] = Test-PsSyntax $target $w.File $bad
    }
    $tokens = $psTokensCache[$target]
    $count = 0
    foreach ($t in $tokens) {
        if ($w.Types -notcontains $t.Type.ToString()) { continue }
        $c = $t.Content
        if ($w.Match -eq 'like') { if ($c -like $w.Symbol) { $count++ } }
        else { if ($c -eq $w.Symbol) { $count++ } }
    }
    # Same two-sided judgement as the .cs channel: the ceiling is OPTIONAL and is read through
    # ContainsKey, because (-not $w.MaxCalls) is True when MaxCalls = 0 - that would skip the
    # ceiling and make a zero-count guard a fake green.
    $hasMax = $w.ContainsKey('MaxCalls')
    $ok = ($count -ge [int]$w.MinCalls) -and ((-not $hasMax) -or ($count -le [int]$w.MaxCalls))
    [void]$report.Add(('{0,-44} count={1} (min {2} max {3}) => {4}' -f $label, $count, $w.MinCalls, $(if ($hasMax) { $w.MaxCalls } else { 'n/a' }), $(if ($ok) { 'WIRED' } else { 'NOT WIRED' })))
    if (-not $ok) {
        $maxText = if ($hasMax) { $w.MaxCalls } else { 'n/a' }
        [void]$bad.Add(('{0}: count {1} outside [min {2}, max {3}] ({4})' -f $label, $count, $w.MinCalls, $maxText, $w.Why))
    }
}

# --- R2 encoding audit: no BOM => every byte must be < 0x80 -------------------
# A .ps1 holding non-ASCII bytes without a UTF-8 BOM is decoded as ANSI (cp936
# here): CJK text becomes ParserError and the script does not run at all, while a
# lone accent (e.g. e-acute) still runs and silently prints the wrong character.
# Neither Parser::Tokenize (never validates encoding) nor ReadAllText (assumes
# UTF-8) can see this, so it is audited straight from the raw bytes.
# Fire rule: -not $hasBom -and $nonAscii -gt 0. Pure-ASCII files need no BOM.
$encFiles = Get-ChildItem -Path $Root -Filter *.ps1 -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\bin\\' -and $_.FullName -notmatch '\\obj\\' -and $_.FullName -notmatch '\\dist\\' }
$encScanned = 0
foreach ($ef in $encFiles) {
    $encScanned++
    $eb = [IO.File]::ReadAllBytes($ef.FullName)
    $hasBom = ($eb.Length -ge 3 -and $eb[0] -eq 0xEF -and $eb[1] -eq 0xBB -and $eb[2] -eq 0xBF)
    $encNonAscii = 0
    foreach ($b in $eb) { if ($b -ge 0x80) { $encNonAscii++ } }
    if (-not $hasBom -and $encNonAscii -gt 0) {
        $encRel = $ef.FullName.Substring($rootFull.Length).TrimStart('\')
        [void]$bad.Add(('{0}: non-ASCII bytes but no UTF-8 BOM => NOT WIRED ({1} non-ASCII bytes)' -f $encRel, $encNonAscii))
    }
    if (-not $psChecked.ContainsKey($ef.FullName)) {
        $psChecked[$ef.FullName] = $true
        $encRelAst = $ef.FullName.Substring($rootFull.Length).TrimStart('\').Replace('\', '/')
        [void](Test-PsSyntax $ef.FullName $encRelAst $bad)
    }
}
[void]$report.Add(('{0,-44} files={1} (no BOM => ASCII only)' -f 'encoding audit (R2)', $encScanned))

# --- 2.3.4: payload Tools SHA-256 anti-drift verification ----------------------
$payloadToolsDir = Join-Path $Root 'installer\payload\Tools'
if (Test-Path -LiteralPath $payloadToolsDir -PathType Container) {
    $syncScripts = @(
        'clean-logs.ps1',
        'port-check.ps1',
        'check-plugin-updates.ps1',
        'install-node.ps1',
        'install-desktop.ps1'
    )
    $syncOk = 0
    foreach ($sn in $syncScripts) {
        $srcPs = Join-Path (Join-Path $Root 'tools') $sn
        $dstPs = Join-Path $payloadToolsDir $sn
        if (-not (Test-Path -LiteralPath $srcPs -PathType Leaf)) {
            [void]$bad.Add(('payload Tools sync: missing source tools\{0}' -f $sn))
            continue
        }
        if (-not (Test-Path -LiteralPath $dstPs -PathType Leaf)) {
            [void]$bad.Add(('payload Tools sync: missing payload script installer\payload\Tools\{0}' -f $sn))
            continue
        }
        $hSrc = (Get-FileHash -LiteralPath $srcPs -Algorithm SHA256).Hash
        $hDst = (Get-FileHash -LiteralPath $dstPs -Algorithm SHA256).Hash
        if ($hSrc -ne $hDst) {
            [void]$bad.Add(('payload Tools drift: tools\{0} ({1}) != installer\payload\Tools\{0} ({2})' -f $sn, $hSrc.Substring(0, 12), $hDst.Substring(0, 12)))
        } else {
            $syncOk++
        }
    }
    [void]$report.Add(('{0,-44} matched={1}/{2}' -f 'payload Tools anti-drift', $syncOk, $syncScripts.Count))
} else {
    [void]$report.Add(('{0,-44} => SKIP (installer\payload\Tools not built)' -f 'payload Tools anti-drift'))
}

foreach ($r in $report) { Write-Host $r }
if ($bad.Count -eq 0) {
    Write-Host 'wiring gate: OK'
    exit 0
}
Write-Host 'wiring gate: VIOLATIONS' -ForegroundColor Red
foreach ($b in $bad) { Write-Host ('  - ' + $b) -ForegroundColor Red }
exit 1
