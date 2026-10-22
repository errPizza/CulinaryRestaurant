param(
 [string]$Python='C:\Users\kikin\Desktop\CulinaryOS\tools\python\python.exe',
 [switch]$Avalonia,
 [switch]$NoClient,
 [ValidateRange(1,65535)][int]$Port=8443,
 [ValidatePattern('^[a-z0-9-]{1,32}$')][string]$SiteId='manual-lab',
 [string]$LabDirectory='',
 [switch]$PassThru
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME=Join-Path $projectRoot '.dotnet'
$env:NUGET_PACKAGES=Join-Path $projectRoot '.packages'
$env:PYTHONDONTWRITEBYTECODE='1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$lab=if($LabDirectory){[IO.Path]::GetFullPath($LabDirectory)}else{Join-Path $projectRoot '.lab/manual'}
$probe=Join-Path $projectRoot 'runtime/lab_probe.py'
$hostScript=Join-Path $projectRoot 'runtime/lab_host.py'
$dotnet=(Get-Command dotnet -ErrorAction Stop).Source
$buildRoot=$null
$api=$null
$backend=$null
$backendPid=$null
function Get-LabCheck([string]$Operation){
 $raw=& $Python -B $probe $Operation --directory $lab --site $SiteId --port $Port
 $exitCode=$LASTEXITCODE
 try{$result=$raw | ConvertFrom-Json}catch{throw 'La comprobación del laboratorio no devolvió una respuesta válida.'}
 if($exitCode -ne 0 -or -not $result.ok){throw $result.error}
 return $result
}
function Publish-LabProject([string]$Project,[string]$Output){
 # Each invocation owns its outputs. Never rebuild into the directory of a live server/client.
 if(-not $script:buildRoot){$script:buildRoot=Join-Path $projectRoot ('.lab/start-builds/'+[Guid]::NewGuid().ToString('N'))}
 $destination=Join-Path $script:buildRoot $Output
 & $dotnet publish $Project -c Debug --no-self-contained "-p:ArtifactsPath=$script:buildRoot/artifacts" -o $destination --nologo | Out-Host
 if($LASTEXITCODE -ne 0){throw "No se pudo compilar $Project en su carpeta independiente."}
 return $destination
}
Push-Location $projectRoot
try{
 $database=Get-LabCheck 'database'
 $existing=Get-LabCheck 'server'
 if($existing.listening -and -not $existing.trusted){throw "El puerto $Port está ocupado. $($existing.error) No se ha iniciado otra instancia."}
 $reused=[bool]$existing.trusted
 if(-not $reused -or -not $database.initialized){
  $api=Join-Path (Publish-LabProject 'src/MesaNexo.Api' 'api') 'MesaNexo.Api.dll'
 }
 if(-not $NoClient){
  if($Avalonia){
   $client=Join-Path (Publish-LabProject 'src/MesaNexo.Desktop' 'client') 'CulinaryRestaurant.exe'
  }else{
   $client=Join-Path $projectRoot 'artifacts/desktop-css/CulinaryRestaurant.exe'
   if(-not(Test-Path -LiteralPath $client)){& (Join-Path $projectRoot 'deploy/Build-Hybrid.ps1')}
  }
 }
 if(-not $database.initialized){
  New-Item -ItemType Directory -Path $lab -Force | Out-Null
  $username=Read-Host 'Elige tu usuario de gerente'
  $name=Read-Host 'Nombre del restaurante de laboratorio'
  $secure=Read-Host 'Contraseña propia (12 a 200 caracteres)' -AsSecureString
  $ptr=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
  $initial=Join-Path $lab ('initialize-'+[Guid]::NewGuid().ToString('N')+'-private.json')
  $code=[Guid]::NewGuid().ToString('N')
  try{
   $password=[Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
   $config=@{siteId=$SiteId;name=$name;mode='licencia';managerName='Gerente';managerUsername=$username;managerPassword=$password;enrollmentCode=$code;modules=@('reservations','reviews','customerRequests','printing','courses','promotions');maxUsers=15;maxTables=30}
   # Restrict the empty temporary file before writing the provisioning password.
   $file=[IO.File]::Open($initial,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$file.Dispose()
   $acl=New-Object Security.AccessControl.FileSecurity
   $acl.SetAccessRuleProtection($true,$false)
   $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
   $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','Allow')))
   Set-Acl -LiteralPath $initial -AclObject $acl
   [IO.File]::WriteAllText($initial,($config|ConvertTo-Json -Depth 8),(New-Object System.Text.UTF8Encoding($false)))
   & $dotnet $api --lab --site $SiteId --python $Python --lab-script $hostScript --lab-directory $lab --initialize $initial
   if($LASTEXITCODE -ne 0){throw 'Inicialización rechazada; se conserva el laboratorio existente.'}
  }finally{
   [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr);$password=$null;$config=$null
   if(Test-Path -LiteralPath $initial){Remove-Item -LiteralPath $initial}
  }
  Write-Host "Código de alta de un solo uso (para tu primera instalación): $code"
 }
 if($reused){
  Write-Host "Reutilizando el servidor HTTPS de confianza en el puerto $Port."
  try{$backendPid=(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1).OwningProcess}catch{}
 }else{
  $arguments='"'+$api+'" --lab --site '+$SiteId+' --port '+$Port+' --python "'+$Python+'" --lab-script "'+$hostScript+'" --lab-directory "'+$lab+'"'
  $stdout=Join-Path $buildRoot 'backend.stdout.log';$stderr=Join-Path $buildRoot 'backend.stderr.log'
  $backend=Start-Process -FilePath $dotnet -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
  $backendPid=$backend.Id
 }
 # A stale certificate file alone does not prove that the backend started or is ready.
 $deadline=[DateTime]::UtcNow.AddSeconds(30)
 do{
  if($backend){$backend.Refresh();if($backend.HasExited){throw "El backend terminó al arrancar. Revisa $buildRoot/backend.stderr.log. No se cerró ningún otro proceso."}}
  $available=Get-LabCheck 'server'
  if($available.trusted -and $available.ready){break}
  if([DateTime]::UtcNow -ge $deadline){throw "El backend no confirmó disponibilidad con TLS e identidad válidos. Revisa el laboratorio y sus registros. Puerto: $Port."}
  Start-Sleep -Milliseconds 250
 }while($true)
 $ca=Join-Path $lab 'lab-ca.crt'
 $fingerprint=Get-LabCheck 'fingerprint'
 Write-Host "Solo laboratorio local: https://localhost:$Port, identidad $SiteId."
 Write-Host "CA pública: $ca"
 Write-Host "SHA-256 de CA: $($fingerprint.sha256)"
 if($backendPid){Write-Host "Backend independiente de la interfaz. PID: $backendPid."}
 if(-not $NoClient){Start-Process -FilePath $client | Out-Null}
 if($PassThru){
  $result=[pscustomobject]@{Reused=$reused;BackendPid=$backendPid;Port=$Port;SiteId=$SiteId;LabDirectory=$lab;BuildRoot=$buildRoot;ApiAssembly=$api;Ready=$true}
  $result.PSObject.TypeNames.Insert(0,'Culinary.Lab.Startup');Write-Output $result
 }
}finally{Pop-Location}
