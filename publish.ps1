# Builds portable, self-contained single-file executables for Windows. No .NET install is needed to run them.
#
#   .\publish.ps1 [-Runtime win-x64|win-arm64] [-Version 1.2.3] [-Zip]
#
# Output: dist\<runtime>\TreeVisualizer.exe (desktop app) and dist\<runtime>\treeviz.exe (command line).
# -Zip also writes dist\TreeVisualizer-<runtime>.zip, the release asset that install.ps1 downloads.
param(
    [ValidateSet('win-x64', 'win-arm64')] [string]$Runtime = 'win-x64',
    [string]$Version = '0.0.0-dev',
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$out = Join-Path $dist $Runtime
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

$common = @('-c', 'Release', '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=true',
            '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', "-p:Version=$Version", '--nologo')
foreach ($project in 'src/TreeVisualizer.App/TreeVisualizer.App.csproj', 'src/TreeVisualizer.Cli/TreeVisualizer.Cli.csproj') {
    dotnet publish (Join-Path $PSScriptRoot $project) @common -o $out
    if ($LASTEXITCODE) { throw "Publish failed: $project" }
}
# Native libraries ship debug symbols that are not needed to run.
Get-ChildItem $out -Recurse -Filter *.pdb | Remove-Item
Copy-Item (Join-Path $PSScriptRoot 'LICENSE') $out
Set-Content -Path (Join-Path $out 'VERSION') -Value $Version -NoNewline -Encoding ASCII

if ($Zip) {
    $zipPath = Join-Path $dist "TreeVisualizer-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zipPath -CompressionLevel Optimal
}

Get-ChildItem $dist -Recurse -File | Select-Object @{ n = 'File'; e = { $_.FullName.Substring($dist.Length + 1) } },
    @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String
