# Builds portable, self-contained single-file executables for Windows x64.
# Output: dist\app\TreeVisualizer.exe (desktop app) and dist\cli\treeviz.exe (command line). No .NET install is needed.
# Separate folders keep each executable next to only the files it needs.
# Usage: .\publish.ps1 [-Runtime win-x64|win-arm64]
param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$common = @('-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '--nologo')
dotnet publish (Join-Path $PSScriptRoot 'src/TreeVisualizer.App/TreeVisualizer.App.csproj') @common -o (Join-Path $dist 'app')
if ($LASTEXITCODE) { throw "App publish failed" }
dotnet publish (Join-Path $PSScriptRoot 'src/TreeVisualizer.Cli/TreeVisualizer.Cli.csproj') @common -o (Join-Path $dist 'cli')
if ($LASTEXITCODE) { throw "CLI publish failed" }
# Native libraries ship debug symbols that are not needed to run.
Get-ChildItem $dist -Recurse -Filter *.pdb | Remove-Item

Get-ChildItem $dist -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($dist.Length + 1) } }, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String
