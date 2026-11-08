# Uses only runtime/validation/fat32.vdi. No physical USB device is captured.
. "$PSScriptRoot\Common.ps1"
$config=Read-Config
$validation=Join-Path $script:Runtime 'validation'
$vdi=Join-Path $validation 'fat32.vdi'
if (-not (Test-Path $vdi)) {throw 'Run New-ValidationDisk.ps1 first.'}

function Worker-Request($Request) {
    $id=[guid]::NewGuid().ToString('N')
    $inputFile=Join-Path $validation ($id + '.request.json')
    $outputFile=Join-Path $validation ($id + '.response.json')
    Write-Utf8 $inputFile ($Request | ConvertTo-Json -Depth 7)
    & "$PSScriptRoot\Worker.ps1" -RequestFile $inputFile -ResponseFile $outputFile
    $response=Get-Content -Raw -Encoding UTF8 -LiteralPath $outputFile | ConvertFrom-Json
    if (-not $response.ok) {throw $response.error}
    return $response.data
}

$info=Invoke-VBox @('showvminfo',$config.vmName,'--machinereadable')
if ($info -match '(?m)^"ValidationUSB-0-0"="([^"]+)"' -and $Matches[1] -ne 'none') {
    if ($Matches[1].Replace('\\','\') -ne $vdi) {throw 'Validation controller contains an unexpected disk.'}
} else {
    $deadline=(Get-Date).AddSeconds(100)
    while ((Invoke-VBox @('showvminfo',$config.vmName,'--machinereadable')) -notmatch '(?m)^VMState="poweroff"') {
        if ((Get-Date) -gt $deadline) {throw 'VM did not shut down; validation disk was not attached.'}
        Start-Sleep 2
    }
    if ($info -notmatch 'storagecontrollername\d+="ValidationUSB"') {
        $null=Invoke-VBox @('storagectl',$config.vmName,'--name','ValidationUSB','--add','usb','--controller','USB','--bootable','off')
    }
    $null=Invoke-VBox @('storageattach',$config.vmName,'--storagectl','ValidationUSB','--port','0','--device','0','--type','hdd','--medium',$vdi)
}
$null=Worker-Request @{action='start'}
$volumes=@((Invoke-Guest $config @{action='status'}).volumes)
$volume=@($volumes | Where-Object {$_.uuid -eq '0135-28F4' -and $_.size -eq 134217728 -and $_.fstype -eq 'vfat'})
if ($volume.Count -ne 1) {throw ('Expected one emulated FAT32 USB volume: ' + ($volumes | ConvertTo-Json -Depth 5))}
Write-Output ('Emulated USB volume: ' + $volume[0].path)
$fixture=Join-Path $validation ('中文測試資料夾-' + [guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Write-Utf8 (Join-Path $fixture '中文 with spaces.txt') "USB Proxy integration`n中文內容`n"
[IO.File]::WriteAllBytes((Join-Path $fixture 'empty.bin'),(New-Object byte[] 0))
$bytes=New-Object byte[] (1MB)
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($bytes); $rng.Dispose()
[IO.File]::WriteAllBytes((Join-Path $fixture 'binary.bin'),$bytes)
$result=Worker-Request @{action='copy'; device=$volume[0].path; paths=@($fixture)}
if (-not $result.verified -or @($result.copied).Count -ne 3) {throw 'Unexpected copy result.'}
Write-Output ('Copy and read-back verification passed: ' + $result.bytes + ' bytes')
$conflictRejected=$false
try {$null=Worker-Request @{action='copy'; device=$volume[0].path; paths=@($fixture)}}
catch {
    if ($_.Exception.Message -notmatch 'already exists') {throw}
    $conflictRejected=$true
}
if (-not $conflictRejected) {throw 'Existing destination was not rejected.'}
$null=Worker-Request @{action='eject'}
$null=Invoke-VBox @('storageattach',$config.vmName,'--storagectl','ValidationUSB','--port','0','--device','0','--type','hdd','--medium','none')
Write-Utf8 (Join-Path $script:Root 'logs\integration.json') (@{
    timestamp=(Get-Date).ToString('o'); kind='VirtualBox emulated USB FAT32 disk';
    copied=$result.copied; bytes=$result.bytes; sha256Verified=$result.verified;
    existingFilesRejected=$conflictRejected; unmounted=$true; virtualDiskDetached=$true;
    physicalUsbPassthroughTested=$false; disabledUsbStorTested=$false
} | ConvertTo-Json -Depth 7)
Write-Output 'Integration passed. No physical USB device was captured or formatted.'
