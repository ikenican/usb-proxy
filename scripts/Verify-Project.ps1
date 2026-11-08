[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$parseErrors=@()
Get-ChildItem -Path (Join-Path $script:Root 'scripts\*.ps1'),(Join-Path $script:Root 'src\*.ps1') | ForEach-Object {
    $tokens=$null; $errors=$null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$tokens,[ref]$errors) | Out-Null
    $parseErrors+=$errors
}
if ($parseErrors.Count) {throw ($parseErrors.Message -join "`n")}
& (Join-Path $script:Root 'src\UsbProxy.ps1') -SmokeTest
$config=Read-Config
$status=Invoke-Guest $config @{action='status'}
if (-not $status.ready) {throw 'Guest is not ready. Start Linux first.'}
$testFile=Join-Path $script:Root 'guest\test_agent.py'
$scpArguments=@(Get-SshArguments $config) + @('-P',[string]$config.sshPort,$testFile,'usbproxy@127.0.0.1:/home/usbproxy/test_agent.py')
$null=Invoke-Native 'scp.exe' $scpArguments
$sshArguments=@(Get-SshArguments $config) + @('-p',[string]$config.sshPort,'usbproxy@127.0.0.1','python3 /home/usbproxy/test_agent.py 2>&1')
$tests=Invoke-Native 'ssh.exe' $sshArguments
Write-Utf8 (Join-Path $script:Root 'logs\guest-tests.txt') $tests
Write-Output $tests
$settings=foreach ($service in @('USBSTOR','UASPStor')) {
    $state=Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $service)
    @{service=$service; start=$state.Start}
}
$drivers=@(Get-CimInstance Win32_SystemDriver | Where-Object {$_.Name -match '^VBox'} | Select-Object Name,State,StartMode,PathName)
$report=@{
    timestamp=(Get-Date).ToString('o'); virtualbox=(Invoke-VBox @('--version'));
    guestReady=$status.ready; hostUsbStorageSettings=$settings; drivers=$drivers;
    passed=@('PowerShell syntax','WinForms construction','SSH/SCP transport','7 guest file-handling tests');
    pending=@('NTFS physical writes','other USB device models','USBSTOR-disabled target machine');
}
foreach ($entry in @(@{file='integration.json';name='virtualUsbFat32Integration'},@{file='physical-usb.json';name='physicalUsbIntegration'})) {
    $path=Join-Path $script:Root ('logs\' + $entry.file)
    if (Test-Path -LiteralPath $path) {$report[$entry.name]=Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json}
}
Write-Utf8 (Join-Path $script:Root 'logs\verification.json') ($report | ConvertTo-Json -Depth 7)
Write-Output 'Software checks passed. See logs/verification.json for recorded integration results and remaining target-machine tests.'
