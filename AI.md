# AI.md — handoff notes for agents working on USB Proxy

Read this before changing anything. The user writes Traditional Chinese (zh-TW); answer in zh-TW. All user-facing UI text is zh-TW.

## What this is

A Windows app that manages USB flash drives through an isolated Linux VM. Windows never mounts the drive: the VM captures it (VirtualBox USB passthrough), mounts it, and does every read/write, verified with SHA-256. The UI imitates Windows "This PC" + File Explorer.

Target: Windows 10/11 x64 without dev tools. Everything runs on the in-box .NET Framework 4.8 (csc) + OpenSSH client. No Python/Visual Studio on the host (Python is used only by dev scripts).

## Layout (v0.0.6+: pure C#, no PowerShell at runtime)

Symantec SONAR blocked the earlier PowerShell-hosting exe, so the Windows side is C# 5 compiled by the in-box
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (`build.cmd`). No C# 6 syntax (no `$""`, `?.`).
Never reintroduce PowerShell child processes or `-EncodedCommand`.

| Path | Role |
|---|---|
| `USB Proxy.exe` | Built by `build.cmd` from `src/*.cs` (except `Installer.cs`). Flags: `--run req.json` (run one worker request, print JSON), `--smoke-test` (writes `logs/ui-preview.png`), `--update` (headless update), `--cleanup <id>` / `--install-host <log>` (elevated helpers, same exe via UAC). |
| `src/App.cs`, `src/Actions.cs` | WinForms UI (two panes, settings, update prompts). Jobs run on a background thread (`Worker.Run(Map)`), progress via `volatile Worker.Progress`. |
| `src/AppleUi.cs` | Custom controls (ASCII only). |
| `src/Core.cs` | Paths, JSON (`J`), process runner, ssh/scp, `Guest()` call to the agent. |
| `src/Worker.cs` | `status, start, shutdown, attach, eject, list, delete, mkdir, rename, copy, transfer, download, fetch, setup`. |
| `src/Setup.cs` | First install: VirtualBox (elevated), VM creation, cloud-init seed. |
| `src/Update.cs` | Checks GitHub releases of ikenican/usb-proxy, downloads `*-update.zip`, verifies GitHub's sha256 digest, renames the running exe to `.old`, copies files (never `runtime/logs/backups/downloads`). Version: `Updater.Current`. |
| `src/Installer.cs` | `USBProxy-Setup-vX.exe`: stub + full zip appended + 8-byte length. Per-user install, shortcuts. `--target <dir>` extracts silently. |
| `guest/usbproxy-agent.py` | Privileged guest agent (see below). |
| `scripts/publish_release.py` | Dev only: builds package, full zip, update zip, installer and creates the GitHub release. |
| `scripts/test_integration.py` | Integration test of `--run` against an emulated FAT32 USB disk. |
| `runtime/` | **Per machine, never commit**: `config.json`, `id_ed25519`, VM, `ui.json`. |

## Key flows

- **Switch to Linux**: double-click a drive in 本機 → confirm → `start` if needed → `status` → `attach` (VBoxManage `usbattach` of that UUID only; never wildcard filters) → worker waits up to 20 s for partitions → UI opens the single partition or shows 本機.
- **Copy (upload)**: worker builds a plan, streams a tar (`file-NNNNNNNN` blobs + `manifest.json`) over ssh stdin into `/home/usbproxy/incoming/<job>`, hashing on the fly; then agent `copy` writes each file to a `.usbproxy-*` temp, checks the staged hash while writing, fsyncs, reads it back (SHA-256), and `os.replace`s it. Existing names are refused unless `overwrite` (edit write-back, regular files only). Progress units = 3 × bytes (upload 1×, write + read-back 2×); the agent writes `<spool>/.progress`, which the worker polls over ssh once a second.
- **Open / save (download)**: agent `stream` writes raw bytes to stdout and the JSON result (size, sha256) to **stderr**; the worker hashes while receiving into `runtime/open/<guid>/<name>` and verifies. The UI watches opened files and offers write-back when the timestamp changes and is stable.
- **Start**: boot with serial console logged to `logs/vm-serial.log`. If the console stops growing for 90 s (240 s on first boot) the VM is power-cycled once. Boot durations go to `config.bootSeconds` and drive the UI's ETA. After ready, `SyncGuestAgent` compares the agent hash and pushes it with the agent's `self-update`. `status` also self-updates a running guest.
- **Sealed clean image**: the first successful `start` after provisioning shuts down, sets the VDI to VirtualBox `immutable` (`SetDiskType`), and boots again. From then on VirtualBox resets the differencing image at every VM start, so the guest always boots the same clean state (base stays ~1.3 GB; a session's changes live only in the diff until the next start). Consequences: anything written in the guest is temporary, and `SyncGuestAgent` re-pushes the agent each boot when the base copy is older (fast self-update). Anything that must persist in the guest must `SetDiskType normal` first, then let `start` seal again.
- **Shutdown**: auto-eject (unmount + `usbdetach`), agent schedules `shutdown -h +1`, then ACPI power button; worker waits until `poweroff` (≈5 s).

## Hard-won lessons (do not regress)

1. **VM must have 1 vCPU.** With Hyper-V present, VirtualBox runs via NEM; 2 vCPUs made Debian boots stall intermittently (3 of 4 boots, up to 8+ min). 1 vCPU: 25 s every time. `StartGuestVm` enforces `--cpus 1`.
2. **No BOM on ssh stdin.** The agent's `json.load` rejects a BOM; write child-process stdin with `Core.Utf8` (BOM-less UTF-8).
3. Keep `AppleUi.cs` ASCII (use `(char)0x00B7` etc.).
4. WinForms anchoring: size a docked panel **before** adding anchored children, or they overflow. ListView column widths and ImageList sizes are not auto-scaled — multiply by the DPI scale (DeviceDpi/96). ImageList images max 256 px.
5. Large-icon view needs `LVM_SETICONSPACING` (ExplorerListView.SetIconSpacing) or big icons overlap.
6. Jumbo shell icons for types without 256 px art sit in a corner; `ShellIcons.Render` falls back to the 48 px list.
7. Media registry: if a test VDI is deleted while still registered, the next `convertfromraw` to that path fails ("medium lock list"); `closemedium disk <uuid>` the stale entry. Read a running VM's serial log with `FileShare.ReadWrite`.
8. ListView groups stop rendering after `Groups.Clear()` + re-adding (only List view, which ignores groups, still showed items). The UI no longer uses groups.

## Testing

- UI smoke test (renders `logs/ui-preview.png` and `ui-preview-swapped.png`): `"USB Proxy.exe" --smoke-test`; worker requests: `"USB Proxy.exe" --run req.json`.
- Guest unit tests (VM running): scp `guest/test_agent.py` to `/home/usbproxy/` and run `python3 /home/usbproxy/test_agent.py` (tests import the *installed* agent, so start Linux first to sync it). Currently 14 tests, all passing.
- End-to-end without a physical drive: attach `runtime/validation/fat32.vdi` (see the header of `scripts/test_integration.py`) to a `ValidationUSB` storage controller while the VM is off; it appears as a USB FAT32 volume in the guest. Detach it afterwards. Never format or write raw to a real device.
- Physical drive: capturing takes it away from Windows — ask the user first.

## Releasing

1. Bump `Updater.Current` in `src/Update.cs`, run `build.cmd`.
2. `python -I scripts\publish_release.py notes.md` (needs the two installers in `downloads/`).
   Installed copies pick the release up through the in-app updater.

## Status / open items

- Verified: emulated FAT32 USB end-to-end (copy with progress, stream download, write-back, save, rename, delete, duplicate refusal); physical SanDisk exFAT capture/copy/release (earlier version); live UI startup listing the real drive.
- Verified offline install: fresh VM from the official image with the network cable unplugged reached ready in 156 s (dpkg from seed ISO; apt never ran).
- Not yet verified: full new UI flow on a physical drive; NTFS writes; other device models; target machines with USBSTOR disabled; real Explorer drag-and-drop and Ctrl+wheel by hand (simulated only).
- Verified for 0.0.2: two virtual USB drives mounted at once, folder transfer between them, folder download to Windows, no-overwrite on both sides; live UI maps the real SanDisk to its drive letter. Eject-by-serial is untested (virtual disks share a serial).
- Ideas not done: drag Linux files out to Explorer, cancel button for transfers, sort arrows in the column header, rename/delete on the Windows side.
- The repo is public and has no license yet.
