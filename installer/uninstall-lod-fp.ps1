# FirstPersonLoD uninstaller: removes the plugin and puts the game's own VR controller files back.
# BepInEx itself is left in place (other mods may use it). To remove it too, delete winhttp.dll,
# doorstop_config.ini and the BepInEx folder from the game folder.
param([string]$GameDir = '')
$ErrorActionPreference = 'Stop'
function Find-Game {
    $roots = @()
    foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try { $p = Get-ItemProperty -Path $k -ErrorAction Stop; if ($p.SteamPath) { $roots += ($p.SteamPath -replace '/', '\') }; if ($p.InstallPath) { $roots += $p.InstallPath } } catch { }
    }
    $libs = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += ($m.Groups[1].Value -replace '\\\\', '\') } }
    }
    foreach ($l in ($libs | Select-Object -Unique)) { $g = Join-Path $l 'steamapps\common\LegendofDungeon'; if (Test-Path $g) { return $g } }
    return $null
}
try {
    if (!$GameDir) { $GameDir = Find-Game }
    if (!$GameDir) { $GameDir = Read-Host "Paste the Legend of Dungeon game folder" }
    if (!(Test-Path $GameDir)) { throw "Game folder not found: $GameDir" }
    $dll = Join-Path $GameDir 'BepInEx\plugins\FirstPersonLoD.dll'
    if (Test-Path $dll) { Remove-Item $dll -Force; Write-Host "Removed BepInEx\plugins\FirstPersonLoD.dll" } else { Write-Host "Plugin not installed." }
    $vr = Get-ChildItem -Path $GameDir -Recurse -Filter '*.json.orig' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'StreamingAssets\\SteamVR' }
    foreach ($o in $vr) {
        $target = $o.FullName.Substring(0, $o.FullName.Length - 5)
        Copy-Item $o.FullName $target -Force
        Remove-Item $o.FullName -Force
        Write-Host ("Restored " + (Split-Path $target -Leaf))
    }
    # controller files the mod added (the game's own list of controllers, restored above, no longer names them)
    foreach ($n in 'bindings_frame_controller.json', 'bindings_oculus_touch.json') {
        $fs = Get-ChildItem -Path $GameDir -Recurse -Filter $n -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'StreamingAssets\\SteamVR' }
        foreach ($f in $fs) { if (!(Test-Path ($f.FullName + '.orig'))) { Remove-Item $f.FullName -Force; Write-Host "Removed the mod's $n" } }
    }
    Write-Host "Done. The mod's settings file stays at BepInEx\config\FirstPersonLoD.cfg (delete it for a clean slate)." -ForegroundColor Green
}
catch { Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red; exit 1 }
