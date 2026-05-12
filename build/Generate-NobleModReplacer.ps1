# Genere content/sound-pack/replacers/noblemod.json et noblemod_hunter_hum.json (SoundAPI).
# SoundAPI tire un clip via une roulette sur la somme des poids (voir SoundReplacementHandler.TryGetReplacementClip).
# update_every_frame + conditions NobleMod:random_slot (voir NobleModRandomSlotCondition.cs).

$root = Split-Path $PSScriptRoot -Parent
$dir = Join-Path $root "content\sound-pack\replacers"
New-Item -ItemType Directory -Force -Path $dir | Out-Null

# R toujours uniforme sur 1..1000 ; plages random_match (10% x4 + 60% silence).
$hunterRanges = @("1..100", "101..200", "201..300", "301..400", "401..1000")

function RandomRangeCond([string]$RandomMatch, [bool]$Sticky) {
    $h = @{ type = "NobleMod:random_slot"; random_match = $RandomMatch }
    if ($Sticky) { $h["sticky"] = $true }
    return $h
}

$outMain = Join-Path $dir "noblemod.json"
$objMain = @{
    replacements = @(
        @{
            matches = @("*:*:extraction point activate01")
            sounds    = @(@{ sound = "herve/jvais_vnir_te_voir_fdp.ogg"; weight = 100 })
        }
    )
}

$outHum = Join-Path $dir "noblemod_hunter_hum.json"
$objHum = @{
    update_every_frame = $true
    replacements       = @(
        @{
            matches = @("*:*:enemy hunter humming loop")
            sounds    = @(
                @{ sound = "herve/jvais_vnir_te_voir_fdp.ogg"; weight = 1; condition = (RandomRangeCond $hunterRanges[0] $true) },
                @{ sound = "herve/coiffeur.ogg"; weight = 1; condition = (RandomRangeCond $hunterRanges[1] $true) },
                @{ sound = "herve/couper_les_chveux.ogg"; weight = 1; condition = (RandomRangeCond $hunterRanges[2] $true) },
                @{ sound = "herve/bougnoule.ogg"; weight = 1; condition = (RandomRangeCond $hunterRanges[3] $true) },
                @{ sound = "long/silence.ogg"; weight = 1; condition = (RandomRangeCond $hunterRanges[4] $true) }
            )
        }
    )
}

$enc = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($outMain, (($objMain | ConvertTo-Json -Depth 10).TrimEnd() + "`n"), $enc)
[System.IO.File]::WriteAllText($outHum, (($objHum | ConvertTo-Json -Depth 10).TrimEnd() + "`n"), $enc)
Write-Host "Wrote $outMain"
Write-Host "Wrote $outHum"
