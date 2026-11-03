param([Parameter(Mandatory)][string]$RootPublicKey,[string]$NsisCompiler,[switch]$Android)
$ErrorActionPreference='Stop'
& (Join-Path $PSScriptRoot 'Use-RootKey.ps1') -Path $RootPublicKey
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $projectRoot
try{
 foreach($rid in @('win-x64','linux-x64')){
  $out=if($rid -eq 'win-x64'){'artifacts/0.3.0/win-x64'}else{'artifacts/0.3.0/linux-x64/client'}
  dotnet publish src/MesaNexo.Desktop -c Release -r $rid --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o $out
  if($LASTEXITCODE -ne 0){throw 'Fallo al publicar cliente.'}
 }
 dotnet publish src/MesaNexo.Api -c Release -r linux-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o artifacts/0.3.0/linux-x64/backend
 if($LASTEXITCODE -ne 0){throw 'Fallo al publicar backend.'}
 dotnet publish src/MesaNexo.Central -c Release -r linux-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o artifacts/0.3.0/central-linux-x64
 if($LASTEXITCODE -ne 0){throw 'Fallo al publicar central.'}
 dotnet publish src/MesaNexo.Public -c Release -r linux-x64 --self-contained true -p:RuntimeFrameworkVersion=10.0.13 -o artifacts/0.3.0/public-linux-x64
 if($LASTEXITCODE -ne 0){throw 'Fallo al publicar portal.'}
 if($NsisCompiler){& (Join-Path $PSScriptRoot 'package_windows.ps1') -NsisCompiler $NsisCompiler}
 if($Android){dotnet build src/MesaNexo.Android -c Debug;if($LASTEXITCODE -ne 0){throw 'Fallo Android.'}}
}finally{Pop-Location}