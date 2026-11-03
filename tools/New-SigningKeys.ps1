# Crea las llaves de firma de Culinary (desarrollador PizzaDev) fuera del repositorio. No sobrescribe ninguna:
# perder la de Android impide actualizar la app ya instalada. Ver docs/FIRMA.md.
param([string]$Directory=(Join-Path $env:USERPROFILE '.culinary-firma'))
$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force $Directory | Out-Null
& icacls.exe $Directory /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
$keytool=Join-Path $env:JAVA_HOME 'bin\keytool.exe'
function RandomPassword($name){
    $file=Join-Path $Directory "$name.pass"
    if(-not(Test-Path $file)){$bytes=New-Object byte[] 24;[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes);[IO.File]::WriteAllText($file,[Convert]::ToBase64String($bytes))}
    (Get-Content $file -Raw).Trim()
}
$windows=Join-Path $Directory 'culinary-windows.pfx'
if(-not(Test-Path $windows)){
    $pass=RandomPassword 'windows'
    & $keytool -genkeypair -alias culinary -keyalg RSA -keysize 3072 -sigalg SHA256withRSA -validity 1825 -dname 'CN=Culinary, OU=PizzaDev, O=Culinary, C=MX' -ext 'KU=digitalSignature' -ext 'EKU=codeSigning' -storetype PKCS12 -keystore $windows -storepass $pass -keypass $pass
    if($LASTEXITCODE){throw 'No se pudo crear el certificado de Windows.'}
    & $keytool -exportcert -rfc -alias culinary -keystore $windows -storepass $pass -file (Join-Path $Directory 'culinary-windows.cer')
    if($LASTEXITCODE){throw 'No se pudo exportar el certificado de Windows.'}
}
$android=Join-Path $Directory 'culinary-android.jks'
if(-not(Test-Path $android)){
    $pass=RandomPassword 'android'
    & $keytool -genkeypair -alias culinary -keyalg RSA -keysize 4096 -sigalg SHA256withRSA -validity 10950 -dname 'CN=PizzaDev, O=Culinary, C=MX' -storetype PKCS12 -keystore $android -storepass $pass -keypass $pass
    if($LASTEXITCODE){throw 'No se pudo crear la llave de Android.'}
}
$gnupg=Join-Path $Directory 'gnupg'
if(-not(Test-Path $gnupg)){
    New-Item -ItemType Directory $gnupg | Out-Null
    RandomPassword 'linux' | Out-Null
    $gpg=Join-Path (Split-Path (Split-Path (Get-Command git).Source -Parent) -Parent) 'usr\bin\gpg.exe'
    # Git's gpg is an MSYS program: it only understands POSIX paths.
    $posix=('/'+$Directory.Replace(':','').Replace([char]92,[char]47))
    & $gpg --homedir "$posix/gnupg" --batch --pinentry-mode loopback --passphrase-file "$posix/linux.pass" --quick-generate-key 'Culinary (PizzaDev)' rsa4096 sign 10y
    if($LASTEXITCODE){throw 'No se pudo crear la llave de Linux.'}
    & $gpg --homedir "$posix/gnupg" --batch --armor --output "$posix/culinary-linux.asc" --export 'Culinary (PizzaDev)'
    if($LASTEXITCODE){throw 'No se pudo exportar la llave de Linux.'}
}
Get-ChildItem $Directory -File | Select-Object Name,Length
