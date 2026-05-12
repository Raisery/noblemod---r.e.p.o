param(
    [Parameter(Mandatory = $true)]
    [string]$RepoPath
)

$ErrorActionPreference = "Stop"

$refs = Join-Path $PSScriptRoot "..\\refs"
$refs = [System.IO.Path]::GetFullPath($refs)
New-Item -ItemType Directory -Force -Path $refs | Out-Null

$data = Join-Path $RepoPath "REPO_Data\Managed"
$bep = Join-Path $RepoPath "BepInEx\core"

if (-not (Test-Path $data)) {
    throw "Missing game Managed folder: $data"
}

Get-ChildItem -Path $data -Filter *.dll -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $refs -Force
}
Write-Host "Copied game Managed DLLs -> $refs ($((Get-ChildItem $data -Filter *.dll -File).Count) files)"

$bepinexDll = Join-Path $bep "BepInEx.dll"
$harmonyDll = Join-Path $bep "0Harmony.dll"
foreach ($src in @($bepinexDll, $harmonyDll)) {
    if (-not (Test-Path $src)) {
        throw "Missing dependency: $src"
    }
    Copy-Item -Force $src $refs
}

$soundApiName = "me.loaforc.soundapi.dll"
$soundApiDest = Join-Path $refs $soundApiName
$plugins = Join-Path $RepoPath "BepInEx\plugins"

function Resolve-SoundApiDllPath {
    param([string]$PluginsRoot)
    if (-not (Test-Path $PluginsRoot)) {
        return $null
    }
    # Racine plugins (certains profils y déposent les DLL)
    $flat = Join-Path $PluginsRoot $soundApiName
    if (Test-Path $flat) {
        return (Resolve-Path $flat).Path
    }
    # Même dossier qu'un mod (ex. NobleMod + SoundAPI côte à côte dans plugins\MonPack\)
    foreach ($dir in Get-ChildItem -Path $PluginsRoot -Directory -ErrorAction SilentlyContinue) {
        $side = Join-Path $dir.FullName $soundApiName
        if (Test-Path $side) {
            return (Resolve-Path $side).Path
        }
    }
    # Secours : arborescence limitée (évite un scan complet du disque)
    $deep = Get-ChildItem -Path $PluginsRoot -Recurse -Depth 6 -Filter $soundApiName -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $deep) {
        return $deep.FullName
    }
    return $null
}

if (Test-Path $soundApiDest) {
    Write-Host "Conserver $soundApiDest (deja present ; supprimez-le pour forcer une recopie depuis le jeu)."
} else {
    $soundApiSrc = Resolve-SoundApiDllPath -PluginsRoot $plugins
    if ($null -eq $soundApiSrc) {
        throw @"
$soundApiName introuvable sous '$plugins' (racine, sous-dossier direct, ou profondeur <= 6).
Placez la DLL au meme endroit que NobleMod.dll (dossier du mod) ou copiez-la manuellement vers :
  $soundApiDest
"@
    }
    Copy-Item -Force $soundApiSrc $soundApiDest
    Write-Host "Copied SoundAPI: $soundApiSrc -> $soundApiDest"
}

$repoLibName = "REPOLib.dll"
$repoLibDest = Join-Path $refs $repoLibName

function Resolve-RepoLibDllPath {
    param([string]$PluginsRoot)
    if (-not (Test-Path $PluginsRoot)) {
        return $null
    }
    $flat = Join-Path $PluginsRoot $repoLibName
    if (Test-Path $flat) {
        return (Resolve-Path $flat).Path
    }
    foreach ($dir in Get-ChildItem -Path $PluginsRoot -Directory -ErrorAction SilentlyContinue) {
        $side = Join-Path $dir.FullName $repoLibName
        if (Test-Path $side) {
            return (Resolve-Path $side).Path
        }
    }
    $deep = Get-ChildItem -Path $PluginsRoot -Recurse -Depth 6 -Filter $repoLibName -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $deep) {
        return $deep.FullName
    }
    return $null
}

if (Test-Path $repoLibDest) {
    Write-Host "Conserver $repoLibDest (deja present ; supprimez-le pour forcer une recopie depuis le jeu)."
} else {
    $repoLibSrc = Resolve-RepoLibDllPath -PluginsRoot $plugins
    if ($null -eq $repoLibSrc) {
        throw @"
$repoLibName introuvable sous '$plugins' (racine, sous-dossier direct, ou profondeur <= 6).
Placez la DLL au meme endroit que NobleMod.dll (dossier du mod) ou copiez-la manuellement vers :
  $repoLibDest
"@
    }
    Copy-Item -Force $repoLibSrc $repoLibDest
    Write-Host "Copied REPOLib: $repoLibSrc -> $repoLibDest"
}

Write-Host "References copied to $refs"
