param(
    [Parameter(Mandatory = $true)]
    [string] $OpenRgbPath
)

$ErrorActionPreference = "Stop"

$repo = (Resolve-Path $OpenRgbPath).Path
$patch = (Resolve-Path (Join-Path $PSScriptRoot "..\patches\openrgb-gigabyte-rtx5060ti-gaming-oc-8g.patch")).Path

if (-not (Test-Path (Join-Path $repo ".git"))) {
    throw "OpenRGB path is not a Git checkout: $repo"
}

$alreadyPatched = git -C $repo grep -q "GIGABYTE_RTX5060TI_GAMING_OC_8G_SUB_DEV" -- pci_ids/pci_ids.h
if ($LASTEXITCODE -eq 0) {
    Write-Host "OpenRGB already contains the RTX 5060 Ti Gaming OC 8G registration."
    exit 0
}

git -C $repo apply --check $patch
if ($LASTEXITCODE -ne 0) {
    throw "The OpenRGB patch cannot be applied cleanly."
}

git -C $repo apply $patch
if ($LASTEXITCODE -ne 0) {
    throw "OpenRGB patch application failed."
}

Write-Host "Applied Gigabyte RTX 5060 Ti Gaming OC 8G RGB support to $repo"
