param(
 [string]$Python='C:\Users\kikin\Desktop\CulinaryOS\tools\python\python.exe',
 [ValidateRange(1,12)][int]$Waiters=6,
 [ValidateRange(1,30)][int]$Tables=24,
 [double]$Minutes=15,
 [int]$PollMs=2000,
 [int]$ThinkMs=600,
 [string]$Output=''
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$env:PYTHONDONTWRITEBYTECODE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
if(-not $Output){$Output=Join-Path $projectRoot 'evidence/load-baseline.json'}
Push-Location $projectRoot
try{
 dotnet run --project tests/MesaNexo.Load -c Release -- --root $projectRoot --python $Python --waiters $Waiters --tables $Tables --minutes $Minutes --poll-ms $PollMs --think-ms $ThinkMs --output $Output
 if($LASTEXITCODE -ne 0){throw 'La prueba de carga detectó un fallo de integridad.'}
}finally{Pop-Location}
