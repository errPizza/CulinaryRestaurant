param([string]$Python='C:\Users\kikin\Desktop\CulinaryOS\tools\python\python.exe',[switch]$NativeStartup)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME=Join-Path $repositoryRoot '.dotnet';$env:NUGET_PACKAGES=Join-Path $repositoryRoot '.packages'
if(-not(Get-Command node -ErrorAction SilentlyContinue)){
 $nodeFolder=Get-ChildItem (Join-Path (Split-Path $repositoryRoot -Parent) 'SoftwarePrototipo/.tools') -Directory -Filter 'node-*-win-x64' -ErrorAction SilentlyContinue | Select-Object -First 1
 if($nodeFolder){$env:PATH="$($nodeFolder.FullName);$env:PATH"}
}
Push-Location $repositoryRoot
try{
 & dotnet run --project tests/MesaNexo.Hybrid.Tests -c Release -- $repositoryRoot $Python
 if($LASTEXITCODE){throw 'Fallaron las pruebas del puente híbrido.'}
 Push-Location src/MesaNexo.Web
 try{& npm.cmd test;if($LASTEXITCODE){throw 'Fallaron las pruebas de React.'};if($NativeStartup){& node tests/native-startup.mjs;if($LASTEXITCODE){throw 'Falló el arranque WebView2 del ejecutable.'}}}finally{Pop-Location}
}finally{Pop-Location}
