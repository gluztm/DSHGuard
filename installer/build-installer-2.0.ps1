param([switch]$SkipPublish)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Push-Location $root
try {
  $gv = Get-Content 'GuardVersion.cs' -Raw -Encoding UTF8
  $ma=[regex]::Match($gv,'Major\s*=\s*(\d+)'); $mi=[regex]::Match($gv,'Minor\s*=\s*(\d+)'); $pa=[regex]::Match($gv,'Patch\s*=\s*(\d+)')
  $ver = "$($ma.Groups[1].Value).$($mi.Groups[1].Value).$($pa.Groups[1].Value)"
  Write-Host "Version: $ver"
  & powershell -NoProfile -ExecutionPolicy Bypass -File 'tools\check-target-hygiene.ps1' -Root $root
  if ($LASTEXITCODE -ne 0) { throw 'Target hygiene failed' }
  $pub='bin\Release\net10.0-windows\win-x64\publish'
  if (-not $SkipPublish) { dotnet publish -c Release --nologo /p:Version=$ver.0; if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' } }
  if (-not (Test-Path "$pub\DSHGuard.exe")) { throw "Missing $pub\DSHGuard.exe" }
  $payload='installer\payload'
  if (Test-Path $payload) { Remove-Item $payload -Recurse -Force }
  New-Item -ItemType Directory -Path "$payload\Tools" -Force | Out-Null
  Copy-Item "$pub\DSHGuard.exe" "$payload\DSHGuard.exe" -Force
  foreach($f in @('clean-logs.ps1','port-check.ps1','check-plugin-updates.ps1','install-node.ps1')) { Copy-Item "$pub\Tools\$f" "$payload\Tools\$f" -Force }
  Copy-Item 'Assets\whale-girl.ico' "$payload\whale-girl.ico" -Force
  $iscc=@("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",'C:\Program Files (x86)\Inno Setup 6\ISCC.exe','C:\Program Files\Inno Setup 6\ISCC.exe') | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $iscc) { throw 'ISCC.exe not found' }
  & $iscc "/DAppVersion=$ver" 'installer\DSHGuard.iss'
  if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
  $setup=Get-Item "dist\DSHGuard-Setup-$ver.exe"
  $hash=(Get-FileHash $setup.FullName -Algorithm SHA256).Hash
  Write-Host "Setup: $($setup.FullName)"
  Write-Host "SizeMB: $([math]::Round($setup.Length/1MB,1))"
  Write-Host "SHA256: $hash"
} finally { Pop-Location }
