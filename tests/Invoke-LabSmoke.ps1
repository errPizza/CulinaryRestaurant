param([string]$Python,[string]$LabDirectory,[string]$SiteId,[int]$Port)
$ErrorActionPreference='Stop'
$result=& (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/Start-Lab.ps1') -Python $Python -LabDirectory $LabDirectory -SiteId $SiteId -Port $Port -NoClient -PassThru
Write-Output ('LAB_RESULT_JSON:'+($result|ConvertTo-Json -Compress))
