# Alinea y firma un APK con la llave de Android de Culinary Services. Si existe culinary-android.lineage, firma con
# rotación: v1/v2 con la llave original (Android 7-8) y v3 con la nueva, encadenada a la anterior (Android 9+), así
# lo ya instalado se actualiza sin desinstalar. Ver docs/FIRMA.md.
param([Parameter(Mandatory,Position=0)][string]$Apk,[Parameter(Mandatory,Position=1)][string]$Out,[string]$Directory=(Join-Path $env:USERPROFILE '.culinary-firma'))
$ErrorActionPreference='Stop'
if(-not(Test-Path $Directory)){$Directory=Join-Path (Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent) 'Desktop\.culinary-firma'}
if(-not(Test-Path $Directory)){throw 'No encuentro la carpeta .culinary-firma; indícala con -Directory.'}
if(-not $env:ANDROID_HOME){$env:ANDROID_HOME=Join-Path $env:LOCALAPPDATA 'Android\Sdk'}
$bt=Get-ChildItem (Join-Path $env:ANDROID_HOME 'build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$pass=(Get-Content (Join-Path $Directory 'android.pass') -Raw).Trim()
$aligned="$Out.aligned"
& (Join-Path $bt.FullName 'zipalign.exe') -f -p 4 $Apk $aligned
if($LASTEXITCODE){throw 'No se pudo alinear el APK.'}
$signers=@('--ks',(Join-Path $Directory 'culinary-android.jks'),'--ks-key-alias','culinary','--ks-pass',"pass:$pass")
$lineage=Join-Path $Directory 'culinary-android.lineage'
if(Test-Path $lineage){
    $signers+=@('--next-signer','--ks',(Join-Path $Directory 'culinary-services-android.jks'),'--ks-key-alias','culinary-services','--ks-pass',"pass:$pass",'--lineage',$lineage,'--rotation-min-sdk-version','28')
}
& (Join-Path $bt.FullName 'apksigner.bat') sign @signers --out $Out $aligned
$code=$LASTEXITCODE
Remove-Item $aligned -ErrorAction SilentlyContinue
if($code){throw 'No se pudo firmar el APK.'}
"APK firmado: $(Resolve-Path $Out)"
