# Development and tests only: the official EDB build of PostgreSQL for Windows in .tools/postgres. The lab and the
# test suite start private clusters from it (src/MesaNexo.Api/LabPostgres.cs). CulinaryOS uses Debian's package.
$ErrorActionPreference='Stop'
$version='17.11-1'
$sha256='6eabdf00d2893713b75db4336a23c3fdf505f056e217ec6e2e95d901750cfea3'
$root=Split-Path $PSScriptRoot -Parent
$target=Join-Path $root '.tools/postgres'
if(Test-Path -LiteralPath (Join-Path $target 'pgsql/bin/postgres.exe')){Write-Host 'PostgreSQL ya está en .tools/postgres.';return}
$download=Join-Path $root '.tools/pg-download'
New-Item -ItemType Directory -Force -Path $download | Out-Null
$zip=Join-Path $download "postgresql-$version-windows-x64-binaries.zip"
if(-not(Test-Path -LiteralPath $zip)){Invoke-WebRequest -UseBasicParsing -Uri "https://get.enterprisedb.com/postgresql/postgresql-$version-windows-x64-binaries.zip" -OutFile $zip}
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha256){Remove-Item -LiteralPath $zip;throw 'El paquete de PostgreSQL no coincide con su SHA-256.'}
Expand-Archive -LiteralPath $zip -DestinationPath $target -Force
Write-Host "PostgreSQL $version listo en .tools/postgres."
