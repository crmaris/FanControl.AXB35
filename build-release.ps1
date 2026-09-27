# Builds the release zip: both plugin builds, axb35ctl, the docs and a SHA256SUMS file, into dist\.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$version = '1.0.0'
dotnet build -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

$stage = Join-Path $PSScriptRoot "dist\FanControl.AXB35-$version"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\net48", "$stage\net8.0-windows" | Out-Null
Copy-Item src\FanControl.Axb35\bin\Release\net48\FanControl.Axb35.dll "$stage\net48\"
Copy-Item src\FanControl.Axb35\bin\Release\net8.0-windows\FanControl.Axb35.dll "$stage\net8.0-windows\"
Copy-Item src\axb35ctl\bin\Release\net48\axb35ctl.exe, src\axb35ctl\bin\Release\net48\axb35ctl.exe.config $stage
Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md $stage

$sums = Get-ChildItem $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
}
$sums | Set-Content "$stage\SHA256SUMS.txt" -Encoding ascii

$zip = Join-Path $PSScriptRoot "dist\FanControl.AXB35-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
'{0}  {1}' -f (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower(), (Split-Path $zip -Leaf)
