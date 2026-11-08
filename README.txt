USB Proxy prototype
===================

Setup: run Setup-USBProxy.cmd once. Approve the Windows UAC prompt for
Oracle VirtualBox. Only VBoxApplication and VBoxUSB are installed.
For another PC, use the clean folder under dist/, not a copy of runtime/.
Start: run Start-USBProxy.cmd. The window opens on "This PC": double-click a USB drive and confirm to switch it to Linux (Linux starts automatically). The drive then opens in an Explorer-like browser (icons/list/details views): drag files in to copy with SHA-256 verification and progress/ETA, open and edit files (offered back to the drive on save), rename, delete, new folder. Eject returns the drive to Windows; shutting down Linux ejects first.

The supplied VirtualBoxPortable64 folder is preserved. Its wrapper is not
executed: its bundled source contains legacy network-driver paths and
temporarily changes machine-wide services and COM registration.
The prototype uses an official, hash-checked, signed VirtualBox installer.
No Extension Pack, host-only networking, bridged networking or developer
toolchain is installed. The guest has NAT with SSH bound to host loopback.
First boot needs internet to install Linux filesystem utilities.

Files are staged locally and in the VM, then checked with SHA-256 before
and after writing. Existing destination files are never deliberately
overwritten. A failed batch may leave earlier completed files on USB;
the error is reported, and retrying that batch can encounter those files.
Empty folders, symbolic links and Windows junctions are not copied.
FAT32 files >=4 GiB are rejected. Never unplug during transfer.
No formatting, partitioning or raw-disk writes are implemented.
The UI filters host USB devices using Windows USB mass-storage compatible
IDs; host drive letters and USBSTOR disk mounting are not required for this
filter. Target-machine enumeration still requires validation. Select only
the intended storage device, especially if it is a composite USB device.

Configuration, SSH keys and VM disks live in runtime/. Keep the private
key private. Do not share runtime/ as a distributable package.
Logs are in logs/. Installation registry snapshots and driver inventory
are in backups/. These snapshots are NOT a full Windows image.
Do not blindly import class-wide registry exports.
To remove host changes, safely eject USB, shut down this VM, then uninstall
Oracle VirtualBox through Windows Installed Apps. Preserve any preexisting
VirtualBox installation; the setup script skips an existing installation.
Project files/VM images on D: must be removed separately if desired.

USBSTOR, UASPStor, Secure Boot, Memory Integrity, BIOS settings, permanent
PowerShell execution policy and Windows optional features are not changed.
The recorded physical SanDisk exFAT capture/copy/release and host read-back
checks passed. Emulated USB FAT32 copying/verification/ejection also passed.
NTFS, other device models and the disabled-USB-storage target machine still
require separate validation. See logs/verification.json and the Chinese guide.

Sources:
https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/installation.html
https://docs.oracle.com/en/virtualization/virtualbox/7.2/user/vboxmanage.html
https://cloud.debian.org/images/cloud/trixie/latest/
