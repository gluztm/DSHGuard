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
    $decls = 0
    $calls = 0
    $callAt = New-Object System.Collections.Generic.List[string]
    foreach ($f in $files) {
        $clean = Get-StrippedSource ([IO.File]::ReadAllText($f.FullName))
        $ownerHere = if ($hasOwner) { [regex]::IsMatch($clean, ('(?i)\b(?:class|struct|record)\s+' + [regex]::Escape($owner) + '\b')) } else { $true }
        $lines = $clean.Split("`n")
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $t = $lines[$i].Trim()
            if ($t -match $declPat) {
                if ($ownerHere) { $decls++ }
                continue
            }
            if ($t -notmatch $callPat) { continue }
            $calls++
            [void]$callAt.Add(('{0}:{1}' -f $f.Name, ($i + 1)))
        }
    }
    $ok = ($decls -ge 1) -and ($calls -ge [int]$w.MinCalls)
    [void]$report.Add(('{0,-28} decl={1} calls={2} (min {3}) => {4}' -f `
        $sym, $decls, $calls, $w.MinCalls, $(if ($ok) { 'WIRED' } else { 'NOT WIRED' })))
    foreach ($c in $callAt) { [void]$report.Add(('      call at ' + $c)) }
    if (-not $ok) {
        $why = if ($decls -lt 1) { 'no declaration found' } else { ('production calls ' + $calls + ' < expected ' + $w.MinCalls) }
        [void]$bad.Add(('{0}: {1} ({2})' -f $sym, $why, $w.Why))
    }
}

foreach ($r in $report) { Write-Host $r }
if ($bad.Count -eq 0) {
    Write-Host 'wiring gate: OK'
    exit 0
}
Write-Host 'wiring gate: VIOLATIONS' -ForegroundColor Red
foreach ($b in $bad) { Write-Host ('  - ' + $b) -ForegroundColor Red }
exit 1
