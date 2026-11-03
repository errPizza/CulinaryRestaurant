param([string]$NsisCompiler)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if(!$NsisCompiler){throw 'Indica la ruta del compilador oficial makensis.exe con -NsisCompiler.'}
$published=Join-Path $projectRoot 'artifacts\0.3.0\win-x64'
if(!(Test-Path -LiteralPath (Join-Path $published 'CulinaryRestaurant.exe'))){throw 'Publica primero el cliente win-x64.'}
$destination=Join-Path $projectRoot 'artifacts\CulinaryRestaurant-0.3.0-dev-win-x64.exe'
$files=Get-ChildItem -LiteralPath $published -File -Recurse
$body=New-Object System.Collections.Generic.List[string]
$body.Add('Unicode true')
$body.Add('Icon "'+(Join-Path $projectRoot 'branding\CulinaryRestaurant.ico')+'"')
$body.Add('UninstallIcon "'+(Join-Path $projectRoot 'branding\CulinaryRestaurant.ico')+'"')
$body.Add('Name "Culinary Restaurant (desarrollo)"')
$body.Add('OutFile "'+$destination+'"')
$body.Add('InstallDir "$LOCALAPPDATA\Programs\MesaNexoNative"')
$body.Add('RequestExecutionLevel user')
$body.Add('VIProductVersion "0.3.0.0"')
$body.Add('VIAddVersionKey /LANG=1033 "ProductName" "Culinary Restaurant"')
$body.Add('VIAddVersionKey /LANG=1033 "FileDescription" "Culinary Restaurant - instalador de desarrollo"')
$body.Add('VIAddVersionKey /LANG=1033 "FileVersion" "0.3.0"')
$body.Add('VIAddVersionKey /LANG=1033 "LegalCopyright" "Culinary Restaurant"')
$body.Add('SetCompressor /SOLID lzma')
$body.Add('Page instfiles')
$body.Add('UninstPage uninstConfirm')
$body.Add('UninstPage instfiles')
$body.Add('Section "Instalar"')
$body.Add('SetOutPath "$INSTDIR"')
$body.Add('File /r "'+$published+'\*"')
$body.Add('WriteUninstaller "$INSTDIR\Desinstalar.exe"')
$body.Add('CreateDirectory "$SMPROGRAMS\Culinary Restaurant"')
$body.Add('CreateShortCut "$SMPROGRAMS\Culinary Restaurant\Culinary Restaurant.lnk" "$INSTDIR\CulinaryRestaurant.exe"')
$body.Add('CreateShortCut "$SMPROGRAMS\Culinary Restaurant\Desinstalar.lnk" "$INSTDIR\Desinstalar.exe"')
$body.Add('SectionEnd')
$body.Add('Section "Uninstall"')
# Delete only files actually shipped. Never recursively delete user-selected paths or AppData.
foreach($file in $files){$relative=$file.FullName.Substring($published.Length+1);$body.Add('Delete "$INSTDIR\'+$relative+'"')}
$body.Add('Delete "$INSTDIR\Desinstalar.exe"')
$directories=Get-ChildItem -LiteralPath $published -Directory -Recurse | Sort-Object {$_.FullName.Length} -Descending
foreach($directory in $directories){$body.Add('RMDir "$INSTDIR\'+$directory.FullName.Substring($published.Length+1)+'"')}
$body.Add('RMDir "$INSTDIR"')
$body.Add('Delete "$SMPROGRAMS\Culinary Restaurant\Culinary Restaurant.lnk"')
$body.Add('Delete "$SMPROGRAMS\Culinary Restaurant\Desinstalar.lnk"')
$body.Add('RMDir "$SMPROGRAMS\Culinary Restaurant"')
$body.Add('SectionEnd')
$script=Join-Path $projectRoot 'artifacts\installer.nsi'
[IO.File]::WriteAllLines($script,$body,(New-Object System.Text.UTF8Encoding($false)))
& $NsisCompiler /V2 $script
if($LASTEXITCODE -ne 0){throw 'NSIS no pudo generar el instalador.'}
Get-Item -LiteralPath $destination | Select-Object Name,Length