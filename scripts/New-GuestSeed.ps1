. "$PSScriptRoot\Common.ps1"
$key = Join-Path $script:Runtime 'id_ed25519'
$public = [IO.File]::ReadAllText($key + '.pub').Trim()
$agent = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:Root 'guest\usbproxy-agent.py')))
$seedDir = Join-Path $script:Runtime 'seed'
New-Item -ItemType Directory -Path $seedDir -Force | Out-Null
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
package_update: true
packages:
  - openssh-server
  - python3
  - exfatprogs
  - ntfs-3g
  - dosfstools
write_files:
  - path: /usr/local/sbin/usbproxy-agent
    owner: root:root
    permissions: '0755'
    encoding: b64
    content: $agent
runcmd:
  - [mkdir, -p, /home/usbproxy/incoming, /var/lib/usbproxy]
  - [chown, 'usbproxy:usbproxy', /home/usbproxy/incoming]
  - [sh, -c, 'command -v ntfs-3g && command -v python3 && touch /var/lib/usbproxy/ready']
"@
Write-Utf8 (Join-Path $seedDir 'user-data') ($userdata.Replace("`r`n","`n") + "`n")
$iso = Join-Path $script:Runtime 'seed.iso'
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
