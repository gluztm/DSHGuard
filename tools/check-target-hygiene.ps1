# DSHGuard 2.3.3 target hygiene check (ASCII only: Windows PowerShell 5 reads BOM-less files as ANSI).
#
# Why: 2.0.0 routes every plugin write through the target dispatch layer (MainWindow.PluginOps.cs).
# A new call site that hard-codes the Web CLI (`--profile web` / bare `npx` write) or a new
# "defaults to Web" parameter would silently act on the wrong profile when the Desktop target is selected.
#
# Rules (comments are ignored; SelfTest sources are excluded):
#   1. `--profile web` only inside PluginManager.cs (the Web CLI builders) or allowlisted read paths.
#   2. RunCommandAsync("npx" / RunCommandCancelableAsync("npx" only inside allowlisted methods.
#   3. No `profileDir = null` / `GuardTarget x = GuardTarget.Web` default parameters.
#   4. (2.3.3) No INDIRECT call to a Web CLI builder (PluginManager.BuildUninstallArgs /
#      BuildAddArgs / BuildAddSourceArgs / BuildInstallAllArgs / BuildUpdateArgs) outside the
#      dispatch layer and the allowlisted Web-only methods.
#      Why a fourth rule: rules 1-3 only see literals. Those five builders hide `--profile web`
#      behind a method call, so a desktop target can still be pointed at the web profile.
#      That is the F-03 hole ("profile path mismatch") this rule closes.
#
# Rule 4 also proves that an allowlisted call site is still desktop-safe. A builder call that is
# reachable without any desktop-target check is the exact bug, so every allowlist entry declares
# how it must prove itself:
#   file   = the file must contain a desktop-target token
#   method = the enclosing method body must contain a desktop-target token
#   none   = Web-only by call-site reachability (documented per entry)
#
# Scope notes:
#   - PluginManager.cs is the builder layer itself and is excluded from rule 4; rule 1 governs it.
#   - SelfTest.cs and the SelfTest\ folder are excluded: the assertions name these symbols on purpose.
#
# Exit code: 0 = clean, 1 = violations found.
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

# file|method pairs allowed to run npx / mention --profile web (all are Web-only read or Web-launch paths)
$allow = @(
    'MainWindow.Tools.cs|LoadLoaderIdsAsync',       # --dump-config read; returns early on Desktop
    'MainWindow.Tools.cs|PreflightManifestAsync'    # Web engine launch preflight (install from manifest)
)

# Rule 4 allowlist for the five Web CLI builders. Guard = how the entry proves desktop-safety.
$allowBuilders = @(
    @{ File = 'MainWindow.PluginOps.cs'; Method = ''; Guard = 'file'; Why = 'the target dispatch layer: every plugin write is routed here by target' }
    @{ File = 'MainWindow.Tools.cs'; Method = 'LoadLoaderIdsAsync'; Guard = 'method'; Why = 'loader id read; returns early on Desktop' }
    @{ File = 'MainWindow.Tools.cs'; Method = 'PreflightManifestAsync'; Guard = 'none'; Why = 'Web engine launch preflight; single call site MainWindow.xaml.cs:1938 inside StartEngineAsync, and all four entry points are Desktop-gated: MainWindow.xaml.cs:565 MainButton_Click returns early on Desktop, MainWindow.Restart.cs:91 RestartCurrentTargetAsync returns early on IsDesktop, and the other two callers (:2116 auto-retry, :2419 reset-and-restart) live inside the Web branch itself' }
    @{ File = 'MainWindow.Tools.cs'; Method = 'UninstallPlugin_Click'; Guard = 'method'; Why = 'picks pnpm vs npx per target' }
    @{ File = 'MainWindow.Tools.cs'; Method = 'UpdateArgsFor'; Guard = 'none'; Why = 'single caller is the dispatch layer Web branch' }
)

# production sources only: no build output, no assertions, no VCS metadata
$files = Get-ChildItem -Path $Root -Filter *.cs -File -Recurse |
    Where-Object {
        $_.FullName -notmatch '\\bin\\' -and $_.FullName -notmatch '\\obj\\' -and
        $_.FullName -notmatch '\\SelfTest\\' -and $_.FullName -notmatch '\\\.git\\' -and
        $_.Name -ne 'SelfTest.cs'
    }

$bad = New-Object System.Collections.Generic.List[string]

foreach ($f in $files) {
    $L = [IO.File]::ReadAllText($f.FullName).Split("`n")
    for ($i = 0; $i -lt $L.Length; $i++) {
        $line = $L[$i]
        $trim = $line.Trim()
        if ($trim.StartsWith('//') -or $trim.StartsWith('*')) { continue }

        $hitProfile = $line.Contains('--profile web')
        $hitNpx = $line -match 'Run(Command|CommandCancelable)Async\("npx"'
        $hitDefault = ($line -match 'profileDir = null') -or ($line -match 'GuardTarget \w+ = GuardTarget\.Web\b')

        if ($hitDefault) {
            $bad.Add(('{0}:{1}  default-to-Web parameter: {2}' -f $f.Name, ($i + 1), $trim))
        }
        if (-not ($hitProfile -or $hitNpx)) { continue }
        if ($hitProfile -and -not $hitNpx -and $f.Name -eq 'PluginManager.cs') { continue }

        # enclosing member name
        $method = '?'
        for ($k = $i; $k -ge 0; $k--) {
            if ($L[$k] -match '^ {4}(public|private|internal|protected)[^=;]*?\s(\w+)\s*(<[^>]*>)?\s*\(') { $method = $matches[2]; break }
        }
        # multi-line call: the npx literal may sit on the next line of a RunCommandAsync("npx", ... call
        if ($allow -contains ('{0}|{1}' -f $f.Name, $method)) { continue }
        $bad.Add(('{0}:{1}  [{2}] Web CLI outside dispatch layer: {3}' -f $f.Name, ($i + 1), $method, $trim))
    }
}

# ---------------------------------------------------------------------------
# rule 4: indirect calls to the Web CLI builders (the F-03 hole)
# ---------------------------------------------------------------------------

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

$sigPat = '^ {4}(public|private|internal|protected)[^=;]*?\s(\w+)\s*(<[^>]*>)?\s*\('
# The qualifier is optional on purpose: every source file lives in namespace DSHGuard, so an
# unqualified BuildAddArgs(...) compiles fine and would bypass a prefix-only pattern.
$builderPat = '(?:\w+\.)?(BuildUninstallArgs|BuildAddArgs|BuildAddSourceArgs|BuildInstallAllArgs|BuildUpdateArgs)\s*\('
$guardPat = '\bIsDesktop\b|\bDesktopTarget\b|GuardTarget\s*\.\s*Desktop'

foreach ($f in $files) {
    if ($f.Name -eq 'PluginManager.cs') { continue }   # the builder layer itself: rule 1 governs it
    $clean = Get-StrippedSource ([IO.File]::ReadAllText($f.FullName))
    $lines = $clean.Split("`n")
    $fileHasGuard = ($clean -match $guardPat)

    # method map: every 4-space-indented member signature, with the line range it owns
    $starts = @()
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match $sigPat) { $starts += ,@{ Line = $i; Name = $matches[2] } }
    }

    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -notmatch $builderPat) { continue }

        $mi = -1
        for ($k = $starts.Count - 1; $k -ge 0; $k--) {
            if ($starts[$k].Line -le $i) { $mi = $k; break }
        }
        $method = if ($mi -ge 0) { $starts[$mi].Name } else { '' }
        $hit = $lines[$i].Trim()

        $entry = $null
        foreach ($a in $allowBuilders) {
            if ($a.File -ne $f.Name) { continue }
            if ($a.Method -eq '' -or $a.Method -eq $method) { $entry = $a; break }
        }
        if (-not $entry) {
            $bad.Add(('{0}:{1}  [rule 4] indirect Web CLI builder call outside the dispatch layer: {2}' -f $f.Name, ($i + 1), $hit))
            continue
        }

        if ($entry.Guard -eq 'file') {
            if (-not $fileHasGuard) {
                $bad.Add(('{0}:{1}  [rule 4] allowlisted file lost its desktop guard ({2}): {3}' -f $f.Name, ($i + 1), $entry.Why, $hit))
            }
        }
        elseif ($entry.Guard -eq 'method') {
            $s = $starts[$mi].Line
            $e = if ($mi + 1 -lt $starts.Count) { $starts[$mi + 1].Line - 1 } else { $lines.Length - 1 }
            if ($e -lt $s) { $e = $s }
            $body = ($lines[$s..$e] -join "`n")
            if (-not ($body -match $guardPat)) {
                $bad.Add(('{0}:{1}  [rule 4] allowlisted method {2} lost its desktop guard: {3}' -f $f.Name, ($i + 1), $method, $hit))
            }
        }
    }
}

if ($bad.Count -eq 0) {
    Write-Output 'target hygiene: OK'
    exit 0
}
Write-Output ('target hygiene: {0} violation(s)' -f $bad.Count)
$bad | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output 'Route plugin writes through MainWindow.PluginOps.cs (UpdateCmdFor / InstallCmdFor / UninstallCmdFor / ReinstallCmdFor).'
exit 1
