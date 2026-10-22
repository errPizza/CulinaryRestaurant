$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName System.Drawing
$symbol=[Drawing.Image]::FromFile((Join-Path $projectRoot 'branding\symbol-original.png'))
function RenderIcon([int]$size,[string]$relative,[double]$fraction=0.88,[bool]$opaque=$false){
 $target=Join-Path $projectRoot $relative
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
 $format=if($opaque){[Drawing.Imaging.PixelFormat]::Format24bppRgb}else{[Drawing.Imaging.PixelFormat]::Format32bppArgb}
 $bitmap=New-Object Drawing.Bitmap($size,$size,$format)
 $graphics=[Drawing.Graphics]::FromImage($bitmap)
 try{
  $graphics.Clear($(if($opaque){[Drawing.Color]::FromArgb(255,231,233,237)}else{[Drawing.Color]::Transparent}))
  $graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $graphics.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality
  $scale=($size*$fraction)/[Math]::Max($symbol.Width,$symbol.Height)
  $w=[int]($symbol.Width*$scale);$h=[int]($symbol.Height*$scale)
  $graphics.DrawImage($symbol,[int](($size-$w)/2),[int](($size-$h)/2),$w,$h)
  $bitmap.Save($target,[Drawing.Imaging.ImageFormat]::Png)
 }finally{$graphics.Dispose();$bitmap.Dispose()}
}
try{
 $sizes=@(16,24,32,48,64,128,256)
 foreach($size in $sizes){RenderIcon $size "branding\icons\icon-$size.png"}
 $ico=Join-Path $projectRoot 'branding\CulinaryRestaurant.ico'
 $stream=[IO.File]::Create($ico);$writer=New-Object IO.BinaryWriter($stream)
 try{
  $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
  $offset=6+16*$sizes.Count
  foreach($size in $sizes){$data=[IO.File]::ReadAllBytes((Join-Path $projectRoot "branding\icons\icon-$size.png"));$dimension=if($size -eq 256){0}else{$size};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$data.Length);$writer.Write([uint32]$offset);$offset+=$data.Length}
  foreach($size in $sizes){$writer.Write([IO.File]::ReadAllBytes((Join-Path $projectRoot "branding\icons\icon-$size.png")))}
 }finally{$writer.Dispose();$stream.Dispose()}
 RenderIcon 512 'branding\culinary-restaurant.png'
 # Keep every client and public surface synchronized with the supplied originals.
 foreach($relative in @('src/MesaNexo.Client/Assets','src/MesaNexo.Web/public/branding','src/MesaNexo.Public/wwwroot')){
  $assets=Join-Path $projectRoot $relative;[IO.Directory]::CreateDirectory($assets)|Out-Null
  Copy-Item -LiteralPath (Join-Path $projectRoot 'branding/symbol-original.png') -Destination (Join-Path $assets 'symbol.png') -Force
  Copy-Item -LiteralPath (Join-Path $projectRoot 'branding/wordmark-original.png') -Destination (Join-Path $assets 'wordmark.png') -Force
 }
 $tauri=Join-Path $projectRoot 'src/MesaNexo.Web/src-tauri/icons';[IO.Directory]::CreateDirectory($tauri)|Out-Null
 Copy-Item -LiteralPath $ico -Destination (Join-Path $tauri 'icon.ico') -Force
 Copy-Item -LiteralPath $ico -Destination (Join-Path $projectRoot 'src/MesaNexo.Web/public/favicon.ico') -Force
 RenderIcon 512 'src/MesaNexo.Web/src-tauri/icons/icon.png'
 foreach($publicSize in @(192,256,512)){RenderIcon $publicSize "src/MesaNexo.Public/wwwroot/icon-$publicSize.png"}

 foreach($entry in @(@('mdpi',48),@('hdpi',72),@('xhdpi',96),@('xxhdpi',144),@('xxxhdpi',192))){RenderIcon $entry[1] "src\MesaNexo.Android\Resources\mipmap-$($entry[0])\ic_launcher.png";RenderIcon ([int]($entry[1]*2.25)) "src\MesaNexo.Android\Resources\mipmap-$($entry[0])\ic_foreground.png" 0.62}
 $adaptive=Join-Path $projectRoot 'src\MesaNexo.Android\Resources\mipmap-anydpi-v26'
 [IO.Directory]::CreateDirectory($adaptive)|Out-Null
 [IO.File]::WriteAllText((Join-Path $adaptive 'ic_launcher.xml'),'<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android"><background android:drawable="@color/brand_icon_background"/><foreground android:drawable="@mipmap/ic_foreground"/></adaptive-icon>')
 $ios=Join-Path $projectRoot 'src\MesaNexo.iOS\Assets.xcassets\AppIcon.appiconset'
 [IO.Directory]::CreateDirectory($ios)|Out-Null
 RenderIcon 1024 'src\MesaNexo.iOS\Assets.xcassets\AppIcon.appiconset\AppIcon.png' 0.80 $true
 [IO.File]::WriteAllText((Join-Path $ios 'Contents.json'),'{"images":[{"filename":"AppIcon.png","idiom":"universal","platform":"ios","size":"1024x1024"}],"info":{"author":"xcode","version":1}}')
 [IO.File]::WriteAllText((Join-Path (Split-Path $ios -Parent) 'Contents.json'),'{"info":{"author":"xcode","version":1}}')
}finally{$symbol.Dispose()}
Write-Output 'Recursos de Windows, Linux, Android e iOS preparados desde el símbolo original.'