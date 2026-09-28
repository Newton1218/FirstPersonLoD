# FirstPersonLoD installer / updater / checker for Legend of Dungeon (BepInEx 5).
# Safe to re-run any time: it skips what is already done and re-copies the plugin,
# so it doubles as the updater when a new FirstPersonLoD.dll arrives.
#
# Usage: double-click install.bat (FirstPersonLoD.dll must sit in the same folder).
# The game is found in your Steam libraries; a different folder can be passed as the first argument.

param([string]$GameDir = '')

# Finds Legend of Dungeon in any Steam library (Steam's registry entry + libraryfolders.vdf).
function Find-Game {
    $roots = @()
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $k -ErrorAction Stop
            if ($p.SteamPath) { $roots += ($p.SteamPath -replace '/', '\') }
            if ($p.InstallPath) { $roots += $p.InstallPath }
        } catch { }
    }
    $libs = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') }
        }
    }
    foreach ($l in ($libs | Select-Object -Unique)) {
        $g = Join-Path $l 'steamapps\common\LegendofDungeon'
        if (Test-Path $g) { return $g }
    }
    return $null
}
if (!$GameDir) { $GameDir = Find-Game }
if (!$GameDir) {
    Write-Host "Could not find Legend of Dungeon in your Steam libraries." -ForegroundColor Yellow
    $GameDir = Read-Host "Paste the game folder (Steam > Legend of Dungeon > Manage > Browse local files, copy the address bar)"
}

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

function Step($m) { Write-Host ""; Write-Host "== $m" -ForegroundColor Cyan }

try {
    # ---- 1. Sanity checks -------------------------------------------------
    Step "Checking game folder"
    if (!(Test-Path $GameDir)) { throw "Game folder not found: $GameDir" }
    $exe = Get-ChildItem -Path $GameDir -Filter *.exe |
        Where-Object { $_.Name -notmatch 'UnityCrashHandler|unins' } |
        Sort-Object Length -Descending | Select-Object -First 1
    if (!$exe) { throw "No game exe found in $GameDir" }

    # Read the PE header to learn whether the game is 32 or 64 bit.
    $fs = [IO.File]::OpenRead($exe.FullName)
    try {
        $br = New-Object IO.BinaryReader($fs)
        [void]$fs.Seek(0x3C, 'Begin')
        $peOff = $br.ReadInt32()
        [void]$fs.Seek($peOff + 4, 'Begin')
        $machine = $br.ReadUInt16()
    } finally { $fs.Close() }
    if     ($machine -eq 0x8664) { $arch = 'x64' }
    elseif ($machine -eq 0x014C) { $arch = 'x86' }
    else   { throw ("Unrecognized exe machine type 0x{0:X4} in {1}" -f $machine, $exe.Name) }
    Write-Host "Game exe: $($exe.Name)  ($arch)"

    # ---- 2. BepInEx -------------------------------------------------------
    $winhttp = Join-Path $GameDir 'winhttp.dll'
    if (Test-Path $winhttp) {
        Write-Host "BepInEx already present, skipping download."
    } else {
        Step "Downloading BepInEx 5 ($arch)"
        $zipUrl = $null
        try {
            $rel = Invoke-RestMethod 'https://api.github.com/repos/BepInEx/BepInEx/releases/latest' `
                -Headers @{ 'User-Agent' = 'lod-fp-installer' }
            $asset = $rel.assets | Where-Object {
                $_.name -match ('win_' + $arch + '.*\.zip$') -or $_.name -match ('BepInEx_' + $arch + '_.*\.zip$')
            } | Select-Object -First 1
            if ($asset) {
                Write-Host "  $($rel.tag_name)  $($asset.name)"
                $zipUrl = $asset.browser_download_url
            }
        } catch { Write-Host "  GitHub API lookup failed, using pinned version." -ForegroundColor Yellow }
        if (!$zipUrl) {
            $zipUrl = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_${arch}_5.4.22.0.zip"
            Write-Host "  $zipUrl"
        }
        $zip = Join-Path $env:TEMP 'BepInEx_lod.zip'
        Invoke-WebRequest $zipUrl -OutFile $zip -Headers @{ 'User-Agent' = 'lod-fp-installer' }

        Step "Extracting into game folder"
        Expand-Archive -Path $zip -DestinationPath $GameDir -Force
        Remove-Item $zip -ErrorAction SilentlyContinue
    }

    # ---- 3. Plugin --------------------------------------------------------
    Step "Installing FirstPersonLoD plugin"
    $src = Join-Path $PSScriptRoot 'FirstPersonLoD.dll'
    if (!(Test-Path $src)) {
        throw "FirstPersonLoD.dll not found next to this script ($PSScriptRoot). Put it in the same folder and re-run."
    }
    $plugins = Join-Path $GameDir 'BepInEx\plugins'
    New-Item -ItemType Directory -Force -Path $plugins | Out-Null
    Copy-Item $src (Join-Path $plugins 'FirstPersonLoD.dll') -Force
    Write-Host "Copied FirstPersonLoD.dll -> BepInEx\plugins"

    # ---- 4. Guided verify -------------------------------------------------
    Step "Verify"
    Write-Host "Launch the game from Steam now, get into the tavern, press F6, then quit the game."
    Read-Host "Press Enter here AFTER you have quit the game"

    $log = Join-Path $GameDir 'BepInEx\LogOutput.log'
    if (Test-Path $log) {
        if (Select-String -Path $log -Pattern 'LoD First Person|FirstPersonLoD' -Quiet) {
            Write-Host "Plugin loaded. Done." -ForegroundColor Green
            Write-Host "Config lives at BepInEx\config\FirstPersonLoD.cfg (F7 in game saves current tuning)."
        } else {
            Write-Host "BepInEx ran but the plugin did not load." -ForegroundColor Yellow
            Write-Host "Send this file to whoever gave you the mod: $log"
        }
    } else {
        Write-Host "BepInEx never hooked (no LogOutput.log). Applying the legacy Unity entrypoint fix." -ForegroundColor Yellow
        $cfgDir = Join-Path $GameDir 'BepInEx\config'
        New-Item -ItemType Directory -Force -Path $cfgDir | Out-Null
        $cfg = Join-Path $cfgDir 'BepInEx.cfg'
        if (Test-Path $cfg) {
            (Get-Content $cfg) -replace '^(Type\s*=\s*)Application', '${1}Camera' | Set-Content $cfg
        } else {
            Set-Content $cfg "[Preloader.Entrypoint]`r`nAssembly = UnityEngine.dll`r`nType = Camera`r`nMethod = .cctor`r`n"
        }
        Write-Host "Fix applied (entrypoint Type = Camera). Run the game once more, quit, then re-run this installer to re-check."
        Write-Host "If it STILL fails after that, delete winhttp.dll from the game folder and re-run this installer."
    }
}
catch {
    Write-Host ""
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Message -match 'denied|unauthorized') {
        Write-Host "Looks like a permissions problem. Right-click install.bat and choose Run as administrator." -ForegroundColor Yellow
    }
    exit 1
}
