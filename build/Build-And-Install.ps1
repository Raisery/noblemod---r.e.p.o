param(
    [string]$ProfileName = "test",
    [string]$BuildConfiguration = "Release",
    [string]$SourceOggPath = "",
    [string]$SourceSpawnConfigPath = ""
)

$ErrorActionPreference = "Stop"

$workspaceRoot = Split-Path $PSScriptRoot -Parent
$installScript = Join-Path $PSScriptRoot "Install-ToThunderstoreProfile.ps1"

if (-not (Test-Path $installScript)) {
    throw "Install script not found: $installScript"
}

Push-Location $workspaceRoot
try {
    Write-Host "Building NobleMod ($BuildConfiguration)..." -ForegroundColor Cyan
    dotnet build -c $BuildConfiguration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Installing to Thunderstore profile '$ProfileName'..." -ForegroundColor Cyan
    if ([string]::IsNullOrWhiteSpace($SourceSpawnConfigPath)) {
        $defaultSpawnConfig = Join-Path $workspaceRoot "content\spawn\level_enemy_overrides.json"
        if (Test-Path $defaultSpawnConfig) {
            Write-Host "Spawn source: $defaultSpawnConfig" -ForegroundColor DarkCyan
        }
        else {
            Write-Warning "Spawn override file missing in workspace: $defaultSpawnConfig"
        }
    }

    & $installScript `
        -ProfileName $ProfileName `
        -BuildConfiguration $BuildConfiguration `
        -SourceOggPath $SourceOggPath `
        -SourceSpawnConfigPath $SourceSpawnConfigPath
    if ($LASTEXITCODE -ne 0) {
        throw "Install script failed with exit code $LASTEXITCODE"
    }

    Write-Host "Build + install completed." -ForegroundColor Green
}
finally {
    Pop-Location
}
