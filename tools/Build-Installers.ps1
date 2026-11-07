# Instaladores firmados: Windows (Tauri/NSIS) y Android (APK). Antes: tools/New-SigningKeys.ps1. Ver docs/FIRMA.md.
param([Parameter(Mandatory)][string]$RootPublicKey,[switch]$Windows,[switch]$Android,[string]$Directory=(Join-Path $env:USERPROFILE '.culinary-firma'),[string]$Tools)
$ErrorActionPreference='Stop'
# The keys were moved to the Desktop (docs/FIRMA.md); fall back there when the default folder is missing.
if(-not(Test-Path $Directory)){$Directory=Join-Path $env:USERPROFILE 'Desktop\.culinary-firma'}
$projectRoot=Split-Path $PSScriptRoot -Parent
if(-not $Tools){$Tools=Join-Path $projectRoot '.tools'}
& (Join-Path $PSScriptRoot 'Use-RootKey.ps1') -Path $RootPublicKey
$version=([xml](Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$out=Join-Path $projectRoot "artifacts\entrega\$version"
New-Item -ItemType Directory -Force $out | Out-Null
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
if($Windows){
    $env:CARGO_HOME=Join-Path $Tools 'cargo';$env:RUSTUP_HOME=Join-Path $Tools 'rustup';$env:PATH="$env:CARGO_HOME\bin;$env:PATH"
    $sdk=Join-Path $Tools 'windows-sdk'
    $compiler=Get-ChildItem 'C:/Program Files/Microsoft Visual Studio/*/*/VC/Tools/MSVC/*/bin/Hostx64/x64/link.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
    if($compiler -and (Test-Path (Join-Path $sdk 'microsoft.windows.sdk.cpp.x64/c/um/x64/kernel32.lib'))){
        $msvc=Split-Path (Split-Path (Split-Path (Split-Path $compiler.FullName -Parent) -Parent) -Parent) -Parent
        $env:PATH="$($compiler.DirectoryName);$sdk\microsoft.windows.sdk.cpp\c\bin\10.0.26100.0\x64;$env:PATH"
        $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER=$compiler.FullName
        $env:CXX=Join-Path $compiler.DirectoryName 'cl.exe'
        $env:LIB="$msvc\lib\onecore\x64;$sdk\microsoft.windows.sdk.cpp.x64\c\um\x64;$sdk\microsoft.windows.sdk.cpp.x64\c\ucrt\x64"
        $headers=Resolve-Path (Join-Path $Tools 'msvc-headers/Contents/VC/Tools/MSVC/*/include') -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Path
        $env:INCLUDE="$headers;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\um;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\shared;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\ucrt"
    }
    # Signing lives in a merged config so development builds and CI keep working without the keys.
    $config=Join-Path $projectRoot '.lab\tauri-firma.json'
    New-Item -ItemType Directory -Force (Split-Path $config) | Out-Null
    $sign=@{bundle=@{windows=@{signCommand=@{cmd='powershell';args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'Sign-Package.ps1'),'%1')}}}} | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($config,$sign)
    Push-Location (Join-Path $projectRoot 'src\MesaNexo.Web')
    try{
        & npm.cmd run tauri -- build --config $config
        if($LASTEXITCODE){throw 'Fallo la compilacion de Tauri.'}
        Copy-Item "src-tauri\target\release\bundle\nsis\Culinary Restaurant_${version}_x64-setup.exe" (Join-Path $out "CulinaryRestaurant-$version-windows-x64-setup.exe") -Force
    }finally{Pop-Location}
}
if($Android){
    $env:CULINARY_ANDROID_PASS=(Get-Content (Join-Path $Directory 'android.pass') -Raw).Trim()
    Push-Location $projectRoot
    try{
        # Untrimmed: the Release APK cannot be run here, so it keeps the same assemblies the Debug build exercised.
        dotnet publish src/MesaNexo.Android -c Release -f net10.0-android -o '.lab/android-release' -p:PublishTrimmed=false -p:RunAOTCompilation=false -p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=$(Join-Path $Directory 'culinary-android.jks')" -p:AndroidSigningKeyAlias=culinary -p:AndroidSigningStorePass=env:CULINARY_ANDROID_PASS -p:AndroidSigningKeyPass=env:CULINARY_ANDROID_PASS "-p:AndroidSdkDirectory=$(Join-Path $env:LOCALAPPDATA 'Android\Sdk')" "-p:JavaSdkDirectory=$env:JAVA_HOME"
        if($LASTEXITCODE){throw 'Fallo Android.'}
        # MSBuild only signs with one key; re-sign with the rotation to the Culinary Services key (tools/Sign-Apk.ps1).
        $apk=Get-ChildItem '.lab/android-release' -Filter '*-Signed.apk' | Select-Object -First 1
        & (Join-Path $PSScriptRoot 'Sign-Apk.ps1') $apk.FullName (Join-Path $out "CulinaryRestaurant-$version-android.apk") -Directory $Directory
    }finally{Pop-Location;Remove-Item Env:\CULINARY_ANDROID_PASS}
}
Get-ChildItem $out | Select-Object Name,Length
