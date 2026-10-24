# Sets the CulinaryServer root public key that MesaNexo.Core compiles in (MSBuild reads CulinaryRootPublicKey from
# the environment). Releases need the provider's key from CulinaryServer's scripts/generate-secrets.mjs; the test
# key in tests/keys signs licenses in the tests only, and a release built with it would accept forged licenses.
param([string]$Path,[switch]$Test)
$ErrorActionPreference='Stop'
$testKey=Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/keys/root-public.pem'
if($Test){$Path=$testKey}
if(-not $Path -or -not(Test-Path -LiteralPath $Path)){throw 'Indica -RootPublicKey con secrets/root-public.pem de CulinaryServer.'}
$Path=(Resolve-Path -LiteralPath $Path).Path
$der=[Convert]::FromBase64String((((Get-Content -LiteralPath $Path) | Where-Object { $_ -notmatch '^-----' }) -join ''))
if($der.Length -ne 44 -or ([BitConverter]::ToString($der[0..11]) -ne '30-2A-30-05-06-03-2B-65-70-03-21-00')){throw 'La llave raíz debe ser una pública Ed25519 en PEM.'}
$isTest=(Get-FileHash -LiteralPath $Path).Hash -eq (Get-FileHash -LiteralPath $testKey).Hash
if($isTest -and -not $Test){throw 'Esa es la llave de pruebas: una publicación con ella aceptaría licencias falsas.'}
$env:CulinaryRootPublicKey=$Path
