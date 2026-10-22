param([switch]$Development,[switch]$Installer)
$ErrorActionPreference='Stop'
$repositoryRoot=Split-Path -Parent $PSScriptRoot
$web=Join-Path $repositoryRoot 'src/MesaNexo.Web'
$env:DOTNET_CLI_HOME=Join-Path $repositoryRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $repositoryRoot '.packages'
if(Test-Path (Join-Path $repositoryRoot '.tools/cargo/bin/cargo.exe')){
    $env:CARGO_HOME=Join-Path $repositoryRoot '.tools/cargo';$env:RUSTUP_HOME=Join-Path $repositoryRoot '.tools/rustup';$env:PATH="$env:CARGO_HOME\bin;$env:PATH"
}
if(-not(Get-Command node -ErrorAction SilentlyContinue)){
    $sibling=Join-Path (Split-Path $repositoryRoot -Parent) 'SoftwarePrototipo/.tools'
    $nodeFolder=Get-ChildItem -LiteralPath $sibling -Directory -Filter 'node-*-win-x64' -ErrorAction SilentlyContinue | Select-Object -First 1
    if($nodeFolder){$env:PATH="$($nodeFolder.FullName);$env:PATH"}
}
if(-not(Get-Command node -ErrorAction SilentlyContinue)){throw 'Instala Node.js 22 LTS o posterior.'}
# Use project-local SDK on this workstation when standard VS C++ discovery is incomplete.
$sdk=Join-Path $repositoryRoot '.tools/windows-sdk'
$compiler=Get-ChildItem 'C:/Program Files/Microsoft Visual Studio/*/*/VC/Tools/MSVC/*/bin/Hostx64/x64/link.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if($compiler -and (Test-Path (Join-Path $sdk 'microsoft.windows.sdk.cpp.x64/c/um/x64/kernel32.lib'))){
    $msvc=Split-Path (Split-Path (Split-Path (Split-Path $compiler.FullName -Parent) -Parent) -Parent) -Parent
    $env:PATH="$($compiler.DirectoryName);$sdk\microsoft.windows.sdk.cpp\c\bin\10.0.26100.0\x64;$env:PATH"
    $env:CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER=$compiler.FullName
    $env:CXX=Join-Path $compiler.DirectoryName 'cl.exe'
    $env:LIB="$msvc\lib\onecore\x64;$sdk\microsoft.windows.sdk.cpp.x64\c\um\x64;$sdk\microsoft.windows.sdk.cpp.x64\c\ucrt\x64"
    $headers=Resolve-Path (Join-Path $repositoryRoot '.tools/msvc-headers/Contents/VC/Tools/MSVC/*/include') -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Path
    $env:INCLUDE="$headers;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\um;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\shared;$sdk\microsoft.windows.sdk.cpp\c\Include\10.0.26100.0\ucrt"
}
Push-Location $web
try{
    if(-not(Test-Path 'node_modules')){& npm.cmd ci --no-audit --no-fund;if($LASTEXITCODE){throw 'No se pudieron preparar las dependencias React/Tauri.'}}
    if($Development){& npm.cmd run desktop;if($LASTEXITCODE){throw 'Falló el inicio de desarrollo.'};return}
    if($Installer){& npm.cmd run package}else{& npm.cmd run tauri build -- --no-bundle}
    if($LASTEXITCODE){throw 'Falló la compilación Tauri.'}
    $output=Join-Path $repositoryRoot 'artifacts/desktop-css';New-Item -ItemType Directory -Force $output | Out-Null
    Copy-Item -LiteralPath 'src-tauri/target/release/culinary-restaurant.exe' -Destination (Join-Path $output 'CulinaryRestaurant.exe') -Force
    Copy-Item -LiteralPath 'src-tauri/binaries/culinary-bridge-x86_64-pc-windows-msvc.exe' -Destination (Join-Path $output 'culinary-bridge.exe') -Force
    Write-Output "Lista: $output\CulinaryRestaurant.exe"
}finally{Pop-Location}
