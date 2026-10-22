$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing
$origin=Get-Content -LiteralPath (Join-Path $projectRoot 'branding/origin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Hash([string]$relative){return (Get-FileHash -LiteralPath (Join-Path $projectRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()}
$symbol=Hash 'branding/symbol-original.png';$wordmark=Hash 'branding/wordmark-original.png'
if($symbol -ne $origin.symbolSha256.ToLowerInvariant() -or $wordmark -ne $origin.wordmarkSha256.ToLowerInvariant()){throw 'Las fuentes no coinciden con la procedencia registrada.'}
foreach($folder in @('src/MesaNexo.Client/Assets','src/MesaNexo.Web/public/branding','src/MesaNexo.Public/wwwroot')){
 if((Hash "$folder/symbol.png") -ne $symbol -or (Hash "$folder/wordmark.png") -ne $wordmark){throw "Los recursos de $folder difieren del original."}
}
$checked=New-Object 'System.Collections.Generic.List[string]'
function CheckPng([string]$relative,[int]$width,[int]$height,[bool]$opaque=$false){
 $path=Join-Path $projectRoot $relative;$bytes=[IO.File]::ReadAllBytes($path)
 if([BitConverter]::ToString($bytes,0,8) -ne '89-50-4E-47-0D-0A-1A-0A'){throw "PNG inválido: $relative"}
 $png=[Drawing.Image]::FromFile($path)
 try{if($png.Width -ne $width -or $png.Height -ne $height){throw "Dimensiones inválidas: $relative"}}finally{$png.Dispose()}
 if($opaque -and $bytes[25] -ne 2){throw 'El AppIcon de iOS debe ser RGB sin canal alfa.'}
 $checked.Add($relative)
}
$sizes=@(16,24,32,48,64,128,256)
foreach($size in $sizes){CheckPng "branding/icons/icon-$size.png" $size $size}
$icoPath=Join-Path $projectRoot 'branding/CulinaryRestaurant.ico';$ico=[IO.File]::ReadAllBytes($icoPath)
if([BitConverter]::ToUInt16($ico,0) -ne 0 -or [BitConverter]::ToUInt16($ico,2) -ne 1 -or [BitConverter]::ToUInt16($ico,4) -ne $sizes.Count){throw 'Se requiere un ICO real con siete tamaños, no un PNG renombrado.'}
for($index=0;$index -lt $sizes.Count;$index++){
 $entry=6+16*$index;$dimension=if($sizes[$index] -eq 256){0}else{$sizes[$index]}
 $length=[BitConverter]::ToUInt32($ico,$entry+8);$offset=[BitConverter]::ToUInt32($ico,$entry+12)
 if($ico[$entry] -ne $dimension -or $ico[$entry+1] -ne $dimension -or $offset+$length -gt $ico.Length -or $ico[$offset] -ne 137){throw 'Entrada ICO inválida.'}
}
$icoHash=Hash 'branding/CulinaryRestaurant.ico'
foreach($relative in @('src/MesaNexo.Web/src-tauri/icons/icon.ico','src/MesaNexo.Web/public/favicon.ico')){if((Hash $relative) -ne $icoHash){throw "ICO desactualizado: $relative"}}
CheckPng 'branding/culinary-restaurant.png' 512 512
CheckPng 'src/MesaNexo.Web/src-tauri/icons/icon.png' 512 512
foreach($size in @(192,256,512)){CheckPng "src/MesaNexo.Public/wwwroot/icon-$size.png" $size $size}
foreach($entry in @(@('mdpi',48),@('hdpi',72),@('xhdpi',96),@('xxhdpi',144),@('xxxhdpi',192))){CheckPng "src/MesaNexo.Android/Resources/mipmap-$($entry[0])/ic_launcher.png" $entry[1] $entry[1];$foreground=[int]($entry[1]*2.25);CheckPng "src/MesaNexo.Android/Resources/mipmap-$($entry[0])/ic_foreground.png" $foreground $foreground}
CheckPng 'src/MesaNexo.iOS/Assets.xcassets/AppIcon.appiconset/AppIcon.png' 1024 1024 $true
$manifest=[xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/MesaNexo.Android/Properties/AndroidManifest.xml') -Raw -Encoding UTF8)
$ns=New-Object Xml.XmlNamespaceManager($manifest.NameTable);$ns.AddNamespace('android','http://schemas.android.com/apk/res/android')
if($manifest.SelectSingleNode('/manifest/application/@android:label',$ns).Value -ne 'Culinary Restaurant'){throw 'Nombre Android incorrecto.'}
$web=Get-Content -LiteralPath (Join-Path $projectRoot 'src/MesaNexo.Web/src/main.tsx') -Raw -Encoding UTF8
if($web -match '<img className="brand-wordmark"'){throw 'El logo grande todavía está en el login CSS.'}
$report=@{name='Culinary Restaurant';sourceSymbolSha256=$symbol;sourceWordmarkSha256=$wordmark;embeddedAssetsMatchOriginal=$true;icoSizes=$sizes;realIco=$true;pngAssetsVerified=$checked.ToArray();loginLargeLogoRemoved=$true;iosNoAlpha=$true;androidAdaptiveSourceVerified=$true;deviceExecution=$false;iosCompiled=$false}
[IO.File]::WriteAllText((Join-Path $projectRoot 'evidence/branding-current.json'),($report|ConvertTo-Json -Depth 6),(New-Object System.Text.UTF8Encoding($false)))
Write-Output "Marca verificada: fuentes exactas, ICO real de 7 tamaños y $($checked.Count) PNG de plataforma."
