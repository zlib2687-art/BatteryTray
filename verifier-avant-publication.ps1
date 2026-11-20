# Vérifie qu'aucune information personnelle ne se trouve dans les fichiers qui seraient publiés sur GitHub.
# Usage :  powershell -ExecutionPolicy Bypass -File verifier-avant-publication.ps1
#
# Les valeurs recherchées sont lues sur CE PC (nom d'utilisateur Windows, nom de l'ordinateur, e-mail Git, nom Git) :
# aucune n'est écrite dans ce script, il peut donc être publié tel quel.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# Dossiers ignorés par .gitignore (régénérables, non publiés)
$ignored = 'bin','obj','publish','dist','TestResults','.vs','.git'

$needles = [ordered]@{}
$needles["nom d'utilisateur Windows"] = $env:USERNAME
$needles["nom de l'ordinateur"]       = $env:COMPUTERNAME
$gitName  = (& git config --global --get user.name  2>$null)
$gitEmail = (& git config --global --get user.email 2>$null)
if ($gitName)  { $needles['nom Git (commits)']    = $gitName }
if ($gitEmail) { $needles['e-mail Git (commits)'] = $gitEmail; $needles["partie locale de l'e-mail"] = ($gitEmail -split '@')[0] }
# Termes privés : ton prénom, ton nom, tes adresses e-mail… à écrire (un par ligne) dans un fichier HORS du dépôt :
#   %USERPROFILE%\.batterytray-termes-prives.txt
# Il n'est jamais publié et ce script ne contient aucun de ces termes.
$privateFile = Join-Path $env:USERPROFILE '.batterytray-termes-prives.txt'
if (Test-Path $privateFile) {
    $i = 0
    foreach ($line in Get-Content $privateFile) {
        $term = $line.Trim()
        if ($term.Length -ge 3 -and -not $term.StartsWith('#')) { $i++; $needles["terme privé n°$i"] = $term }
    }
    "Termes privés lus : $i (depuis $privateFile)"
} else {
    "ATTENTION : aucun fichier de termes privés ($privateFile). Crée-le avec ton prénom, ton nom et tes e-mails (un par ligne)."
}

$needles['chemin utilisateur Windows'] = '\Users\'
$needles['dossier AppData']            = 'AppData\'

$files = Get-ChildItem $root -Recurse -File -Force | Where-Object {
    $parts = $_.FullName.Substring($root.Length).Split('\') | Where-Object { $_ }
    -not ($parts | Where-Object { $ignored -contains $_ }) -and $_.Extension -notin '.png','.exe','.dll' `
        -and $_.Name -ne 'verifier-avant-publication.ps1'   # ce script contient les motifs qu'il cherche
}

$problems = 0
foreach ($file in $files) {
    $text = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $text) { continue }
    foreach ($label in $needles.Keys) {
        $needle = $needles[$label]
        if ([string]::IsNullOrWhiteSpace($needle) -or $needle.Length -lt 4) { continue }
        if ($text.IndexOf($needle, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            # on indique le fichier et le type d'information, JAMAIS la valeur trouvée
            "  PROBLÈME : $($file.FullName.Substring($root.Length + 1)) contient le $label"
            $problems++
        }
    }
    # adresses IPv4 (hors boucle locale 127.0.0.1 et adresses de version comme 8.0.0.0)
    foreach ($m in [regex]::Matches($text, '\b(?!127\.0\.0\.1\b)(?:\d{1,3}\.){3}\d{1,3}\b')) {
        if ($m.Value -match '^\d+\.\d+\.0\.0$|^0\.') { continue }
        "  À VÉRIFIER : $($file.FullName.Substring($root.Length + 1)) contient « $($m.Value) » (adresse IP ?)"
        $problems++
    }
}

"Fichiers examinés : $($files.Count)"
if ($problems -eq 0) { 'OK : aucune information personnelle trouvée dans les fichiers à publier.' }
else { "$problems point(s) à corriger avant de publier."; exit 1 }
