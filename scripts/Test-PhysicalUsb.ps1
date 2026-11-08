param([Parameter(Mandatory=$true)][string]$SerialNumber,
      [Parameter(Mandatory=$true)][string]$HostDriveLetter)
. "$PSScriptRoot\Common.ps1"
$config=Read-Config
$jobs=Join-Path $script:Runtime 'physical-validation'
New-Item -ItemType Directory -Path $jobs -Force | Out-Null
function Request($Body) {
    $id=[guid]::NewGuid().ToString('N')
    $inputPath=Join-Path $jobs ($id + '.request.json')
    $outputPath=Join-Path $jobs ($id + '.response.json')
    Write-Utf8 $inputPath ($Body | ConvertTo-Json -Depth 7)
    & "$PSScriptRoot\Worker.ps1" -RequestFile $inputPath -ResponseFile $outputPath
    $response=Get-Content -Raw -Encoding UTF8 -LiteralPath $outputPath | ConvertFrom-Json
    if (-not $response.ok) {throw $response.error}
    return $response.data
}
$disk=@(Get-Disk | Where-Object {$_.BusType -eq 'USB' -and $_.SerialNumber.Trim() -eq $SerialNumber})
if ($disk.Count -ne 1 -or $disk[0].IsBoot -or $disk[0].IsSystem) {throw 'Expected one non-system USB disk with the exact serial.'}
$volume=Get-Volume -DriveLetter $HostDriveLetter
$partition=Get-Partition -DriveLetter $HostDriveLetter
if ($partition.DiskNumber -ne $disk[0].Number) {throw 'Drive letter does not belong to the selected USB disk.'}
$folderName='USBProxy-Test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
$hostDestination=$HostDriveLetter + ':\' + $folderName
if (Test-Path -LiteralPath $hostDestination) {throw 'Test destination already exists.'}
$fixture=Join-Path $jobs $folderName
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Write-Utf8 (Join-Path $fixture '中文測試 with spaces.txt') "USB Proxy physical USB validation`n這是獨立的測試資料夾。`n"
$bytes=New-Object byte[] (256KB)
$rng=[Security.Cryptography.RandomNumberGenerator]::Create(); $rng.GetBytes($bytes); $rng.Dispose()
[IO.File]::WriteAllBytes((Join-Path $fixture 'binary.bin'),$bytes)
$deadline=(Get-Date).AddSeconds(100)
while ((Request @{action='status'}).vm.state -eq 'stopping') {
    if ((Get-Date) -gt $deadline) {throw 'Wait for the previous Linux shutdown to finish.'}
    Start-Sleep 2
}
$null=Request @{action='start'}
$status=Request @{action='status'}
$usb=@($status.usb | Where-Object {$_.SerialNumber -eq $SerialNumber})
if ($usb.Count -ne 1) {throw 'Expected one enumerated USB storage device with the exact serial.'}
$null=Request @{action='remember'; location=$usb[0].location}
$attached=$false
try {
    $attachedResult=Request @{action='attach'; uuid=$usb[0].uuid}
    $attached=$true
    $guestVolumes=@($attachedResult.guest.volumes)
    $deadline=(Get-Date).AddSeconds(45)
    do {
        $target=@($guestVolumes | Where-Object {$_.size -eq $partition.Size -and $_.fstype -eq 'exfat'})
        if ($target.Count -eq 1) {break}
        if ((Get-Date) -gt $deadline) {break}
        Start-Sleep 3
        $guestVolumes=@((Request @{action='status'}).guest.volumes)
    } while ($true)
    if ($target.Count -ne 1) {throw ('Cannot unambiguously identify the expected exFAT partition: ' + ($guestVolumes | ConvertTo-Json -Depth 6))}
    Write-Output ('Captured physical USB: ' + $target[0].path)
    $copy=Request @{action='copy'; device=$target[0].path; paths=@($fixture)}
    if (-not $copy.verified) {throw 'Guest read-back verification failed.'}
    Write-Output ('Guest copy + SHA256 passed: ' + $copy.bytes + ' bytes')
} finally {
    if ($attached) {$null=Request @{action='eject'}}
}
$deadline=(Get-Date).AddSeconds(30)
while (-not (Test-Path -LiteralPath $hostDestination)) {
    if ((Get-Date) -gt $deadline) {throw 'USB was released but Windows drive letter did not return in time.'}
    Start-Sleep 1
}
$hashes=foreach ($file in Get-ChildItem -LiteralPath $fixture -File) {
    $sourceHash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $destinationHash=(Get-FileHash -LiteralPath (Join-Path $hostDestination $file.Name) -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {throw ('Host read-back hash mismatch: ' + $file.Name)}
    @{file=$file.Name; sha256=$sourceHash}
}
Write-Utf8 (Join-Path $script:Root 'logs\physical-usb.json') (@{
    timestamp=(Get-Date).ToString('o'); serial=$SerialNumber; device=$disk[0].FriendlyName;
    filesystem=$volume.FileSystem; capacity=$disk[0].Size; testFolder=$hostDestination;
    copied=$copy.copied; bytes=$copy.bytes; guestVerified=$true; hostReadbackVerified=$true;
    releasedToWindows=$true; rememberedPort=$usb[0].location; hashes=@($hashes);
    formatted=$false; existingFilesOverwritten=$false; disabledUsbStorTested=$false
} | ConvertTo-Json -Depth 8)
Write-Output ('Physical USB integration passed. Test folder retained: ' + $hostDestination)
