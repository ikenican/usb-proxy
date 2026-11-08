# Updates only the dedicated VM via its NoCloud seed; requires a clean shutdown.
. "$PSScriptRoot\Common.ps1"
$config=Read-Config
if ($config.attachedUsb) {throw 'Safely eject the USB device first.'}
$deadline=(Get-Date).AddSeconds(120)
while ((Invoke-VBox @('showvminfo',$config.vmName,'--machinereadable')) -notmatch '(?m)^VMState="poweroff"') {
    if ((Get-Date) -gt $deadline) {throw 'Shut down Linux before updating its seed.'}
    Start-Sleep 2
}
$null=& "$PSScriptRoot\New-GuestSeed.ps1"
$config | Add-Member -NotePropertyName shutdownRequestedAt -NotePropertyValue $null -Force
Write-Utf8 $script:ConfigFile ($config | ConvertTo-Json -Depth 8)
$null=Invoke-VBox @('startvm',$config.vmName,'--type','headless')
$expected=(Get-FileHash -LiteralPath (Join-Path $script:Root 'guest\usbproxy-agent.py') -Algorithm SHA256).Hash.ToLowerInvariant()
$arguments=@(Get-SshArguments $config) + @('-p',[string]$config.sshPort,'usbproxy@127.0.0.1','sha256sum /usr/local/sbin/usbproxy-agent')
$deadline=(Get-Date).AddMinutes(8)
while ((Get-Date) -lt $deadline) {
    try {
        $actual=Invoke-Native 'ssh.exe' $arguments
        if (($actual -split '\s+')[0] -eq $expected) {
            $state=Invoke-Guest $config @{action='status'}
            if ($state.ready) {Write-Output 'Guest agent updated and SHA256 verified.'; return}
        }
    } catch { }
    Start-Sleep 3
}
throw 'Guest update not confirmed. Inspect the VM/cloud-init before using USB.'
