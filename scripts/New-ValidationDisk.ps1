# Creates a fresh FAT32 superfloppy inside a regular project file, never a device.
. "$PSScriptRoot\Common.ps1"
$directory=Join-Path $script:Runtime 'validation'
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$raw=Join-Path $directory 'fat32.raw'
if (Test-Path $raw) {throw 'Validation image already exists; will not overwrite.'}
$stream=[IO.File]::Open($raw,[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    $stream.SetLength(128MB)
    $boot=New-Object byte[] 512
    function Put16([byte[]]$Buffer,[int]$Offset,[uint16]$Value) {[BitConverter]::GetBytes($Value).CopyTo($Buffer,$Offset)}
    function Put32([byte[]]$Buffer,[int]$Offset,[uint32]$Value) {[BitConverter]::GetBytes($Value).CopyTo($Buffer,$Offset)}
    $boot[0]=0xEB; $boot[1]=0x58; $boot[2]=0x90
    [Text.Encoding]::ASCII.GetBytes('MSDOS5.0').CopyTo($boot,3)
    Put16 $boot 11 512; $boot[13]=1; Put16 $boot 14 32; $boot[16]=2
    $boot[21]=0xF8; Put16 $boot 24 63; Put16 $boot 26 255
    Put32 $boot 32 262144; Put32 $boot 36 2048; Put32 $boot 44 2
    Put16 $boot 48 1; Put16 $boot 50 6
    $boot[64]=0x80; $boot[66]=0x29; Put32 $boot 67 20261108
    [Text.Encoding]::ASCII.GetBytes('USBPROXYTST').CopyTo($boot,71)
    [Text.Encoding]::ASCII.GetBytes('FAT32   ').CopyTo($boot,82)
    $boot[510]=0x55; $boot[511]=0xAA
    $fsinfo=New-Object byte[] 512
    Put32 $fsinfo 0 0x41615252; Put32 $fsinfo 484 0x61417272
    Put32 $fsinfo 488 ([uint32]::MaxValue); Put32 $fsinfo 492 3
    $fsinfo[510]=0x55; $fsinfo[511]=0xAA
    foreach ($sector in @(0,6)) {$stream.Position=$sector*512; $stream.Write($boot,0,512)}
    foreach ($sector in @(1,7)) {$stream.Position=$sector*512; $stream.Write($fsinfo,0,512)}
    $fat=New-Object byte[] 512
    Put32 $fat 0 0x0FFFFFF8; Put32 $fat 4 ([uint32]::MaxValue); Put32 $fat 8 0x0FFFFFFF
    foreach ($sector in @(32,2080)) {$stream.Position=$sector*512; $stream.Write($fat,0,512)}
    $root=New-Object byte[] 512
    [Text.Encoding]::ASCII.GetBytes('USBPROXYTST').CopyTo($root,0)
    $root[11]=0x08
    $stream.Position=4128*512; $stream.Write($root,0,512)
    $stream.Flush($true)
} finally {$stream.Dispose()}
$vdi=Join-Path $directory 'fat32.vdi'
$null=Invoke-Native (Get-VBox) @('convertfromraw',$raw,$vdi,'--format','VDI')
Write-Output $vdi
