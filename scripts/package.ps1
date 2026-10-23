<#
.SYNOPSIS
  Builds Rental Warehouse in Release and packs dist/RentalWarehouse.zip with only the files the game needs.

.DESCRIPTION
  1. dotnet build -c Release of the game project (its output goes to Mod/1.7/Assemblies).
  2. Copies Mod/ to dist/RentalWarehouse/, leaving out placeholders and anything not meant for players.
  3. Zips it as dist/RentalWarehouse.zip (folder RentalWarehouse/ at the root).
  Afterwards run: python scripts/validate-package.py dist/RentalWarehouse.zip --source Source

.PARAMETER RimWorldManagedPath
  Optional local game folder with the managed assemblies; without it the NuGet reference assemblies are used.
#>
param(
    [string]$RimWorldManagedPath = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo "dist"
$stage = Join-Path $dist "RentalWarehouse"
$zip = Join-Path $dist "RentalWarehouse.zip"
$project = Join-Path $repo "Source/RentalWarehouse/RentalWarehouse.csproj"
$assemblies = Join-Path $repo "Mod/1.7/Assemblies"

# Stale DLLs from other builds must never reach the package.
Get-ChildItem -Path $assemblies -Filter *.dll -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $assemblies -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

$buildArgs = @("build", $project, "-c", "Release", "-nologo")
if ($RimWorldManagedPath -ne "") { $buildArgs += "-p:RimWorldManagedPath=$RimWorldManagedPath" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -Path (Join-Path $repo "Mod/*") -Destination $stage -Recurse

# Keep only what the game loads.
Get-ChildItem -Path $stage -Recurse -File |
    Where-Object { $_.Name -eq ".keep" -or $_.Extension -in @(".pdb", ".json", ".cs", ".csproj") } |
    Remove-Item -Force
foreach ($required in @("About/About.xml", "1.7/Assemblies/RentalWarehouse.dll", "1.7/Assemblies/RentalWarehouse.Core.dll")) {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "missing $required after build" }
}

Compress-Archive -Path $stage -DestinationPath $zip
Write-Host "Packed $zip"
