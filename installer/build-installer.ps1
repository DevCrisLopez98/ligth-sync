$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'dist\publish'
$openRgbStage = Join-Path $root 'dist\OpenRGB'
$redist = Join-Path $PSScriptRoot 'redist'
$iscc = 'C:\Users\cris-\AppData\Local\Programs\Inno Setup 6\ISCC.exe'

& (Join-Path $PSScriptRoot 'New-Icon.ps1')

$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
& $dotnet publish (Join-Path $root 'src\LightSync\LightSync.csproj') -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}

$openRgbSource = Join-Path $root '.vendor\OpenRGB\OpenRGB Windows 64-bit'
if (-not (Test-Path (Join-Path $openRgbSource 'OpenRGB.exe'))) {
    throw 'Patched OpenRGB.exe was not found. Build it locally first. Nothing was committed to the OpenRGB repository.'
}
if (Test-Path $openRgbStage) {
    Remove-Item $openRgbStage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $openRgbStage | Out-Null
robocopy $openRgbSource $openRgbStage /E /NFL /NDL /NJH /NJS /XD .git | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "Failed to copy OpenRGB ($LASTEXITCODE)"
}

New-Item -ItemType Directory -Force -Path $redist | Out-Null
$pawnSetup = Join-Path $redist 'PawnIO_setup.exe'
if (-not (Test-Path $pawnSetup)) {
    Invoke-WebRequest -Uri 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe' -OutFile $pawnSetup
}

& $iscc (Join-Path $PSScriptRoot 'LightSync.iss')
Write-Output (Join-Path $PSScriptRoot 'output\LightSync-Setup.exe')
