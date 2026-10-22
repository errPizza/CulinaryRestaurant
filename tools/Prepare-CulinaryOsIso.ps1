param([ValidateRange(1,99)][int]$Revision=1)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$revisionTag=if($Revision -eq 1){''}else{"-r$Revision"}
$stage=(Resolve-Path -LiteralPath (Join-Path $projectRoot "artifacts/culinaryos-cd$revisionTag")).Path
$isoName="CulinaryRestaurant-CulinaryOS-0.3.0$revisionTag.iso"
$output=Join-Path $projectRoot "artifacts/$isoName"
if(Test-Path -LiteralPath $output){throw 'No se sobrescribe una ISO existente.'}
Add-Type -TypeDefinition @"
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class CulinaryApplicationIso {
    public static void Save(object input, string output) {
        var stream=(IStream)input;
        IntPtr counter=Marshal.AllocHGlobal(4);
        try {
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write)) {
                byte[] buffer=new byte[65536];
                while(true) {
                    Marshal.WriteInt32(counter,0);stream.Read(buffer,buffer.Length,counter);
                    int n=Marshal.ReadInt32(counter);if(n==0)break;
                    file.Write(buffer,0,n);
                }
                file.Flush(true);
            }
        } finally {Marshal.FreeHGlobal(counter);}
    }
}
"@
$image=New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$image.FileSystemsToCreate=3
$image.VolumeName='CULINARY_APP'
$image.Root.AddTree($stage,$false)
$result=$image.CreateResultImage()
[CulinaryApplicationIso]::Save($result.ImageStream,$output)
$hash=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$output.sha256", "$hash  $isoName"+[char]10,(New-Object Text.UTF8Encoding($false)))
Get-Item -LiteralPath $output | Select-Object FullName,Length
