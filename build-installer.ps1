# Construit l'installateur BatteryTray-Setup.exe (autonome : aucun .NET à installer).
# Usage :  powershell -ExecutionPolicy Bypass -File build-installer.ps1
# Résultat : dist\BatteryTray-Setup.exe  (le même fichier sert d'installateur et de programme)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$publish = Join-Path $root 'publish'
$dist    = Join-Path $root 'dist'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

& $dotnet publish (Join-Path $root 'BatteryTray\BatteryTray.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Echec de dotnet publish' }

# Le nom du fichier déclenche le mode installateur (voir Installer.IsSetupFile)
Copy-Item (Join-Path $publish 'BatteryTray.exe') (Join-Path $dist 'BatteryTray-Setup.exe') -Force
$size = [math]::Round((Get-Item (Join-Path $dist 'BatteryTray-Setup.exe')).Length / 1MB, 1)
"OK : $dist\BatteryTray-Setup.exe ($size Mo)"
