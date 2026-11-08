param([string]$IsoPath = '', [string]$SeedDirectory = '')
. "$PSScriptRoot\Common.ps1"
$key = Join-Path $script:Runtime 'id_ed25519'
$public = [IO.File]::ReadAllText($key + '.pub').Trim()
$agent = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:Root 'guest\usbproxy-agent.py')))
$seedDir = if ($SeedDirectory) {$SeedDirectory} else {Join-Path $script:Runtime 'seed'}
New-Item -ItemType Directory -Path $seedDir -Force | Out-Null
# File-system tools are installed from bundled, checksum-verified Debian packages so setup needs no network.
$packages = Join-Path $script:Root 'guest\packages'
$seedPackages = Join-Path $seedDir 'packages'
if (Test-Path -LiteralPath $seedPackages) { Remove-Item -LiteralPath $seedPackages -Recurse -Force }
New-Item -ItemType Directory -Path $seedPackages -Force | Out-Null
$sums = @(Get-Content -LiteralPath (Join-Path $packages 'SHA256SUMS') | Where-Object { $_ -match '^[0-9a-f]{64}\s+\S+\.deb$' })
if ($sums.Count -eq 0) { throw 'guest\packages\SHA256SUMS lists no packages.' }
foreach ($line in $sums) {
    $hash, $name = $line -split '\s+', 2
    $file = Join-Path $packages $name
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "Package checksum mismatch: $name" }
    Copy-Item -LiteralPath $file -Destination $seedPackages
}
$instance = 'usbproxy-' + [guid]::NewGuid().ToString('N')
Write-Utf8 (Join-Path $seedDir 'meta-data') "instance-id: $instance`nlocal-hostname: usbproxy`n"
$userdata = @"
#cloud-config
users:
  - name: usbproxy
    shell: /bin/bash
    lock_passwd: true
    ssh_authorized_keys:
      - $public
    sudo: ['ALL=(root) NOPASSWD: /usr/local/sbin/usbproxy-agent']
ssh_pwauth: false
ssh_deletekeys: false
disable_root: true
package_update: false
package_upgrade: false
write_files:
  - path: /usr/local/sbin/usbproxy-agent
    owner: root:root
    permissions: '0755'
    encoding: b64
    content: $agent
runcmd:
  - [mkdir, -p, /home/usbproxy/incoming, /var/lib/usbproxy]
  - [chown, 'usbproxy:usbproxy', /home/usbproxy/incoming]
  # openssh-server, python3 and dosfstools ship in the Debian cloud image; the rest come from this seed ISO.
  - [sh, -c, 'set -e; m=`$(mktemp -d); mount -o ro LABEL=cidata "`$m"; dpkg -i "`$m"/packages/*.deb; umount "`$m"; rmdir "`$m"']
  - [sh, -c, 'command -v ntfs-3g && command -v mkfs.exfat && command -v python3 && touch /var/lib/usbproxy/ready && echo "usbproxy: offline setup complete"']
"@
Write-Utf8 (Join-Path $seedDir 'user-data') ($userdata.Replace("`r`n","`n") + "`n")
$iso = if ($IsoPath) {$IsoPath} else {Join-Path $script:Runtime 'seed.iso'}
$image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
$image.FileSystemsToCreate = 3
$image.VolumeName = 'cidata'
$image.Root.AddTree($seedDir, $false)
$result = $image.CreateResultImage()
# IMAPI exposes an IStream COM object. Copy directly through its vtable wrapper.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class IsoWriter {
    public static void Save(object value, string path) {
        IStream stream = (IStream)value;
        IntPtr count = Marshal.AllocHGlobal(4);
        try {
            byte[] buffer = new byte[1048576];
            using (FileStream output = File.Create(path)) {
                for (;;) {
                    stream.Read(buffer, buffer.Length, count);
                    int n = Marshal.ReadInt32(count);
                    if (n == 0) break;
                    output.Write(buffer, 0, n);
                }
            }
        } finally { Marshal.FreeHGlobal(count); }
    }
}
'@
[IsoWriter]::Save($result.ImageStream, $iso)
Write-Output $iso
