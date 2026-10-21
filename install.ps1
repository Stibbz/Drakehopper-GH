<#
Copies built Grasshopper components (.gha) and user objects (.ghuser) from this repo
to the Grasshopper folders under %APPDATA%\Grasshopper.

Usage:  .\install.ps1 [-Configuration Release] [-Build]
  -Build  runs `dotnet build` on each *.csproj first.
Close Rhino before running, otherwise loaded .gha files are locked.
#>
param(
    [string]$Configuration = "Release",
    [switch]$Build
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$libs = Join-Path $env:APPDATA "Grasshopper\Libraries"
$userObjs = Join-Path $env:APPDATA "Grasshopper\UserObjects"
New-Item -ItemType Directory -Force -Path $libs, $userObjs | Out-Null

if ($Build) {
    Get-ChildItem $root -Recurse -Filter *.csproj | ForEach-Object {
        dotnet build $_.FullName -c $Configuration
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $($_.Name)" }
    }
}

$gha = Get-ChildItem $root -Recurse -Filter *.gha |
    Where-Object { $_.FullName -match "\\bin\\$Configuration\\" }
foreach ($f in $gha) {
    Copy-Item $f.FullName $libs -Force
    Write-Host "Copied $($f.Name) -> $libs"
}

Get-ChildItem $root -Recurse -Filter *.ghuser | ForEach-Object {
    Copy-Item $_.FullName $userObjs -Force
    Write-Host "Copied $($_.Name) -> $userObjs"
}

if (-not $gha) { Write-Warning "No .gha found for $Configuration. Use -Build." }
