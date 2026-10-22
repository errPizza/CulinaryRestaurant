param([string]$Python='C:\Users\kikin\Desktop\CulinaryOS\tools\python\python.exe')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$env:PYTHONDONTWRITEBYTECODE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $projectRoot
try{
 & $Python -B tests/test_runtime_extension.py
 if($LASTEXITCODE -ne 0){throw 'Fallaron las pruebas del runtime.'}
 dotnet run --project tests/MesaNexo.Tests -c Debug -- $projectRoot $Python
 if($LASTEXITCODE -ne 0){throw 'Fallaron las pruebas de integración.'}
}finally{Pop-Location}