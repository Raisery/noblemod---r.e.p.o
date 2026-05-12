param(
    [string]$Configuration = "Release",
    [string]$ThunderstoreTeam = "raisery",
    [switch]$SkipBuild,
    [string]$OutDir = ""
)

$ErrorActionPreference = "Stop"

function Write-ThunderstoreZipFromStaging {
    param(
        [string]$StagingDirectory,
        [string]$ZipPath
    )
    # Compress-Archive avec plusieurs chemins peut, selon la version PowerShell, aplatir des dossiers
    # ou produire des entrees ambigues. ZipArchive + chemins relatifs explicites = structure stable partout.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $stagingFull = [System.IO.Path]::GetFullPath($StagingDirectory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $stagingFull -PathType Container)) {
        throw "Staging introuvable: $stagingFull"
    }

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $stagingFull -Recurse -File -Force) {
            $rel = $file.FullName.Substring($stagingFull.Length).TrimStart('\', '/')
            $entryName = ($rel -replace '\\', '/')
            if ([string]::IsNullOrWhiteSpace($entryName)) {
                continue
            }
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $file.FullName,
                $entryName,
                [System.IO.Compression.CompressionLevel]::Optimal
            )
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-ThunderstoreZipLayout {
    param(
        [string]$ZipPath,
        [string]$PluginRootInZip,
        [string]$PluginDllFileName,
        [string]$ExpectedSoundPackPath = "",
        [string[]]$MappedSoundFiles = @()
    )
    $rootNorm = ($PluginRootInZip.TrimEnd('/') -replace '\\', '/')
    $dllZipPath = "$rootNorm/$PluginDllFileName"
    $packJsonZipPath = "$rootNorm/sound_pack.json"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $hasDll = $false
        $hasPack = $false
        foreach ($entry in $zip.Entries) {
            $p = $entry.FullName.Replace('\', '/').TrimEnd('/')
            if ($p.Equals($dllZipPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hasDll = $true
                continue
            }
            if ($p.Equals($packJsonZipPath, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hasPack = $true
            }
        }
        if (-not $hasDll) {
            throw "ZIP Thunderstore invalide : DLL attendue sous $dllZipPath."
        }
        if (-not $hasPack) {
            throw "ZIP Thunderstore invalide : sound_pack.json attendu sous $packJsonZipPath (SoundAPI)."
        }

        if (-not [string]::IsNullOrWhiteSpace($ExpectedSoundPackPath) -and (Test-Path -LiteralPath $ExpectedSoundPackPath)) {
            $expectedBytes = [System.IO.File]::ReadAllBytes($ExpectedSoundPackPath)
            $entry = $zip.GetEntry($packJsonZipPath)
            if ($null -eq $entry) {
                throw "ZIP : entree '$packJsonZipPath' introuvable (GetEntry)."
            }
            $readStream = $entry.Open()
            try {
                $ms = New-Object System.IO.MemoryStream
                try {
                    $readStream.CopyTo($ms)
                    $actualBytes = $ms.ToArray()
                }
                finally {
                    $ms.Dispose()
                }
            }
            finally {
                $readStream.Dispose()
            }
            if ($actualBytes.Length -ne $expectedBytes.Length) {
                throw "ZIP : sound_pack.json ne correspond pas au staging (taille $($actualBytes.Length) vs $($expectedBytes.Length))."
            }
            for ($i = 0; $i -lt $expectedBytes.Length; $i++) {
                if ($actualBytes[$i] -ne $expectedBytes[$i]) {
                    throw "ZIP : sound_pack.json differe du staging (octet #$i)."
                }
            }
        }

        foreach ($soundFile in $MappedSoundFiles) {
            if ([string]::IsNullOrWhiteSpace($soundFile)) { continue }
            $soundFileNorm = ($soundFile.Trim() -replace '\\', '/')
            $zipRel = "$rootNorm/sounds/$soundFileNorm"
            $found = $false
            foreach ($entry in $zip.Entries) {
                $p = $entry.FullName.Replace('\', '/').TrimEnd('/')
                if ($p.Equals($zipRel, [System.StringComparison]::OrdinalIgnoreCase)) {
                    $found = $true
                    break
                }
            }
            if (-not $found) {
                throw "ZIP Thunderstore invalide : fichier son manquant : '$zipRel' (SoundAPI, dossier sounds/)."
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Assert-ValidThunderstoreIconPng {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 24) {
        throw "icon.png is too small or empty: $Path"
    }
    $pngSig = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    for ($i = 0; $i -lt 8; $i++) {
        if ($bytes[$i] -ne $pngSig[$i]) {
            throw "icon.png n'est pas un PNG valide (signature fichier). Souvent un .ico ou .webp renomme en .png; Thunderstore le refuse. Corriger thunderstore\icon.png (export PNG 256x256). Fichier: $Path"
        }
    }
}

function Get-SoundFileNamesFromReplacerJsons {
    param([string]$ReplacersDirectory)
    if (-not (Test-Path -LiteralPath $ReplacersDirectory -PathType Container)) {
        throw "Dossier replacers introuvable: $ReplacersDirectory"
    }
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
$pluginInfoPath = Join-Path $workspaceRoot "src\PluginInfo.cs"
$manifestTemplatePath = Join-Path $workspaceRoot "thunderstore\manifest.json"
$thunderstoreReadmePath = Join-Path $workspaceRoot "thunderstore\README.md"
$rootReadmePath = Join-Path $workspaceRoot "README.md"
$changelogPath = Join-Path $workspaceRoot "CHANGELOG.md"
$iconPreferredPath = Join-Path $workspaceRoot "thunderstore\icon.png"
$iconFallbackPath = Join-Path $workspaceRoot "icon.png"
$dllPath = Join-Path $workspaceRoot "bin\$Configuration\net48\NobleMod.dll"
$soundPackSource = Join-Path $workspaceRoot "content\sound-pack"
$oggSource = Join-Path $soundPackSource "sounds"
$spawnConfigSource = Join-Path $workspaceRoot "content\spawn"

if (-not (Test-Path $pluginInfoPath)) {
    throw "Missing PluginInfo.cs: $pluginInfoPath"
}
if (-not (Test-Path $manifestTemplatePath)) {
    throw "Missing thunderstore\manifest.json: $manifestTemplatePath"
}

$pluginInfoText = Get-Content -LiteralPath $pluginInfoPath -Raw -Encoding UTF8
if ($pluginInfoText -notmatch 'Version\s*=\s*"([^"]+)"') {
    throw "Could not parse PluginInfo.Version in $pluginInfoPath"
}
$version = $Matches[1]

if ($pluginInfoText -notmatch 'Name\s*=\s*"([^"]+)"') {
    throw "Could not parse PluginInfo.Name in $pluginInfoPath"
}
$modName = $Matches[1]

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $workspaceRoot "dist"
}

if (-not $SkipBuild) {
    Push-Location $workspaceRoot
    try {
        dotnet build -c $Configuration --nologo -v minimal
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

if (-not (Test-Path $dllPath)) {
    throw "Missing DLL after build: $dllPath"
}

$stagingRoot = Join-Path $workspaceRoot "dist\thunderstore_staging"
if (Test-Path $stagingRoot) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null

# manifest : version alignée sur PluginInfo
$manifestJson = Get-Content -LiteralPath $manifestTemplatePath -Raw -Encoding UTF8
$manifestJson = $manifestJson -replace '"version_number"\s*:\s*"[^"]*"', "`"version_number`": `"$version`""
$manifestOut = Join-Path $stagingRoot "manifest.json"
[System.IO.File]::WriteAllText($manifestOut, $manifestJson.TrimEnd() + "`n", [System.Text.UTF8Encoding]::new($false))

# README (page Thunderstore)
if (Test-Path $thunderstoreReadmePath) {
    Copy-Item -LiteralPath $thunderstoreReadmePath -Destination (Join-Path $stagingRoot "README.md") -Force
}
elseif (Test-Path $rootReadmePath) {
    Copy-Item -LiteralPath $rootReadmePath -Destination (Join-Path $stagingRoot "README.md") -Force
}
else {
    throw "No README: add thunderstore\README.md or README.md at repo root."
}

if (-not (Test-Path -LiteralPath $changelogPath)) {
    throw "Missing CHANGELOG.md at repo root (requis Thunderstore / suivi des versions): $changelogPath"
}
Copy-Item -LiteralPath $changelogPath -Destination (Join-Path $stagingRoot "CHANGELOG.md") -Force

# icon.png 256×256 (obligatoire Thunderstore)
$iconOut = Join-Path $stagingRoot "icon.png"
if (Test-Path $iconPreferredPath) {
    Copy-Item -LiteralPath $iconPreferredPath -Destination $iconOut -Force
}
elseif (Test-Path $iconFallbackPath) {
    Copy-Item -LiteralPath $iconFallbackPath -Destination $iconOut -Force
}
else {
    try {
        Add-Type -AssemblyName System.Drawing
        $bmp = New-Object System.Drawing.Bitmap 256, 256
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::FromArgb(58, 42, 88))
        $g.Dispose()
        $bmp.Save($iconOut, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Warning "No thunderstore\icon.png: generated a solid-color 256×256 placeholder. Replace before release."
    }
    catch {
        throw "Thunderstore requires icon.png (256×256). Add thunderstore\icon.png or install GDI+ / use a real PNG. Details: $_"
    }
}

Assert-ValidThunderstoreIconPng -Path $iconOut

# Meme convention que beaucoup de paquets Thunderstore REPO : contenu BepInEx sous BepInEx/plugins/<Mod>/
# (manifest.json, README.md, CHANGELOG.md, icon.png a la racine du zip Thunderstore).
$dllFileName = "$modName.dll"
$pluginStagingRoot = Join-Path $stagingRoot "BepInEx\plugins\$modName"
New-Item -ItemType Directory -Force -Path $pluginStagingRoot | Out-Null
Copy-Item -LiteralPath $dllPath -Destination (Join-Path $pluginStagingRoot $dllFileName) -Force

# SoundAPI : tout le pack sous content/sound-pack/ (json, replacers, sounds/**/*.ogg).
if (-not (Test-Path -LiteralPath $soundPackSource -PathType Container)) {
    throw "Missing content/sound-pack folder: $soundPackSource"
}
$soundPackTemplate = Join-Path $soundPackSource "sound_pack.json"
if (-not (Test-Path -LiteralPath $soundPackTemplate)) {
    throw "Missing content/sound-pack/sound_pack.json: $soundPackTemplate"
}
$replacersSource = Join-Path $soundPackSource "replacers"
if (-not (Test-Path -LiteralPath $replacersSource -PathType Container)) {
    throw "Missing content/sound-pack/replacers: $replacersSource"
}

if (-not (Test-Path -LiteralPath $oggSource -PathType Container)) {
    throw "Missing folder with .ogg sources for package: $oggSource"
}

$mappedFiles = Get-SoundFileNamesFromReplacerJsons -ReplacersDirectory $replacersSource
if ($mappedFiles.Count -eq 0) {
    throw "content/sound-pack/replacers/*.json : aucune entree 'sound' trouvee."
}
foreach ($soundFile in $mappedFiles) {
    $soundPath = Join-Path $oggSource $soundFile
    if (-not (Test-Path -LiteralPath $soundPath)) {
        throw "Replacer reference un fichier manquant dans content/sound-pack/sounds/: $soundPath"
    }
}

$soundPackJsonText = Get-Content -LiteralPath $soundPackTemplate -Raw -Encoding UTF8
$soundPackJsonText = $soundPackJsonText -replace '"version"\s*:\s*"[^"]*"', "`"version`": `"$version`""
$soundPackStagingPath = Join-Path $pluginStagingRoot "sound_pack.json"
[System.IO.File]::WriteAllText($soundPackStagingPath, $soundPackJsonText.TrimEnd() + "`n", [System.Text.UTF8Encoding]::new($false))

$destReplacers = Join-Path $pluginStagingRoot "replacers"
Copy-Item -Path $replacersSource -Destination $destReplacers -Recurse -Force

$destSounds = Join-Path $pluginStagingRoot "sounds"
New-Item -ItemType Directory -Force -Path $destSounds | Out-Null
$oggRootResolved = (Resolve-Path -LiteralPath $oggSource).Path
foreach ($oggFile in (Get-ChildItem -LiteralPath $oggSource -Recurse -File -Filter *.ogg)) {
    $rel = $oggFile.FullName.Substring($oggRootResolved.Length).TrimStart('\', '/')
    $destPath = Join-Path $destSounds $rel
    $destParent = Split-Path -Parent $destPath
    if (-not (Test-Path -LiteralPath $destParent)) {
        New-Item -ItemType Directory -Force -Path $destParent | Out-Null
    }
    Copy-Item -LiteralPath $oggFile.FullName -Destination $destPath -Force
}

if (Test-Path $spawnConfigSource) {
    $destSpawn = Join-Path $pluginStagingRoot "SpawnConfig"
    Copy-Item -Path $spawnConfigSource -Destination $destSpawn -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$zipName = "$ThunderstoreTeam-$modName-$version.zip"
$zipPath = Join-Path $OutDir $zipName
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-ThunderstoreZipFromStaging -StagingDirectory $stagingRoot -ZipPath $zipPath

$pluginRootInZip = "BepInEx/plugins/$modName"
Assert-ThunderstoreZipLayout -ZipPath $zipPath -PluginRootInZip $pluginRootInZip -PluginDllFileName $dllFileName -ExpectedSoundPackPath $soundPackStagingPath -MappedSoundFiles $mappedFiles

Write-Host ""
Write-Host "Thunderstore package ready:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host ""
Write-Host "Next: validate manifest at https://thunderstore.io/tools/manifest-v1-validator/ then upload for game REPO."
