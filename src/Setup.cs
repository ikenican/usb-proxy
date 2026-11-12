// First run: install VirtualBox (one UAC prompt) and build the Linux VM. Formerly Install-Host.ps1,
// Prepare-VM.ps1 and New-GuestSeed.ps1.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace UsbProxy
{
    public static class Setup
    {
        const string Installer = "VirtualBox-7.2.22-175245-Win.exe";
        const string DebianArchive = "debian-13-generic-amd64.tar.xz";
        static string Downloads(string name) { return Path.Combine(Core.Root, "downloads", name); }

        public static Map Run()
        {
            foreach (string name in new[] { Installer, DebianArchive })
                if (!File.Exists(Downloads(name))) throw new ToolException(string.Format(Core.T("找不到安裝檔 downloads\\{0}。請使用完整的部署包。"), name));
            if (!File.Exists(Core.VBoxManage))
            {
                Worker.Progress = J.Obj("phase", "virtualbox");
                InstallHost();
            }
            if (!File.Exists(Core.ConfigFile))
            {
                Worker.Progress = J.Obj("phase", "prepare");
                PrepareVm();
            }
            return J.Obj("installed", true);
        }

        // ---------------- VirtualBox install (elevated) ----------------
        static void InstallHost()
        {
            string log = Path.Combine(Core.Root, @"logs\host-setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            Process p;
            try
            {
                p = Process.Start(new ProcessStartInfo(Core.ExePath, "--install-host " + Core.Quote(log)) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Win32Exception) { throw new ToolException(Core.T("需要系統管理員權限才能安裝 VirtualBox。請再按一次「安裝」，並在提示中按「是」。")); }
            p.WaitForExit();
            if (p.ExitCode != 0) throw new ToolException("Elevated setup failed. See " + log);
        }

        // Runs in the elevated copy of this exe. Returns the process exit code.
        public static int InstallHostElevated(string log)
        {
            var lines = new List<string>();
            Action<string> say = s => { lines.Add(s); try { File.WriteAllLines(log, lines, Encoding.UTF8); } catch { } };
            try { InstallHostSteps(say); return 0; }
            catch (Exception e) { say(e.ToString()); return 1; }
        }

        static void InstallHostSteps(Action<string> say)
        {
            string installer = Downloads(Installer);
            string expected = File.ReadAllLines(Downloads("virtualbox-SHA256SUMS"))
                .Where(l => Regex.IsMatch(l, "\\s\\*?VirtualBox-7\\.2\\.22-175245-Win\\.exe$")).Select(l => Regex.Split(l, "\\s+")[0]).FirstOrDefault();
            if (expected == null || !string.Equals(Core.Sha256File(installer), expected, StringComparison.OrdinalIgnoreCase)) throw new ToolException("Installer SHA256 mismatch.");
            if (!SignedByOracle(installer)) throw new ToolException("Invalid Oracle installer signature.");
            if (File.Exists(Core.VBoxManage)) { say("VirtualBox already installed; leaving it unchanged."); return; }

            string backup = Path.Combine(Core.Root, @"backups\preinstall-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);
            var keys = new List<string> { @"HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR", @"HKLM\SYSTEM\CurrentControlSet\Services\UASPStor",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Class\{36FC9E60-C465-11CF-8056-444553540000}",
                @"HKLM\SOFTWARE\Oracle\VirtualBox", @"HKCU\Software\Oracle\VirtualBox" };
            foreach (string service in new[] { "VBoxSup", "VBoxUSB", "VBoxUSBMon", "VBoxSDS", "VBoxNetLwf", "VBoxNetAdp6" })
                keys.Add(@"HKLM\SYSTEM\CurrentControlSet\Services\" + service);
            var manifest = new List<object>();
            foreach (string key in keys)
            {
                string file = Regex.Replace(key, "[^A-Za-z0-9{}-]", "_") + ".reg";
                bool exists = true;
                try { Core.Run("reg.exe", new[] { "export", key, Path.Combine(backup, file), "/y" }); }
                catch { exists = false; }   // reg export fails only for a missing key here
                manifest.Add(J.Obj("key", key, "existed", exists, "file", exists ? file : null));
            }
            Core.WriteUtf8(Path.Combine(backup, "registry-manifest.json"), J.Write(manifest.ToArray()));
            var drivers = new List<object>();
            using (var s = new ManagementObjectSearcher("SELECT Name,State,StartMode,PathName FROM Win32_SystemDriver"))
                foreach (ManagementObject o in s.Get())
                    drivers.Add(J.Obj("Name", o["Name"], "State", o["State"], "StartMode", o["StartMode"], "PathName", o["PathName"]));
            Core.WriteUtf8(Path.Combine(backup, "system-drivers.json"), J.Write(drivers.ToArray()));
            foreach (var dump in new[] { new[] { "bcdedit.exe", "/enum", "boot-config.txt" }, new[] { "pnputil.exe", "/enum-drivers", "driver-store.txt" } })
            {
                try { Core.WriteUtf8(Path.Combine(backup, dump[2]), Core.Run(dump[0], new[] { dump[1] })); } catch (Exception e) { Core.WriteUtf8(Path.Combine(backup, dump[2]), e.Message); }
            }
            Core.WriteUtf8(Path.Combine(backup, "changes.json"), J.Write(J.Obj(
                "installer", installer, "sha256", Core.Sha256File(installer).ToUpperInvariant(),
                "features", new[] { "VBoxApplication", "VBoxUSB" },
                "unchanged", new[] { "USBSTOR", "UASPStor", "SecureBoot", "MemoryIntegrity", "ExecutionPolicy", "WindowsFeatures" },
                "note", "Registry snapshots are not a full Windows image. Uninstall VirtualBox using Windows Installed Apps; do not blindly import class-wide registry snapshots.")));

            string extract = Downloads("virtualbox-msi");
            Directory.CreateDirectory(extract);
            if (Directory.GetFiles(extract, "*amd64.msi").Length == 0) say(Core.Run(installer, new[] { "-extract", "-path", extract }, "", 600));
            var msi = Directory.GetFiles(extract, "*.msi").Where(f => !Regex.IsMatch(Path.GetFileName(f), "arm64|x86", RegexOptions.IgnoreCase)).ToList();
            if (msi.Count != 1) throw new ToolException("Expected exactly one x64 MSI.");
            if (!SignedByOracle(msi[0])) throw new ToolException("Invalid Oracle MSI signature.");
            string installLog = Path.Combine(backup, "installation.log");
            say("Backup complete: " + backup);
            say("Installing official VirtualBox application + USB support.");
            var args = new[] { "/i", msi[0], "/qn", "/norestart", "ADDLOCAL=VBoxApplication,VBoxUSB",
                "VBOX_INSTALLDESKTOPSHORTCUT=0", "VBOX_INSTALLQUICKLAUNCHSHORTCUT=0", "VBOX_START=0", "/L*v", installLog };
            int code;
            using (var p = Process.Start(new ProcessStartInfo("msiexec.exe", Core.JoinArgs(args)) { UseShellExecute = false, CreateNoWindow = true }))
            {
                p.WaitForExit();
                code = p.ExitCode;
            }
            Core.WriteUtf8(Path.Combine(backup, "installation-result.json"), J.Write(J.Obj("exitCode", code, "timestamp", DateTime.Now.ToString("o"))));
            if (code != 0 && code != 3010) throw new ToolException("Installation failed: " + code + ". See " + installLog);
            if (code == 3010) throw new ToolException("Installation succeeded but requires a Windows restart. Restart before preparing VM.");
            say("Installed: " + Core.VBox("--version"));
        }

        // Authenticode: the file's signature must verify and the signer must be Oracle.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class WinTrustFileInfo { public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)); public string pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        class WinTrustData
        {
            public uint cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData));
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public uint dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1;   // no UI, no revocation (offline), file
            public IntPtr pFile;
            public uint dwStateAction = 0;
            public IntPtr hWVTStateData, pwszURLReference;
            public uint dwProvFlags = 0, dwUIContext = 0;
            public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, WinTrustData data);

        static bool SignedByOracle(string path)
        {
            var file = new WinTrustFileInfo { pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(file));
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                var data = new WinTrustData { pFile = pFile };
                if (WinVerifyTrust(IntPtr.Zero, new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), data) != 0) return false;
                return X509Certificate.CreateFromSignedFile(path).Subject.IndexOf("Oracle", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(pFile); }
        }

        // ---------------- Linux VM ----------------
        static void PrepareVm()
        {
            if (File.Exists(Core.ConfigFile)) { Core.VBox("showvminfo", J.S(Core.ReadConfig(), "vmName"), "--machinereadable"); return; }
            string archive = Downloads(DebianArchive);
            var lines = File.ReadAllLines(Downloads("debian-SHA512SUMS")).Where(l => Regex.IsMatch(l, "\\s\\*?debian-13-generic-amd64\\.tar\\.xz$")).ToList();
            if (lines.Count != 1) throw new ToolException("Cannot identify Debian archive checksum.");
            string expected = Regex.Split(lines[0], "\\s+")[0];
            string actual;
            using (var sha = SHA512.Create()) using (var s = File.OpenRead(archive)) actual = Core.Hex(sha.ComputeHash(s));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new ToolException("Debian archive SHA512 mismatch.");
            string diskDir = Path.Combine(Core.Runtime, "disk");
            Directory.CreateDirectory(diskDir);
            var entries = Regex.Split(Core.Run("tar.exe", new[] { "-tf", archive }, "", 600), "\\r?\\n");
            if (entries.Any(e => Regex.IsMatch(e, "(^/|(^|/)\\.\\.(/|$))"))) throw new ToolException("Unsafe archive paths.");
            Core.Run("tar.exe", new[] { "-xf", archive, "-C", diskDir }, "", 600);
            var raw = Directory.GetFiles(diskDir, "*.raw", SearchOption.AllDirectories);
            if (raw.Length != 1) throw new ToolException("Expected one raw Debian disk.");
            string vdi = Path.Combine(Core.Runtime, "usbproxy.vdi");
            Core.Run(Core.VBoxManage, new[] { "convertfromraw", raw[0], vdi, "--format", "VDI", "--variant", "Standard" }, "", 600);
            Core.VBox("modifymedium", "disk", vdi, "--resize", "32768");
            // The extracted raw image (about 3 GB) is only an intermediate for the VDI.
            Directory.Delete(diskDir, true);
            string key = Path.Combine(Core.Runtime, "id_ed25519");
            // A key left by an interrupted earlier run is reused (ssh-keygen would stop at its overwrite prompt).
            if (!File.Exists(key)) Core.Run("ssh-keygen.exe", new[] { "-t", "ed25519", "-f", key, "-N", "", "-C", "usbproxy-local" });
            Core.Run("icacls.exe", new[] { key, "/inheritance:r", "/grant:r", WindowsIdentity.GetCurrent().Name + ":(F)" });
            string iso = NewGuestSeed();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            string name = "USBProxy-" + Core.NewId().Substring(0, 8);
            Core.VBox("createvm", "--name", name, "--ostype", "Debian_64", "--basefolder", Path.Combine(Core.Runtime, "machines"), "--register");
            // Save immediately: a failed subsequent step leaves an identifiable VM for recovery.
            Core.SaveConfig(J.Obj("vmName", name, "sshPort", port, "created", DateTime.Now.ToString("o"), "disk", vdi));
            Core.VBox("modifyvm", name, "--memory", "1024", "--cpus", "1", "--graphicscontroller", "vmsvga",
                "--vram", "16", "--audio-enabled", "off", "--usb-xhci", "on", "--nic1", "nat", "--nictype1", "82540EM",
                "--natpf1", "ssh,tcp,127.0.0.1," + port + ",,22", "--boot1", "disk", "--boot2", "none", "--boot3", "none", "--boot4", "none");
            Core.VBox("storagectl", name, "--name", "SATA", "--add", "sata", "--controller", "IntelAhci", "--portcount", "2");
            Core.VBox("storageattach", name, "--storagectl", "SATA", "--port", "0", "--device", "0", "--type", "hdd", "--medium", vdi);
            Core.VBox("storageattach", name, "--storagectl", "SATA", "--port", "1", "--device", "0", "--type", "dvddrive", "--medium", iso);
        }

        // cloud-init seed ISO: the SSH key, the guest agent and the bundled offline packages.
        public static string NewGuestSeed()
        {
            string publicKey = File.ReadAllText(Path.Combine(Core.Runtime, "id_ed25519.pub")).Trim();
            string agent = Convert.ToBase64String(File.ReadAllBytes(Path.Combine(Core.Root, @"guest\usbproxy-agent.py")));
            string seedDir = Path.Combine(Core.Runtime, "seed");
            Directory.CreateDirectory(seedDir);
            // File-system tools are installed from bundled, checksum-verified Debian packages so setup needs no network.
            string packages = Path.Combine(Core.Root, @"guest\packages");
            string seedPackages = Path.Combine(seedDir, "packages");
            if (Directory.Exists(seedPackages)) Directory.Delete(seedPackages, true);
            Directory.CreateDirectory(seedPackages);
            var sums = File.ReadAllLines(Path.Combine(packages, "SHA256SUMS")).Where(l => Regex.IsMatch(l, "^[0-9a-f]{64}\\s+\\S+\\.deb$")).ToList();
            if (sums.Count == 0) throw new ToolException("guest\\packages\\SHA256SUMS lists no packages.");
            foreach (string line in sums)
            {
                var parts = Regex.Split(line, "\\s+");
                string file = Path.Combine(packages, parts[1]);
                if (Core.Sha256File(file) != parts[0]) throw new ToolException("Package checksum mismatch: " + parts[1]);
                File.Copy(file, Path.Combine(seedPackages, parts[1]), true);
            }
            Core.WriteUtf8(Path.Combine(seedDir, "meta-data"), "instance-id: usbproxy-" + Core.NewId() + "\nlocal-hostname: usbproxy\n");
            string userData = string.Join("\n", new[] {
                "#cloud-config",
                "users:",
                "  - name: usbproxy",
                "    shell: /bin/bash",
                "    lock_passwd: true",
                "    ssh_authorized_keys:",
                "      - " + publicKey,
                "    sudo: ['ALL=(root) NOPASSWD: /usr/local/sbin/usbproxy-agent']",
                "ssh_pwauth: false",
                "ssh_deletekeys: false",
                "disable_root: true",
                "package_update: false",
                "package_upgrade: false",
                "write_files:",
                "  - path: /usr/local/sbin/usbproxy-agent",
                "    owner: root:root",
                "    permissions: '0755'",
                "    encoding: b64",
                "    content: " + agent,
                "runcmd:",
                "  - [mkdir, -p, /home/usbproxy/incoming, /var/lib/usbproxy]",
                "  - [chown, 'usbproxy:usbproxy', /home/usbproxy/incoming]",
                "  # openssh-server, python3 and dosfstools ship in the Debian cloud image; the rest come from this seed ISO.",
                "  - [sh, -c, 'set -e; m=$(mktemp -d); mount -o ro LABEL=cidata \"$m\"; dpkg -i \"$m\"/packages/*.deb; umount \"$m\"; rmdir \"$m\"']",
                "  - [sh, -c, 'command -v ntfs-3g && command -v mkfs.exfat && command -v python3 && touch /var/lib/usbproxy/ready && echo \"usbproxy: offline setup complete\"']" }) + "\n";
            Core.WriteUtf8(Path.Combine(seedDir, "user-data"), userData);
            string iso = Path.Combine(Core.Runtime, "seed.iso");
            dynamic image = Activator.CreateInstance(Type.GetTypeFromProgID("IMAPI2FS.MsftFileSystemImage"));
            image.FileSystemsToCreate = 3;   // ISO 9660 + Joliet
            image.VolumeName = "cidata";
            image.Root.AddTree(seedDir, false);
            dynamic result = image.CreateResultImage();
            var stream = (IStream)result.ImageStream;
            IntPtr count = Marshal.AllocHGlobal(4);
            try
            {
                var buffer = new byte[1 << 20];
                using (var output = File.Create(iso))
                    for (; ; )
                    {
                        stream.Read(buffer, buffer.Length, count);
                        int n = Marshal.ReadInt32(count);
                        if (n == 0) break;
                        output.Write(buffer, 0, n);
                    }
            }
            finally { Marshal.FreeHGlobal(count); }
            return iso;
        }
    }
}
