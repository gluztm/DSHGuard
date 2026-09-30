# DSHGuard 2.0.0 target hygiene check (ASCII only: Windows PowerShell 5 reads BOM-less files as ANSI).
#
# Why: 2.0.0 routes every plugin write through the target dispatch layer (MainWindow.PluginOps.cs).
# A new call site that hard-codes the Web CLI (`--profile web` / bare `npx` write) or a new
# "defaults to Web" parameter would silently act on the wrong profile when the Desktop target is selected.
#
# Rules (comments are ignored; SelfTest sources are excluded):
#   1. `--profile web` only inside PluginManager.cs (the Web CLI builders) or allowlisted read paths.
#   2. RunCommandAsync("npx" / RunCommandCancelableAsync("npx" only inside allowlisted methods.
#   3. No `profileDir = null` / `GuardTarget x = GuardTarget.Web` default parameters.
#
# Exit code: 0 = clean, 1 = violations found.
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

# file|method pairs allowed to run npx / mention --profile web (all are Web-only read or Web-launch paths)
$allow = @(
    'MainWindow.Tools.cs|LoadLoaderIdsAsync',       # --dump-config read; returns early on Desktop
    'MainWindow.Tools.cs|PreflightManifestAsync'    # Web engine launch preflight (install from manifest)
)

$files = Get-ChildItem -Path $Root -Filter *.cs -File |
    Where-Object { $_.Name -ne 'SelfTest.cs' }

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

if ($bad.Count -eq 0) {
    Write-Output 'target hygiene: OK'
    exit 0
}
Write-Output ('target hygiene: {0} violation(s)' -f $bad.Count)
$bad | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output 'Route plugin writes through MainWindow.PluginOps.cs (UpdateCmdFor / InstallCmdFor / UninstallCmdFor / ReinstallCmdFor).'
exit 1
