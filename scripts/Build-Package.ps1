# A clean target-machine package: no development VM, SSH keys, user data or logs.
. "$PSScriptRoot\Common.ps1"
$destination=Join-Path $script:Root ('dist\USBProxy-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $destination) {throw 'Package directory already exists.'}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($directory in @('scripts','src','guest')) {
    Copy-Item -LiteralPath (Join-Path $script:Root $directory) -Destination $destination -Recurse
}
foreach ($directory in @('runtime','logs','backups','downloads')) {
    New-Item -ItemType Directory -Path (Join-Path $destination $directory) -Force | Out-Null
}
foreach ($file in @('USB Proxy.exe','Start-USBProxy.cmd','build.cmd','README.txt','使用說明.txt')) {
    Copy-Item -LiteralPath (Join-Path $script:Root $file) -Destination $destination
}
$files=@('VirtualBox-7.2.22-175245-Win.exe','virtualbox-SHA256SUMS',
    'debian-13-generic-amd64.tar.xz','debian-SHA512SUMS')
foreach ($file in $files) {
    Copy-Item -LiteralPath (Join-Path $script:Root ('downloads\' + $file)) -Destination (Join-Path $destination 'downloads')
}
$verificationFile=Join-Path $script:Root 'logs\verification.json'
if (Test-Path -LiteralPath $verificationFile) {
    $verification=Get-Content -Raw -Encoding UTF8 -LiteralPath $verificationFile | ConvertFrom-Json
    $summary=@{developmentValidation=$true; timestamp=$verification.timestamp;
        virtualbox=$verification.virtualbox; checks=$verification.passed;
        virtualUsbFat32Passed=[bool]$verification.virtualUsbFat32Integration.sha256Verified;
        physicalUsbExfatPassed=[bool]$verification.physicalUsbIntegration.hostReadbackVerified;
        pending=$verification.pending}
    Write-Utf8 (Join-Path $destination 'development-validation.json') ($summary | ConvertTo-Json -Depth 6)
}
$manifest=Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
    @{path=$_.FullName.Substring($destination.Length+1); bytes=$_.Length;
      sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
}
Write-Utf8 (Join-Path $destination 'package-manifest.json') (@{created=(Get-Date).ToString('o'); files=@($manifest)} | ConvertTo-Json -Depth 6)
Write-Output $destination
