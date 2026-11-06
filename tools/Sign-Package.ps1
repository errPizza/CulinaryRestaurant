# Firma un .exe (Authenticode) o un .deb (firma GPG separada, .asc) con las llaves de tools/New-SigningKeys.ps1.
# Con un certificado de una CA instalado en el equipo, definir CULINARY_SIGN_THUMBPRINT: se usa en lugar del .pfx.
param([Parameter(Mandatory,Position=0)][string]$Path,[string]$Directory=(Join-Path $env:USERPROFILE '.culinary-firma'))
$ErrorActionPreference='Stop'
# The keys were moved to the Desktop (docs/FIRMA.md); fall back there when the default folder is missing.
if(-not(Test-Path $Directory)){$Directory=Join-Path $env:USERPROFILE 'Desktop\.culinary-firma'}
if($Path -like '*.deb'){
    $gpg=Join-Path (Split-Path (Split-Path (Get-Command git).Source -Parent) -Parent) 'usr\bin\gpg.exe'
    # Git's gpg is an MSYS program: it only understands POSIX paths.
    $posix={param($p) '/'+((Resolve-Path -LiteralPath $p).Path.Replace(':','').Replace([char]92,[char]47))}
    $key=& $posix $Directory;$file=& $posix $Path
    & $gpg --homedir "$key/gnupg" --batch --yes --pinentry-mode loopback --passphrase-file "$key/linux.pass" --local-user 'Culinary Services (PizzaDev)' --armor --detach-sign --output "$file.asc" $file
    if($LASTEXITCODE){throw 'No se pudo firmar el paquete Linux.'}
    return
}
$sdk=Join-Path (Split-Path $PSScriptRoot -Parent) '.tools\windows-sdk\microsoft.windows.sdk.cpp\c\bin\10.0.26100.0\x64\signtool.exe'
$identity=if($env:CULINARY_SIGN_THUMBPRINT){@('/sha1',$env:CULINARY_SIGN_THUMBPRINT)}else{@('/f',(Join-Path $Directory 'culinary-windows.pfx'),'/p',(Get-Content (Join-Path $Directory 'windows.pass') -Raw).Trim())}
# The timestamp keeps signatures valid after the certificate expires; the service only receives a hash.
foreach($server in 'http://timestamp.digicert.com','http://timestamp.sectigo.com'){
    & $sdk sign @identity /fd sha256 /tr $server /td sha256 /d 'Culinary Services' /q $Path
    if($LASTEXITCODE -eq 0){exit 0}
}
throw "No se pudo firmar $Path."
