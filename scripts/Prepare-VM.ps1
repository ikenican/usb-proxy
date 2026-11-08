[CmdletBinding()]
param()
. "$PSScriptRoot\Common.ps1"
if (Test-Path $script:ConfigFile) {
    $existing=Read-Config
    $null=Invoke-VBox @('showvminfo',$existing.vmName,'--machinereadable')
    Write-Output ('Using existing VM: ' + $existing.vmName)
    return
}
$null = Get-VBox
$archive = Join-Path $script:Root 'downloads\debian-13-generic-amd64.tar.xz'
$sumfile = Join-Path $script:Root 'downloads\debian-SHA512SUMS'
$lines = @(Get-Content $sumfile | Where-Object { $_ -match '\s\*?debian-13-generic-amd64\.tar\.xz$' })
if ($lines.Count -ne 1) { throw 'Cannot identify Debian archive checksum.' }
$expected = ($lines[0] -split '\s+')[0]
if ((Get-FileHash $archive -Algorithm SHA512).Hash -ne $expected) { throw 'Debian archive SHA512 mismatch.' }
$diskDir = Join-Path $script:Runtime 'disk'
New-Item -ItemType Directory -Path $diskDir -Force | Out-Null
$entries = (Invoke-Native 'tar.exe' @('-tf',$archive)) -split '\r?\n'
if (@($entries | Where-Object { $_ -match '(^/|(^|/)\.\.(/|$))' }).Count) { throw 'Unsafe archive paths.' }
Write-Output 'Extracting official Debian image...'
$null = Invoke-Native 'tar.exe' @('-xf',$archive,'-C',$diskDir) -TimeoutSeconds 600
$raw = @(Get-ChildItem $diskDir -Recurse -File -Filter '*.raw')
if ($raw.Count -ne 1) { throw 'Expected one raw Debian disk.' }
$vdi = Join-Path $script:Runtime 'usbproxy.vdi'
Write-Output 'Converting Linux disk to dynamic VDI...'
$null = Invoke-Native (Get-VBox) @('convertfromraw',$raw[0].FullName,$vdi,'--format','VDI','--variant','Standard') -TimeoutSeconds 600
$null = Invoke-VBox @('modifymedium','disk',$vdi,'--resize','32768')
$key = Join-Path $script:Runtime 'id_ed25519'
# .NET starts ssh-keygen directly, so the empty passphrase argument is preserved.
$null = Invoke-Native 'ssh-keygen.exe' @('-t','ed25519','-f',$key,'-N','','-C','usbproxy-local')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$null = Invoke-Native 'icacls.exe' @($key,'/inheritance:r','/grant:r',($identity + ':(F)'))
$iso = & "$PSScriptRoot\New-GuestSeed.ps1"
$listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$name = 'USBProxy-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$null = Invoke-VBox @('createvm','--name',$name,'--ostype','Debian_64','--basefolder',(Join-Path $script:Runtime 'machines'),'--register')
$config = @{vmName=$name; sshPort=$port; created=(Get-Date).ToString('o'); disk=$vdi; attachedUsb=$null}
# Save immediately: a failed subsequent step leaves an identifiable VM for recovery.
Write-Utf8 $script:ConfigFile ($config | ConvertTo-Json)
$null = Invoke-VBox @('modifyvm',$name,'--memory','1024','--cpus','1','--graphicscontroller','vmsvga',
    '--vram','16','--audio-enabled','off','--usb-xhci','on','--nic1','nat','--nictype1','82540EM',
    '--natpf1',"ssh,tcp,127.0.0.1,$port,,22",'--boot1','disk','--boot2','none','--boot3','none','--boot4','none')
$null = Invoke-VBox @('storagectl',$name,'--name','SATA','--add','sata','--controller','IntelAhci','--portcount','2')
$null = Invoke-VBox @('storageattach',$name,'--storagectl','SATA','--port','0','--device','0','--type','hdd','--medium',$vdi)
$null = Invoke-VBox @('storageattach',$name,'--storagectl','SATA','--port','1','--device','0','--type','dvddrive','--medium',$iso)
Write-Output "VM prepared: $name; SSH loopback port: $port"
Write-Output 'The first boot installs guest filesystem utilities using NAT internet access.'
