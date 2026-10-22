param([switch]$Development)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
if(-not $Development){
    $application=Join-Path $repositoryRoot 'artifacts/desktop-css/CulinaryRestaurant.exe'
    if(-not(Test-Path -LiteralPath $application)){throw 'Primero compila con deploy/Build-Hybrid.ps1.'}
    & $application
    exit
}
& (Join-Path $PSScriptRoot 'Build-Hybrid.ps1') -Development
