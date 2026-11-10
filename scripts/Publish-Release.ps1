# Development only: publishes the version in src\Update.cs as a GitHub release with
#   USBProxy-Setup-vX.Y.Z.exe   installer (src\Installer.cs + the full package appended)
#   USBProxy-vX.Y.Z-full.zip    the same package for manual install
#   USBProxy-vX.Y.Z-update.zip  program files only, what the in-app updater downloads
# Bump Updater.Current and run build.cmd first.   powershell -File scripts\Publish-Release.ps1 -NotesFile notes.md
param([Parameter(Mandatory)][string]$NotesFile)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot
$version = [regex]::Match((Get-Content -Raw "$root\src\Update.cs"), 'Current = "([^"]+)"').Groups[1].Value
$dist = "$root\dist"
# Full package (keep only the newest package folder in dist).
Get-ChildItem $dist -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
$package = (& "$PSScriptRoot\Build-Package.ps1" | Select-Object -Last 1)
$full = "$dist\USBProxy-v$version-full.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($package, $full, 'Optimal', $false)
# Update zip: the same package without the bundled installers and the per-machine folders.
$stage = "$env:TEMP\usbproxy-update-$version"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
foreach ($item in 'USB Proxy.exe','Start-USBProxy.cmd','build.cmd','README.txt','使用說明.txt','src','scripts','guest') {
    Copy-Item -Recurse -LiteralPath "$package\$item" -Destination $stage
}
$update = "$dist\USBProxy-v$version-update.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $update, 'Optimal', $false)
Remove-Item -Recurse -Force $stage
# Installer: stub exe + full zip + zip length (8 bytes).
$stub = "$env:TEMP\usbproxy-setup-stub.exe"
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /platform:x64 "/win32icon:$root\src\app.ico" "/out:$stub" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll /reference:Microsoft.CSharp.dll "$root\src\Installer.cs"
if ($LASTEXITCODE) { throw 'Installer build failed.' }
$setup = "$dist\USBProxy-Setup-v$version.exe"
$out = [IO.File]::Create($setup)
foreach ($part in $stub, $full) { $in = [IO.File]::OpenRead($part); $in.CopyTo($out); $in.Dispose() }
$length = [BitConverter]::GetBytes([long](Get-Item $full).Length); $out.Write($length, 0, 8); $out.Dispose()
Remove-Item $stub
gh release create "v$version" $setup $full $update -R ikenican/usb-proxy --title "USB Proxy v$version" --notes-file $NotesFile
