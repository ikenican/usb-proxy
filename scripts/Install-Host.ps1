[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $log = Join-Path $script:Root ('logs\host-setup-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.txt')
    $command = "Start-Transcript -LiteralPath '" + $log.Replace("'","''") + "' -Force; try { & '" + $PSCommandPath.Replace("'","''") + "'; exit 0 } catch { Write-Output `$_.Exception.ToString(); exit 1 } finally { Stop-Transcript }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    $process = Start-Process 'powershell.exe' -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Elevated setup failed. See $log" }
    Get-Content -LiteralPath $log
    exit 0
}

$installer = Join-Path $script:Root 'downloads\VirtualBox-7.2.22-175245-Win.exe'
$checksumFile = Join-Path $script:Root 'downloads\virtualbox-SHA256SUMS'
$expected = (Get-Content $checksumFile | Where-Object { $_ -match '\s\*?VirtualBox-7\.2\.22-175245-Win\.exe$' }) -split '\s+'
if (-not $expected -or (Get-FileHash $installer -Algorithm SHA256).Hash -ne $expected[0]) { throw 'Installer SHA256 mismatch.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Oracle') { throw 'Invalid Oracle installer signature.' }
if (Test-Path (Join-Path $env:ProgramFiles 'Oracle\VirtualBox\VBoxManage.exe')) {
    Write-Output 'VirtualBox already installed; leaving it unchanged.'
    exit 0
}
$backup = Join-Path $script:Root ('backups\preinstall-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$keys = @('HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR',
    'HKLM\SYSTEM\CurrentControlSet\Services\UASPStor',
    'HKLM\SYSTEM\CurrentControlSet\Control\Class\{36FC9E60-C465-11CF-8056-444553540000}',
    'HKLM\SOFTWARE\Oracle\VirtualBox', 'HKCU\Software\Oracle\VirtualBox')
foreach ($service in @('VBoxSup','VBoxUSB','VBoxUSBMon','VBoxSDS','VBoxNetLwf','VBoxNetAdp6')) {
    $keys += 'HKLM\SYSTEM\CurrentControlSet\Services\' + $service
}
$manifest = foreach ($key in $keys) {
    $providerKey = $key.Replace('HKLM\','Registry::HKEY_LOCAL_MACHINE\').Replace('HKCU\','Registry::HKEY_CURRENT_USER\')
    $exists = Test-Path -LiteralPath $providerKey
    $name = ($key -replace '[^A-Za-z0-9{}-]', '_') + '.reg'
    if ($exists) {
        & reg.exe export $key (Join-Path $backup $name) /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "Cannot back up $key" }
    }
    [pscustomobject]@{key=$key; existed=$exists; file=$(if ($exists) {$name} else {$null})}
}
Write-Utf8 (Join-Path $backup 'registry-manifest.json') ($manifest | ConvertTo-Json -Depth 5)
Get-CimInstance Win32_SystemDriver | Select-Object Name,State,StartMode,PathName |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $backup 'system-drivers.json') -Encoding UTF8
Get-ExecutionPolicy -List | Out-File (Join-Path $backup 'execution-policies.txt')
& bcdedit.exe /enum 2>&1 | Out-File (Join-Path $backup 'boot-config.txt')
& pnputil.exe /enum-drivers 2>&1 | Out-File (Join-Path $backup 'driver-store.txt')
Write-Utf8 (Join-Path $backup 'changes.json') (@{
    installer=$installer; sha256=(Get-FileHash $installer -Algorithm SHA256).Hash;
    features=@('VBoxApplication','VBoxUSB');
    unchanged=@('USBSTOR','UASPStor','SecureBoot','MemoryIntegrity','ExecutionPolicy','WindowsFeatures');
    note='Registry snapshots are not a full Windows image. Uninstall VirtualBox using Windows Installed Apps; do not blindly import class-wide registry snapshots.'
} | ConvertTo-Json -Depth 5)

$extract = Join-Path $script:Root 'downloads\virtualbox-msi'
New-Item -ItemType Directory -Path $extract -Force | Out-Null
$existingMsi = @(Get-ChildItem $extract -Filter '*amd64.msi')
if ($existingMsi.Count -eq 0) {
    Write-Output (Invoke-Native $installer @('-extract','-path',$extract))
}
$msi = @(Get-ChildItem $extract -Filter '*.msi' | Where-Object { $_.Name -notmatch 'arm64|x86' })
if ($msi.Count -ne 1) { throw 'Expected exactly one x64 MSI.' }
$msiSignature = Get-AuthenticodeSignature -LiteralPath $msi[0].FullName
if ($msiSignature.Status -ne 'Valid' -or $msiSignature.SignerCertificate.Subject -notmatch 'Oracle') { throw 'Invalid Oracle MSI signature.' }
$log = Join-Path $backup 'installation.log'
$args = @('/i',$msi[0].FullName,'/qn','/norestart','ADDLOCAL=VBoxApplication,VBoxUSB',
    'VBOX_INSTALLDESKTOPSHORTCUT=0','VBOX_INSTALLQUICKLAUNCHSHORTCUT=0',
    'VBOX_START=0','/L*v',$log)
Write-Output "Backup complete: $backup"
Write-Output 'Installing official VirtualBox application + USB support. Windows may show a UAC prompt.'
$p = Start-Process -FilePath 'msiexec.exe' -ArgumentList (($args | ForEach-Object { Quote-Argument $_ }) -join ' ') -Verb RunAs -WindowStyle Hidden -Wait -PassThru
Write-Utf8 (Join-Path $backup 'installation-result.json') (@{exitCode=$p.ExitCode; timestamp=(Get-Date).ToString('o')} | ConvertTo-Json)
if ($p.ExitCode -notin @(0,3010)) { throw "Installation failed: $($p.ExitCode). See $log" }
if ($p.ExitCode -eq 3010) { throw 'Installation succeeded but requires a Windows restart. Restart before preparing VM.' }
Write-Output ('Installed: ' + (Invoke-VBox @('--version')))
