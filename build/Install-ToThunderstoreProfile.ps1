param(
    [string]$ProfileName = "test",
    [string]$BuildConfiguration = "Release",
    [string]$SourceOggPath = "",
    [string]$SourceSpawnConfigPath = ""
)

$ErrorActionPreference = "Stop"

function Get-SoundFileNamesFromReplacerJsons {
    param([string]$ReplacersDirectory)
    $set = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($f in Get-ChildItem -LiteralPath $ReplacersDirectory -Filter *.json -File) {
        $obj = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($null -eq $obj.replacements) { continue }
        foreach ($rep in $obj.replacements) {
            if ($null -eq $rep.sounds) { continue }
            foreach ($s in $rep.sounds) {
                $sn = $s.sound
                if (-not [string]::IsNullOrWhiteSpace($sn)) {
                    [void]$set.Add($sn.Trim())
                }
            }
        }
    }
    return @($set)
}

$workspaceRoot = Split-Path $PSScriptRoot -Parent
$dllPath = Join-Path $workspaceRoot "bin\$BuildConfiguration\net48\NobleMod.dll"
$pluginInfoPath = Join-Path $workspaceRoot "src\PluginInfo.cs"
$soundPackSource = Join-Path $workspaceRoot "content\sound-pack"
$soundPackTemplate = Join-Path $soundPackSource "sound_pack.json"
$replacersSource = Join-Path $soundPackSource "replacers"

if ([string]::IsNullOrWhiteSpace($SourceSpawnConfigPath)) {
    $spawnConfigSourcePath = Join-Path $workspaceRoot "content\spawn"
}
else {
    $spawnConfigSourcePath = $SourceSpawnConfigPath
}

if ([string]::IsNullOrWhiteSpace($SourceOggPath)) {
    $oggSource = Join-Path $workspaceRoot "content\sound-pack\sounds"
}
else {
    $oggSource = $SourceOggPath
}

$profilesRoot = Join-Path $env:APPDATA "Thunderstore Mod Manager\DataFolder\REPO\profiles"
$profileRoot = Join-Path $profilesRoot $ProfileName
$pluginsRoot = Join-Path $profileRoot "BepInEx\plugins"
$modRoot = Join-Path $pluginsRoot "NobleMod"
$targetSoundsPath = Join-Path $modRoot "sounds"
$targetReplacersPath = Join-Path $modRoot "replacers"
$targetSpawnConfigPath = Join-Path $modRoot "SpawnConfig"

if (-not (Test-Path $profileRoot)) {
    throw "Thunderstore profile not found: $profileRoot"
}

if (-not (Test-Path $dllPath)) {
    throw "Missing DLL: $dllPath (compile first with: dotnet build -c $BuildConfiguration)"
}

if (-not (Test-Path -LiteralPath $soundPackTemplate)) {
    throw "Missing content/sound-pack/sound_pack.json: $soundPackTemplate"
}
if (-not (Test-Path -LiteralPath $replacersSource -PathType Container)) {
    throw "Missing content/sound-pack/replacers: $replacersSource"
}

if (-not (Test-Path -LiteralPath $oggSource -PathType Container)) {
    throw "Missing folder with .ogg files: $oggSource (workspace content/sound-pack/sounds/ or -SourceOggPath)."
}

$mappedFiles = Get-SoundFileNamesFromReplacerJsons -ReplacersDirectory $replacersSource
if ($mappedFiles.Count -eq 0) {
    throw "content/sound-pack/replacers: aucun 'sound' dans les JSON."
}

foreach ($soundFile in $mappedFiles) {
    $soundPath = Join-Path $oggSource $soundFile
    if (-not (Test-Path -LiteralPath $soundPath)) {
        throw "Fichier son manquant pour install: $soundPath"
    }
}

$pluginInfoText = Get-Content -LiteralPath $pluginInfoPath -Raw -Encoding UTF8
if ($pluginInfoText -notmatch 'Version\s*=\s*"([^"]+)"') {
    throw "Could not parse PluginInfo.Version in $pluginInfoPath"
}
$version = $Matches[1]

if (Test-Path $modRoot) {
    Remove-Item -LiteralPath $modRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $targetSoundsPath | Out-Null
New-Item -ItemType Directory -Force -Path $targetReplacersPath | Out-Null
New-Item -ItemType Directory -Force -Path $targetSpawnConfigPath | Out-Null

Copy-Item -LiteralPath $dllPath -Destination (Join-Path $modRoot "NobleMod.dll") -Force

$soundPackJsonText = Get-Content -LiteralPath $soundPackTemplate -Raw -Encoding UTF8
$soundPackJsonText = $soundPackJsonText -replace '"version"\s*:\s*"[^"]*"', "`"version`": `"$version`""
[System.IO.File]::WriteAllText((Join-Path $modRoot "sound_pack.json"), $soundPackJsonText.TrimEnd() + "`n", [System.Text.UTF8Encoding]::new($false))

Copy-Item -Path (Join-Path $replacersSource '*') -Destination $targetReplacersPath -Force

foreach ($soundFile in $mappedFiles) {
    $srcPath = Join-Path $oggSource $soundFile
    $dstPath = Join-Path $targetSoundsPath $soundFile
    $dstParent = Split-Path -Parent $dstPath
    if (-not (Test-Path -LiteralPath $dstParent)) {
        New-Item -ItemType Directory -Force -Path $dstParent | Out-Null
    }
    Copy-Item -LiteralPath $srcPath -Destination $dstPath -Force
}

$spawnOverrideFile = Join-Path $spawnConfigSourcePath "level_enemy_overrides.json"
if (Test-Path $spawnOverrideFile) {
    Copy-Item -LiteralPath $spawnOverrideFile -Destination (Join-Path $targetSpawnConfigPath "level_enemy_overrides.json") -Force
}
else {
    Write-Warning "Spawn override file not found: $spawnOverrideFile (spawn config not copied)"
}

Write-Host "Installed NobleMod to profile '$ProfileName'." -ForegroundColor Green
Write-Host "DLL: $(Join-Path $modRoot 'NobleMod.dll')"
Write-Host "SoundAPI pack: sound_pack.json + replacers/ + sounds/"
Write-Host "Spawn config: $targetSpawnConfigPath"
