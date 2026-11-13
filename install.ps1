# Tree Visualizer installer for Windows.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -c "irm https://raw.githubusercontent.com/hypertetrahedron/tree-visualizer/main/install.ps1 | iex"
#
# Installs the latest release for the current user (no admin rights needed):
#   - files in %LOCALAPPDATA%\Programs\TreeVisualizer (TreeVisualizer.exe desktop app, treeviz.exe command line)
#   - that folder added to the user PATH, so `treeviz` works in new terminals
#   - a Start menu shortcut, and an entry in Settings > Apps for uninstalling
# Running it again upgrades in place. Every download is checked against the release's SHA256SUMS.txt.
#
# Optional environment variables:
#   TREEVIZ_VERSION   release tag to install, e.g. v0.1.0 (default: latest)
#   TREEVIZ_BASE_URL  folder URL holding the release assets (for mirrors or testing)
#   TREEVIZ_UNINSTALL set to 1 to uninstall

function Install-TreeVisualizer {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # the progress bar makes downloads very slow in Windows PowerShell
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo = 'hypertetrahedron/tree-visualizer'
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\TreeVisualizer'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Tree Visualizer.lnk'
    $uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\TreeVisualizer'

    if ($env:TREEVIZ_UNINSTALL -eq '1') {
        $script = Join-Path $installDir 'uninstall.ps1'
        if (Test-Path $script) { & $script } else { Write-Host 'Tree Visualizer is not installed.' }
        return
    }

    # Pick the build for this machine. PROCESSOR_ARCHITEW6432 is set when 32-bit PowerShell runs on 64-bit Windows.
    $arch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    $rid = switch ($arch) {
        'AMD64' { 'win-x64' }
        'ARM64' { 'win-arm64' }
        default { throw "Unsupported processor architecture '$arch'. Tree Visualizer needs 64-bit Windows (x64 or ARM64)." }
    }

    $base = if ($env:TREEVIZ_BASE_URL) { $env:TREEVIZ_BASE_URL.TrimEnd('/') }
            elseif ($env:TREEVIZ_VERSION) { "https://github.com/$repo/releases/download/$($env:TREEVIZ_VERSION)" }
            else { "https://github.com/$repo/releases/latest/download" }
    $asset = "TreeVisualizer-$rid.zip"

    if (Get-Process -Name TreeVisualizer -ErrorAction SilentlyContinue) {
        throw 'Tree Visualizer is running. Close it and run the installer again.'
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ("treeviz-install-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        Write-Host "Downloading $asset ..."
        $zip = Join-Path $temp $asset
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$asset" -OutFile $zip
        $sums = (Invoke-WebRequest -UseBasicParsing -Uri "$base/SHA256SUMS.txt").Content
        if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }

        # Verify the download before touching the existing installation.
        $expected = $null
        foreach ($line in ($sums -split "`r?`n")) {
            $parts = $line.Trim() -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].TrimStart('*') -eq $asset) { $expected = $parts[0].ToLowerInvariant() }
        }
        if (-not $expected) { throw "SHA256SUMS.txt has no entry for $asset." }
        $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "Checksum mismatch for $asset (expected $expected, got $actual). Nothing was installed." }

        $staging = Join-Path $temp 'files'
        Expand-Archive -Path $zip -DestinationPath $staging
        $version = if (Test-Path (Join-Path $staging 'VERSION')) { (Get-Content (Join-Path $staging 'VERSION') -Raw).Trim() } else { 'unknown' }

        if (Test-Path $installDir) { Get-ChildItem $installDir -Force | Remove-Item -Recurse -Force }
        else { New-Item -ItemType Directory -Path $installDir | Out-Null }
        Copy-Item -Path (Join-Path $staging '*') -Destination $installDir -Recurse -Force
    }
    finally {
        Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Add the install folder to the user PATH. Read and write the raw registry value so entries such as
    # %USERPROFILE%\bin stay unexpanded (REG_EXPAND_SZ).
    $envKey = Get-Item 'HKCU:\Environment'
    $userPath = $envKey.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    $entries = @($userPath -split ';' | Where-Object { $_ })
    if ($entries -notcontains $installDir) {
        $newPath = (($entries + $installDir) -join ';')
        Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $newPath -Type ExpandString
        # Setting any user variable through .NET broadcasts the change so new terminals pick up the PATH.
        [Environment]::SetEnvironmentVariable('TREEVIZ_INSTALL_BROADCAST', '1', 'User')
        [Environment]::SetEnvironmentVariable('TREEVIZ_INSTALL_BROADCAST', $null, 'User')
    }
    if (($env:Path -split ';') -notcontains $installDir) { $env:Path = "$env:Path;$installDir" }

    # Start menu shortcut.
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = Join-Path $installDir 'TreeVisualizer.exe'
    $lnk.WorkingDirectory = $installDir
    $lnk.Description = 'Tree Visualizer'
    $lnk.Save()

    # Uninstaller, registered in Settings > Apps (per user, no admin rights).
    $uninstall = @'
$ErrorActionPreference = 'Stop'
$installDir = '__DIR__'
if (Get-Process -Name TreeVisualizer -ErrorAction SilentlyContinue) { throw 'Close Tree Visualizer first.' }
$envKey = Get-Item 'HKCU:\Environment'
$userPath = $envKey.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
$kept = @($userPath -split ';' | Where-Object { $_ -and $_ -ne $installDir }) -join ';'
Set-ItemProperty -Path 'HKCU:\Environment' -Name Path -Value $kept -Type ExpandString
[Environment]::SetEnvironmentVariable('TREEVIZ_INSTALL_BROADCAST', '1', 'User')
[Environment]::SetEnvironmentVariable('TREEVIZ_INSTALL_BROADCAST', $null, 'User')
Remove-Item '__LNK__' -Force -ErrorAction SilentlyContinue
Remove-Item 'Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Uninstall\TreeVisualizer' -Recurse -Force -ErrorAction SilentlyContinue
Set-Location $env:TEMP
Remove-Item $installDir -Recurse -Force
Write-Host 'Tree Visualizer was uninstalled.'
'@
    $uninstall = $uninstall.Replace('__DIR__', $installDir.Replace("'", "''")).Replace('__LNK__', $shortcut.Replace("'", "''"))
    $uninstallPath = Join-Path $installDir 'uninstall.ps1'
    Set-Content -Path $uninstallPath -Value $uninstall -Encoding UTF8

    New-Item -Path $uninstallKey -Force | Out-Null
    $props = @{
        DisplayName     = 'Tree Visualizer'
        DisplayVersion  = $version
        Publisher       = 'hypertetrahedron'
        InstallLocation = $installDir
        DisplayIcon     = (Join-Path $installDir 'TreeVisualizer.exe')
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallPath`""
        URLInfoAbout    = "https://github.com/$repo"
    }
    foreach ($k in $props.Keys) { Set-ItemProperty -Path $uninstallKey -Name $k -Value $props[$k] }
    Set-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -Type DWord

    Write-Host ""
    Write-Host "Tree Visualizer $version installed to $installDir"
    Write-Host "  Desktop app: Start menu > Tree Visualizer"
    Write-Host "  Command line: open a new terminal and run 'treeviz --help'"
    Write-Host "  Uninstall: Settings > Apps > Tree Visualizer"
}

Install-TreeVisualizer
