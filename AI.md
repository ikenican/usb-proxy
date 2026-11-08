# AI.md — handoff notes for agents working on USB Proxy

Read this before changing anything. The user writes Traditional Chinese (zh-TW); answer in zh-TW. All user-facing UI text is zh-TW.

## What this is

A Windows app that manages USB flash drives through an isolated Linux VM. Windows never mounts the drive: the VM captures it (VirtualBox USB passthrough), mounts it, and does every read/write, verified with SHA-256. The UI imitates Windows "This PC" + File Explorer.

Target: Windows 10/11 x64 without dev tools. Everything runs on built-in Windows PowerShell 5.1 + .NET Framework 4.8 + OpenSSH client. No Python/Visual Studio on the host.

## Layout

| Path | Role |
|---|---|
| `Start-USBProxy.cmd` | Launches `src/UsbProxy.ps1` (hidden console, STA). |
| `Setup-USBProxy.cmd` | `scripts/Install-Host.ps1` (elevated; installs signed VirtualBox 7.2.22: app + USB only) then `scripts/Prepare-VM.ps1`. |
| `src/UsbProxy.ps1` | WinForms UI: two panes in a SplitContainer — left `$win` browses Windows (special folders, drives, folders; synchronous .NET IO), right `$lin` browses volumes in Linux (async via worker `list`). Pane = hashtable (List, Empty, Large image list, Location, History, Back/Up/View). Event handlers find their pane with `Get-PaneOf $sender`. Runs each operation via `Begin-Operation` → spawns `scripts/Worker.ps1`; a 250 ms timer polls for the response and runs `$script:followUp`. Copies: Windows→Linux `copy`, Linux→Linux `transfer`, Linux→Windows `download` (drag-drop with custom format `UsbProxy.LinuxItems`, Ctrl+C/V, or "send to other pane"). |
| `src/AppleUi.cs` | Custom controls compiled at startup with `Add-Type` (**C# 5 only, ASCII only** — csc reads the file in the system code page). Theme, Card, PillButton, PopupSelect, ThinProgress, PenguinBadge, MenuRenderer, FileEntry, ShellIcons (Windows shell icons/type names), ExplorerListView (Explorer theme, natural sort, Ctrl+wheel event, icon spacing), DeviceChangeWatcher (WM_DEVICECHANGE). |
| `src/app.ico`, `src/penguin.png` | Window icon; Linux badge drawn on drives. Generated from `linxupenguin.png`. Use the PNG for drawing: .NET cannot decode PNG-compressed ICO frames. |
| `scripts/Worker.ps1` | One request per process: `status, start, shutdown, attach, eject, list, delete, mkdir, rename, copy, transfer, download, fetch`. Several drives can be attached: `config.attachedUsbs = [{uuid,name,serial}]` (serial as Linux reports it, used to unmount one drive on eject); `status` maps VirtualBox USB devices to Windows drive letters by serial (`Get-Disk`). Reads `-RequestFile`, writes `-ResponseFile` `{ok,data|error}`. Holds `runtime/operation.lock`. Writes progress to `<ResponseFile>.progress`. |
| `scripts/Common.ps1` | Paths, `Invoke-Native` (with `-Poll` callback), `Invoke-Guest` (ssh → `sudo -n /usr/local/sbin/usbproxy-agent`, JSON on stdin), config helpers. |
| `guest/usbproxy-agent.py` | Privileged guest agent (runs as root via a single sudoers entry). Only mounts partitions whose block-device ancestry has `TRAN=usb`, each at `/mnt/usbproxy/<device name>` (several at once). Actions include `walk` (files under a selection) and `transfer` (volume→volume, verified). Paths are volume-relative and never follow symlinks (`resolve()`). |
| `guest/test_agent.py` | Unit tests, run inside the VM (see Testing). |
| `scripts/New-GuestSeed.ps1` | cloud-init NoCloud seed ISO (user, key, agent via `write_files`, and `guest/packages/*.deb` installed with `dpkg -i` from the ISO — no apt, no network). `-IsoPath/-SeedDirectory` build a seed elsewhere (tests). |
| `scripts/Build-Package.ps1` | Builds `dist/USBProxy-<timestamp>` (scripts, src, guest, docs, the two installers + checksums, manifest). |
| `scripts/Test-Integration.ps1`, `New-ValidationDisk.ps1`, `Test-PhysicalUsb.ps1`, `Verify-Project.ps1` | Older validation scripts (emulated FAT32 USB disk; physical drive). Still compatible with the worker. |
| `runtime/` | **Per machine, never commit**: `config.json`, `id_ed25519` (private key), VM, `jobs/`, `open/` (local copies of opened files), `ui.json` (view/zoom/window size). |
| `guest/packages/` | The 7 Debian 13 .deb files the base cloud image lacks (exfatprogs, ntfs-3g, libntfs-3g89t64, fuse3, libgcrypt20, libgpg-error0, libgpg-error-l10n) + `SHA256SUMS` (matches Debian's signed index). Setup is fully offline. If the Debian base image is ever updated, regenerate these against it (install online once, copy from `/var/cache/apt/archives`, verify with `apt-cache show`). |
| `downloads/` | `VirtualBox-7.2.22-175245-Win.exe`, `debian-13-generic-amd64.tar.xz` (not in git; in the release zip) + tracked checksum files. |

## Key flows

- **Switch to Linux**: double-click a drive in 本機 → confirm → `start` if needed → `status` → `attach` (VBoxManage `usbattach` of that UUID only; never wildcard filters) → worker waits up to 20 s for partitions → UI opens the single partition or shows 本機.
- **Copy (upload)**: worker builds a plan, streams a tar (`file-NNNNNNNN` blobs + `manifest.json`) over ssh stdin into `/home/usbproxy/incoming/<job>`, hashing on the fly; then agent `copy` writes each file to a `.usbproxy-*` temp, checks the staged hash while writing, fsyncs, reads it back (SHA-256), and `os.replace`s it. Existing names are refused unless `overwrite` (edit write-back, regular files only). Progress units = 3 × bytes (upload 1×, write + read-back 2×); the agent writes `<spool>/.progress`, which the worker polls over ssh once a second.
- **Open / save (download)**: agent `stream` writes raw bytes to stdout and the JSON result (size, sha256) to **stderr**; the worker hashes while receiving into `runtime/open/<guid>/<name>` and verifies. The UI watches opened files and offers write-back when the timestamp changes and is stable.
- **Start**: boot with serial console logged to `logs/vm-serial.log`. If the console stops growing for 90 s (240 s on first boot) the VM is power-cycled once. Boot durations go to `config.bootSeconds` and drive the UI's ETA. After ready, `Sync-GuestAgent` compares the agent hash; it uses `self-update` (new agents) or re-seeds cloud-init (agents older than self-update). `status` also self-updates a running guest (no reseed).
- **Sealed clean image**: the first successful `start` after provisioning shuts down, sets the VDI to VirtualBox `immutable` (`Set-DiskType`), and boots again. From then on VirtualBox resets the differencing image at every VM start, so the guest always boots the same clean state (base stays ~1.3 GB; a session's changes live only in the diff until the next start). Consequences: anything written in the guest is temporary, and `Sync-GuestAgent` re-pushes the agent each boot when the base copy is older (fast self-update). Anything that must persist in the guest (re-seeding) must `Set-DiskType normal` first, then let `start` seal again — `Sync-GuestAgent`'s reseed path does this; the legacy `scripts/Update-Guest.ps1` does not.
- **Shutdown**: auto-eject (unmount + `usbdetach`), agent schedules `shutdown -h +1`, then ACPI power button; worker waits until `poweroff` (≈5 s).

## Hard-won lessons (do not regress)

1. **VM must have 1 vCPU.** With Hyper-V present, VirtualBox runs via NEM; 2 vCPUs made Debian boots stall intermittently (3 of 4 boots, up to 8+ min). 1 vCPU: 25 s every time. `Start-GuestVm` enforces `--cpus 1`.
2. **No BOM on ssh stdin.** Child-process stdin uses `Console.InputEncoding`; a UTF-8 console prefixes a BOM and the agent's `json.load` rejects it. `Common.ps1` sets `[Console]::InputEncoding` to BOM-less UTF-8.
3. **PowerShell 5.1 needs UTF-8 *with BOM*** for any `.ps1` containing Chinese, including your own test scripts (otherwise strings are garbled and the agent rightly rejects names). Keep `AppleUi.cs` ASCII (use `(char)0x00B7` etc.).
4. PS 5.1 has no `try` as an expression, no `??`, no `?.`; `@($null).Count` is 1 — filter with `Where-Object {$_}`.
5. WinForms anchoring: size a docked panel **before** adding anchored children, or they overflow. ListView column widths and ImageList sizes are not auto-scaled — multiply by `$scale` (DeviceDpi/96). ImageList images max 256 px.
6. Large-icon view needs `LVM_SETICONSPACING` (ExplorerListView.SetIconSpacing) or big icons overlap.
7. Jumbo shell icons for types without 256 px art sit in a corner; `ShellIcons.Render` falls back to the 48 px list.
8. Never use `.GetNewClosure()` (worker poll callbacks or UI event handlers): the closure runs in a dynamic module that cannot see the script's own functions. Plain scriptblocks see the caller's variables via dynamic scope; UI handlers use `param($sender,$e)` + `Get-PaneOf $sender`.
9. When testing worker flows, run each request in a **separate `powershell.exe`** like the UI does; dot-sourcing/`&`-calling Worker.ps1 in one session pollutes `$script:` state.
10. Media registry: if a test VDI is deleted while still registered, the next `convertfromraw` to that path fails ("medium lock list"); `closemedium disk <uuid>` the stale entry. Read a running VM's serial log with `FileShare.ReadWrite`.
11. ListView groups stop rendering after `Groups.Clear()` + re-adding (only List view, which ignores groups, still showed items). The UI no longer uses groups.

## Testing

- UI smoke test (renders `logs/ui-preview.png`): `powershell -STA -File src\UsbProxy.ps1 -SmokeTest`
- Parse check: `[Management.Automation.Language.Parser]::ParseFile(...)` on every `.ps1`.
- Guest unit tests (VM running): scp `guest/test_agent.py` to `/home/usbproxy/` and run `python3 /home/usbproxy/test_agent.py` (tests import the *installed* agent, so start Linux first to sync it). Currently 14 tests, all passing.
- End-to-end without a physical drive: attach `runtime/validation/fat32.vdi` (created by `New-ValidationDisk.ps1`) to a `ValidationUSB` storage controller while the VM is off; it appears as a USB FAT32 volume in the guest. Detach it afterwards. Never format or write raw to a real device.
- Physical drive: capturing takes it away from Windows — ask the user first.

## Releasing

Versions start at `v0.0.1` (pre-release). Bump the patch/minor for each published package.

1. `scripts/Build-Package.ps1` → `dist/USBProxy-<timestamp>` (requires the two installers in `downloads/`; get them from the latest release zip).
2. Keep only the newest folder in `dist/` (user preference).
3. Zip it as `USBProxy-<version>-full.zip` and attach it to a GitHub release.

## Status / open items

- Verified: emulated FAT32 USB end-to-end (copy with progress, stream download, write-back, save, rename, delete, duplicate refusal); physical SanDisk exFAT capture/copy/release (earlier version); live UI startup listing the real drive.
- Verified offline install: fresh VM from the official image with the network cable unplugged reached ready in 156 s (dpkg from seed ISO; apt never ran).
- Not yet verified: full new UI flow on a physical drive; NTFS writes; other device models; target machines with USBSTOR disabled; real Explorer drag-and-drop and Ctrl+wheel by hand (simulated only).
- Verified for 0.0.2: two virtual USB drives mounted at once, folder transfer between them, folder download to Windows, no-overwrite on both sides; live UI maps the real SanDisk to its drive letter. Eject-by-serial is untested (virtual disks share a serial).
- Ideas not done: drag Linux files out to Explorer, cancel button for transfers, sort arrows in the column header, rename/delete on the Windows side.
- Legacy scripts (`Test-Integration.ps1`, `Test-PhysicalUsb.ps1`, `Update-Guest.ps1`) predate multi-drive and sealed images.
- The repo is private and has no license yet.
