// Background operations (VirtualBox, guest agent, transfers). The UI runs one at a time on a worker thread.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace UsbProxy
{
    public static class Worker
    {
        static string T(string s) { return Core.T(s); }
        static string VBox(params string[] a) { return Core.VBox(a); }
        const RegexOptions CI = RegexOptions.IgnoreCase;
        static string SerialLog { get { return Path.Combine(Core.Root, @"logs\vm-serial.log"); } }

        // Progress for the UI: replaced as a whole, read by the UI timer.
        public static volatile Map Progress;
        static void Phase(string phase) { Progress = J.Obj("phase", phase); }

        // Runs one request; never throws. {ok, data} or {ok=false, error}.
        public static Map Run(Map request)
        {
            Progress = null;
            FileStream lockFile = null;
            try
            {
                Directory.CreateDirectory(Core.Runtime);
                lockFile = new FileStream(Path.Combine(Core.Runtime, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return J.Obj("ok", true, "data", Operate(request));
            }
            catch (Exception e)
            {
                try
                {
                    Directory.CreateDirectory(Path.Combine(Core.Root, "logs"));
                    File.AppendAllText(Path.Combine(Core.Root, @"logs\worker-errors.txt"), DateTime.Now.ToString("o") + "\n" + e + "\n", Encoding.UTF8);
                }
                catch { }
                string message = e is IOException && lockFile == null ? T("另一個 USB Proxy 視窗正在處理工作，請稍候再試。") : e.Message;
                return J.Obj("ok", false, "error", message);
            }
            finally { if (lockFile != null) lockFile.Dispose(); }
        }

        // ---------------- USB devices ----------------
        static readonly string[] usbFields = { "VendorId", "ProductId", "Manufacturer", "Product", "SerialNumber", "Address", "Port", "Current State" };
        static string HexId(string value) { var m = Regex.Match(value ?? "", "^0x([0-9a-fA-F]{4})"); return m.Success ? m.Groups[1].Value : ""; }

        // VirtualBox's host USB list; without includeAll only mass-storage devices that are really plugged in.
        public static List<Map> UsbDevices(bool includeAll)
        {
            string raw = VBox("list", "usbhost");
            var storage = new List<string>();
            if (!includeAll)
            {
                using (var s = new ManagementObjectSearcher("SELECT PNPDeviceID,CompatibleID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\\\VID_%'"))
                    foreach (ManagementObject o in s.Get())
                    {
                        var ids = o["CompatibleID"] as string[];
                        if (ids != null && ids.Any(x => Regex.IsMatch(x ?? "", "^USB\\\\Class_08", CI))) storage.Add((string)o["PNPDeviceID"]);
                    }
            }
            var result = new List<Map>();
            foreach (string block in Regex.Split(raw, "(?:\\r?\\n){2,}"))
            {
                var u = Regex.Match(block, "(?m)^UUID:\\s*(\\S+)");
                if (!u.Success) continue;
                var item = J.Obj("uuid", u.Groups[1].Value);
                foreach (string field in usbFields)
                {
                    var m = Regex.Match(block, "(?m)^" + Regex.Escape(field) + ":[ \\t]*(.*)$");
                    item[field] = m.Success ? m.Groups[1].Value.Trim() : "";
                }
                if (includeAll) { result.Add(item); continue; }
                string vendor = HexId(J.S(item, "VendorId")), product = HexId(J.S(item, "ProductId"));
                if (vendor == "" || product == "") continue;
                var matching = storage.Where(id => Regex.IsMatch(id, "^USB\\\\VID_" + vendor + "&PID_" + product + "(?:&|\\\\)", CI)).ToList();
                if (matching.Count == 0) continue;
                string serial = J.S(item, "SerialNumber");
                if (serial != "")
                {
                    // VirtualBox keeps stale entries for drives it failed to capture; the serial must belong to a
                    // plugged-in drive. Windows prefixes some USB 3 drives' serials with "MSFT30" in the instance ID.
                    if (!matching.Any(id => { string last = id.Split('\\').Last(); return string.Equals(last, serial, StringComparison.OrdinalIgnoreCase) || string.Equals(last, "MSFT30" + serial, StringComparison.OrdinalIgnoreCase); }))
                        continue;
                }
                result.Add(item);
            }
            return result;
        }

        // Windows drive letters per USB serial number (empty when Windows cannot mount the drive).
        static Dictionary<string, List<string>> UsbDriveLetters()
        {
            var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var scope = new ManagementScope(@"\\.\root\Microsoft\Windows\Storage");
                var partitions = new List<KeyValuePair<long, int>>();
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT DiskNumber,DriveLetter FROM MSFT_Partition")))
                    foreach (ManagementObject o in s.Get())
                        partitions.Add(new KeyValuePair<long, int>(Convert.ToInt64(o["DiskNumber"]), o["DriveLetter"] == null ? 0 : Convert.ToInt32(o["DriveLetter"])));
                using (var s = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT Number,SerialNumber FROM MSFT_Disk WHERE BusType=7")))   // 7 = USB
                    foreach (ManagementObject o in s.Get())
                    {
                        string serial = Convert.ToString(o["SerialNumber"]);
                        if (string.IsNullOrWhiteSpace(serial)) continue;
                        long number = Convert.ToInt64(o["Number"]);
                        map[serial.Trim()] = partitions.Where(p => p.Key == number && p.Value != 0).Select(p => ((char)p.Value) + ":").ToList();
                    }
            }
            catch { }
            return map;
        }

        // USB devices VirtualBox has actually attached to the VM (from "Currently attached USB devices").
        static List<Map> VmUsb(Map config)
        {
            string text = VBox("showvminfo", J.S(config, "vmName"));
            var parts = text.Split(new[] { "Currently attached USB devices:" }, 2, StringSplitOptions.None);
            var list = new List<Map>();
            if (parts.Length < 2) return list;
            string section = Regex.Split(parts[1], "(?m)^Bandwidth groups:")[0];
            // Each device starts at "UUID:"; its own fields contain a blank line, so blank lines do not separate devices.
            foreach (string block in Regex.Split(section, "(?m)^(?=UUID:)"))
            {
                var u = Regex.Match(block, "(?m)^UUID:\\s*(\\S+)");
                if (!u.Success) continue;
                var s = Regex.Match(block, "(?m)^SerialNumber:[ \\t]*(.+)$");
                var name = new[] { "Manufacturer", "Product" }.Select(f => Regex.Match(block, "(?m)^" + f + ":[ \\t]*(.+)$"))
                    .Where(m => m.Success).Select(m => m.Groups[1].Value.Trim());
                Func<string, string> field = f => { var m = Regex.Match(block, "(?m)^" + f + ":[ \\t]*(.+)$"); return m.Success ? m.Groups[1].Value.Trim() : ""; };
                list.Add(J.Obj("uuid", u.Groups[1].Value, "serial", s.Success ? s.Groups[1].Value.Trim() : "", "name", string.Join(" ", name),
                    "VendorId", field("VendorId"), "ProductId", field("ProductId")));
            }
            return list;
        }

        // Drives handed to Linux: [{uuid, name, serial}].
        public static List<Map> Attached(Map config)
        {
            return J.Maps(config, "attachedUsbs").Where(d => J.S(d, "uuid") != "").ToList();
        }
        static void SetAttached(Map config, List<Map> list)
        {
            config["attachedUsbs"] = list.ToArray();
            Core.SaveConfig(config);
        }

        // Capture of an already-plugged drive is unreliable on some machines, while capture at insertion works:
        // a drive the user replugged arrives in the VM by itself. Make the recorded list match the VM.
        static void SyncAttached(Map config, Map guest, bool remember)
        {
            var inVm = VmUsb(config);
            var known = Attached(config);
            var guestSerials = J.Maps(guest, "volumes").Select(v => J.S(v, "serial")).ToList();
            // Keep the serial only if Linux reports the same one; an empty serial makes eject unmount every drive (safe).
            Func<Map, string> serialOf = d => J.S(d, "serial") != "" && guestSerials.Any(s => string.Equals(s, J.S(d, "serial"), StringComparison.OrdinalIgnoreCase)) ? J.S(d, "serial") : "";
            bool changed = false;
            var list = new List<Map>();
            foreach (var k in known)
            {
                var vm = inVm.FirstOrDefault(x => J.S(x, "uuid") == J.S(k, "uuid"));
                if (vm == null) continue;
                // A drive recorded before Linux showed its partitions gets its serial and name once it does.
                if (J.S(k, "serial") == "") { string s = serialOf(vm); if (s != "") { k["serial"] = s; changed = true; } }
                if (J.S(k, "name") == "" && J.S(vm, "name") != "") { k["name"] = J.S(vm, "name"); changed = true; }
                list.Add(k);
            }
            foreach (var d in inVm.Where(x => !known.Any(k => J.S(k, "uuid") == J.S(x, "uuid"))))
                list.Add(J.Obj("uuid", J.S(d, "uuid"), "name", J.S(d, "name"), "serial", serialOf(d)));
            if (changed || list.Count != known.Count || list.Any(x => !known.Any(k => J.S(k, "uuid") == J.S(x, "uuid")))) SetAttached(config, list);
            // A drive that arrived no longer needs its capture-on-insert filter.
            var arrived = inVm.Select(x => J.S(x, "serial") != "" ? J.S(x, "serial") : "any").ToList();
            if (arrived.Count > 0) RemovePendingFilters(config, arrived);
            // Every drive that made it into Linux goes straight there next time it is plugged in.
            if (remember) foreach (var d in inVm) Remember(config, J.S(d, "VendorId"), J.S(d, "ProductId"), J.S(d, "serial"), J.S(d, "name"));
        }

        // VM filters capture one specific drive (vendor + product + serial) the moment it is plugged in, without
        // Windows or this program in between. "usbproxy-pending-*": one-shot, for a stuck drive the user replugs.
        // "usbproxy-keep-*": a remembered drive that goes straight to Linux every time (also at Linux start).
        const string Pending = "usbproxy-pending-", Keep = "usbproxy-keep-";
        static List<KeyValuePair<int, string>> Filters(Map config, string prefix)
        {
            string raw = VBox("showvminfo", J.S(config, "vmName"), "--machinereadable");
            return Regex.Matches(raw, "(?m)^USBFilterName(\\d+)=\"(" + Regex.Escape(prefix) + "[^\"]*)\"").Cast<Match>()
                .Select(m => new KeyValuePair<int, string>(int.Parse(m.Groups[1].Value) - 1, m.Groups[2].Value)).ToList();
        }
        static void AddFilter(Map config, string prefix, string vendorId, string productId, string serial)
        {
            if (serial == "" || HexId(vendorId) == "" || HexId(productId) == "") return;   // never arm a filter that would grab every drive of this model
            string name = prefix + serial;
            if (Filters(config, prefix).Any(f => f.Value == name)) return;
            VBox("usbfilter", "add", "0", "--target", J.S(config, "vmName"), "--name", name,
                "--vendorid", HexId(vendorId), "--productid", HexId(productId), "--active", "yes", "--serialnumber", serial);
        }
        static void RemoveFilters(Map config, string prefix, List<string> serials = null)
        {
            // Highest index first, so the remaining indexes stay valid.
            foreach (var f in Filters(config, prefix).OrderByDescending(f => f.Key))
            {
                if (serials != null && !serials.Any(s => f.Value == prefix + s)) continue;
                try { VBox("usbfilter", "remove", f.Key.ToString(CultureInfo.InvariantCulture), "--target", J.S(config, "vmName")); } catch { }
            }
        }
        static void AddPendingFilter(Map config, Map device) { AddFilter(config, Pending, J.S(device, "VendorId"), J.S(device, "ProductId"), J.S(device, "SerialNumber")); }
        static void RemovePendingFilters(Map config, List<string> serials = null) { RemoveFilters(config, Pending, serials); }

        // Remembered drives: [{serial, name}] for the settings list; the keep filters themselves live in the VM.
        static List<Map> Remembered(Map config)
        {
            var names = J.M(config, "rememberedNames") ?? new Map();
            return Filters(config, Keep).Select(f => f.Value.Substring(Keep.Length))
                .Select(s => J.Obj("serial", s, "name", J.S(names, s))).ToList();
        }
        static void Remember(Map config, string vendorId, string productId, string serial, string name)
        {
            AddFilter(config, Keep, vendorId, productId, serial);
            if (serial == "") return;
            var names = J.M(config, "rememberedNames") ?? new Map();
            if (J.S(names, serial) == name || name == "") return;
            names[serial] = name; config["rememberedNames"] = names;
            Core.SaveConfig(config);
        }
        static void Forget(Map config, List<string> serials)
        {
            RemoveFilters(config, Keep, serials);
            var names = J.M(config, "rememberedNames");
            if (names == null) return;
            foreach (string s in serials == null ? names.Keys.ToList() : serials) names.Remove(s);
            Core.SaveConfig(config);
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        // CM_LOCATE_DEVNODE_NORMAL finds only devices that are present.
        public static bool DevicePresent(string instanceId) { uint d; return CM_Locate_DevNodeW(out d, instanceId, 0) == 0; }

        // A drive whose Windows device record is old is not re-enumerated when VirtualBox asks to capture it;
        // deleting that record (only while unplugged) fixes it. Needs admin: one UAC prompt, then this exe runs
        // elevated with --cleanup, waits up to 3 minutes for the unplug and removes just this drive's record.
        static void StartRecordCleanup(Map device)
        {
            string serial = J.S(device, "SerialNumber").Trim();
            if (serial == "") return;
            string prefix = "USB\\VID_" + HexId(J.S(device, "VendorId")) + "&PID_" + HexId(J.S(device, "ProductId")) + "\\";
            string id = new[] { prefix + serial, prefix + "MSFT30" + serial }.FirstOrDefault(DevicePresent);
            if (id == null) return;
            try
            {
                Process.Start(new ProcessStartInfo(Core.ExePath, "--cleanup " + Core.Quote(id)) { Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch { }   // declined UAC: replug may still work
        }
        public static void CleanupRecord(string id)
        {
            DateTime end = DateTime.Now.AddMinutes(3);
            while (DateTime.Now < end)
            {
                if (!DevicePresent(id))
                {
                    try { Core.Run(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), new[] { "/remove-device", id }); } catch { }
                    return;
                }
                Core.Sleep(500);
            }
        }

        // Names valid on Linux file systems may be invalid on Windows.
        static string WindowsName(string name)
        {
            string clean = Regex.Replace(name, "[\\\\/:*?\"<>|\\x00-\\x1f]", "_").TrimEnd(' ', '.');
            return clean != "" ? clean : "file";
        }

        // The package folder was moved: VirtualBox and config.json still point at the old location,
        // which makes the VM "inaccessible". Re-register it from runtime\machines with paths rewritten.
        static void RepairVmLocation(Map config)
        {
            string disk = Path.Combine(Core.Runtime, "usbproxy.vdi");
            if (File.Exists(J.S(config, "disk")) || !File.Exists(disk)) return;
            string vm = J.S(config, "vmName");
            string vbox = Path.Combine(Core.Runtime, "machines", vm, vm + ".vbox");
            if (!File.Exists(vbox)) return;
            string oldRoot = Path.GetDirectoryName(Path.GetDirectoryName(J.S(config, "disk")));
            var xml = new XmlDocument(); xml.Load(vbox);
            var machine = xml.DocumentElement["Machine"];
            try { VBox("unregistervm", machine.GetAttribute("uuid")); } catch { }   // stale entry; files are kept
            string backup = Path.Combine(Core.Root, @"backups\vbox-before-move-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(backup);
            foreach (string file in new[] { vbox, vbox + "-prev" })
            {
                if (!File.Exists(file)) continue;
                File.Copy(file, Path.Combine(backup, Path.GetFileName(file)), true);
                string text = File.ReadAllText(file);
                foreach (char sep in new[] { '\\', '/' })
                    text = Regex.Replace(text, Regex.Escape(oldRoot.Replace('\\', sep)), Core.Root.Replace('\\', sep).Replace("$", "$$"), CI);
                Core.WriteUtf8(file, text);
            }
            VBox("registervm", vbox);
            config["disk"] = disk;
            Core.SaveConfig(config);
        }

        // Windows OpenSSH ignores a private key others can read. Copying the folder to another drive
        // replaces the key's owner-only permissions with the folder's, so restore them when needed.
        static void ProtectSshKey()
        {
            string key = Path.Combine(Core.Runtime, "id_ed25519");
            if (!File.Exists(key)) return;
            var me = WindowsIdentity.GetCurrent();
            var rules = File.GetAccessControl(key).GetAccessRules(true, true, typeof(SecurityIdentifier));
            if (!rules.Cast<FileSystemAccessRule>().Any(r => !r.IdentityReference.Equals(me.User))) return;
            Core.Run("icacls.exe", new[] { key, "/reset" });
            Core.Run("icacls.exe", new[] { key, "/inheritance:r", "/grant:r", me.Name + ":(F)" });
        }

        static Map Info(Map config)
        {
            string raw = VBox("showvminfo", J.S(config, "vmName"), "--machinereadable");
            var m = Regex.Match(raw, "(?m)^VMState=\"([^\"]+)\"");
            string state = m.Success ? m.Groups[1].Value : "unknown";
            if (state == "running" && J.S(config, "shutdownRequestedAt") != "") state = "stopping";
            return J.Obj("state", state, "attached", Attached(config).ToArray(), "vmName", J.S(config, "vmName"));
        }
        static string State(Map config) { return J.S(Info(config), "state"); }
        static Map GuestStatus(Map config) { return Core.Guest(config, J.Obj("action", "status")); }

        static long SerialLogSize() { return File.Exists(SerialLog) ? new FileInfo(SerialLog).Length : 0; }

        static void StartGuestVm(Map config)
        {
            string vm = J.S(config, "vmName");
            // Capture the boot console to a file so stalls can be detected and diagnosed.
            string raw = VBox("showvminfo", vm, "--machinereadable");
            if (!raw.Contains("uartmode1=\"file," + SerialLog.Replace("\\", "\\\\") + "\"") && !raw.Contains("uartmode1=\"file," + SerialLog + "\""))
                VBox("modifyvm", vm, "--uart1", "0x3F8", "4", "--uart-mode1", "file", SerialLog);
            // CPU/memory from the settings dialog. Default 1 CPU: with Hyper-V active, VirtualBox runs through NEM
            // where SMP guests intermittently stall at boot (the start action power-cycles a stalled boot once).
            int cpus = J.I(config, "vmCpus") >= 1 ? J.I(config, "vmCpus") : 1;
            int memory = J.I(config, "vmMemory") >= 512 ? J.I(config, "vmMemory") : 1024;
            if (!Regex.IsMatch(raw, "(?m)^cpus=" + cpus + "\\s*$") || !Regex.IsMatch(raw, "(?m)^memory=" + memory + "\\s*$"))
                VBox("modifyvm", vm, "--cpus", cpus.ToString(), "--memory", memory.ToString());
            VBox("startvm", vm, "--type", "headless");
        }

        // The provisioned Linux disk is sealed as an immutable image: VirtualBox discards every change
        // at each VM start, so the guest always boots from the same clean state and never grows.
        static bool DiskSealed(Map config) { return Regex.IsMatch(VBox("showmediuminfo", "disk", J.S(config, "disk")), "(?m)^Type:\\s+immutable"); }

        static void SetDiskType(Map config, string type)
        {
            string vm = J.S(config, "vmName"), disk = J.S(config, "disk");
            VBox("storageattach", vm, "--storagectl", "SATA", "--port", "0", "--device", "0", "--medium", "none");
            // Remove the differencing image VirtualBox keeps for an immutable disk before changing its type.
            string info = VBox("showmediuminfo", "disk", disk);
            const string uuid = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
            var block = Regex.Match(info, "(?m)^Child UUIDs:\\s*" + uuid + "(?:\\r?\\n\\s+" + uuid + ")*");
            foreach (Match child in Regex.Matches(block.Value, uuid))
                try { VBox("closemedium", "disk", child.Value, "--delete"); } catch { }
            VBox("modifymedium", "disk", disk, "--type", type);
            VBox("storageattach", vm, "--storagectl", "SATA", "--port", "0", "--device", "0", "--type", "hdd", "--medium", disk);
        }

        static void StopGuestVm(Map config)
        {
            string vm = J.S(config, "vmName");
            try { Core.Guest(config, J.Obj("action", "shutdown")); } catch { }
            try { VBox("controlvm", vm, "acpipowerbutton"); } catch { }
            DateTime deadline = DateTime.Now.AddSeconds(90);
            while (State(config) != "poweroff")
            {
                if (DateTime.Now > deadline) { VBox("controlvm", vm, "poweroff"); Core.Sleep(2000); break; }
                Core.Sleep(2000);
            }
            config["shutdownRequestedAt"] = null;
            Core.SaveConfig(config);
        }

        static bool WaitGuestReady(Map config, int seconds)
        {
            DateTime deadline = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < deadline)
            {
                try { if (J.B(GuestStatus(config), "ready")) return true; } catch { }
                Core.Sleep(3000);
            }
            return false;
        }

        // Keeps the guest agent in step with guest\usbproxy-agent.py after an application update.
        static string AgentFile { get { return Path.Combine(Core.Root, @"guest\usbproxy-agent.py"); } }
        static string LocalAgentHash() { return Core.Sha256File(AgentFile); }

        static bool SyncGuestAgent(Map config)
        {
            string expected = LocalAgentHash();
            Func<string> guestHash = () => Regex.Split(Core.Ssh(config, "sha256sum /usr/local/sbin/usbproxy-agent"), "\\s+")[0];
            if (guestHash() == expected)
            {
                if (J.S(config, "agentHash") != expected) { config["agentHash"] = expected; Core.SaveConfig(config); }
                return false;
            }
            Phase("updating");
            string staged = "/home/usbproxy/incoming/agent-" + Core.NewId() + ".py";
            var scp = Core.SshArgs(config, true); scp.Add(AgentFile); scp.Add("usbproxy@127.0.0.1:" + staged);
            Core.Run("scp.exe", scp);
            try { Core.Guest(config, J.Obj("action", "self-update", "source", staged, "sha256", expected)); }
            finally { try { Core.Ssh(config, "rm -f -- " + staged); } catch { } }
            if (guestHash() != expected) throw new ToolException("Guest agent update could not be verified.");
            config["agentHash"] = expected;
            Core.SaveConfig(config);
            return true;
        }

        // ---------------- Transfers ----------------
        // Transfer progress for the UI: stage, current file, and weighted units done/total.
        class TransferState { public string Stage = "", File = ""; public int Index, Count; public long Done, Total = 1; public DateTime Last = DateTime.MinValue; }
        static TransferState transfer = new TransferState();
        static void StartTransfer(long total, int count) { transfer = new TransferState { Total = Math.Max(1, total), Count = count }; }
        static void ReportTransfer(string stage, string file, int index, long add, bool force = false)
        {
            var t = transfer;
            t.Stage = stage; t.File = file; t.Index = index; t.Done += add;
            if (!force && (DateTime.Now - t.Last).TotalMilliseconds < 250) return;
            t.Last = DateTime.Now;
            Progress = J.Obj("stage", t.Stage, "file", t.File, "index", t.Index, "count", t.Count, "done", Math.Min(t.Done, t.Total), "total", t.Total);
        }
        // Guest-side progress (written by the agent next to the spool) folded into ours.
        static Action GuestProgressPoll(Map config, string remote, bool replaceTotals, long offset)
        {
            return () =>
            {
                string text = Core.Ssh(config, "cat " + remote + "/.progress 2>/dev/null || true");
                if (text == "") return;
                var g = J.Read(text);
                if (replaceTotals) { transfer.Total = Math.Max(1, J.L(g, "total")); transfer.Count = J.I(g, "count"); }
                transfer.Done = offset + J.L(g, "done");
                ReportTransfer(J.S(g, "stage"), J.S(g, "file"), J.I(g, "index"), 0, true);
            };
        }

        class PlanItem { public FileInfo Source; public string Relative; }

        // Files to copy with their destination paths; folders keep their own name as the top level.
        static List<PlanItem> CopyPlan(Map request)
        {
            var plan = new List<PlanItem>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in J.Strings(request, "paths"))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ToolException("Symbolic links and junctions are not supported.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // Enumerate explicitly so a junction cannot escape the selected directory.
                    var top = new DirectoryInfo(path);
                    string baseDir = top.Parent.FullName.TrimEnd('\\') + "\\";
                    var queue = new Queue<DirectoryInfo>(); queue.Enqueue(top);
                    while (queue.Count > 0)
                    {
                        foreach (var child in queue.Dequeue().GetFileSystemInfos())
                        {
                            if ((child.Attributes & FileAttributes.ReparsePoint) != 0) throw new ToolException("Symbolic link/junction found: " + child.FullName);
                            if (child is DirectoryInfo) { queue.Enqueue((DirectoryInfo)child); continue; }
                            string relative = child.FullName.Substring(baseDir.Length).Replace('\\', '/');
                            if (!names.Add(relative)) throw new ToolException("Duplicate destination: " + relative);
                            plan.Add(new PlanItem { Source = (FileInfo)child, Relative = relative });
                        }
                    }
                }
                else
                {
                    var file = new FileInfo(path);
                    // Write-back of an opened file keeps its original name on the USB volume.
                    string relative = J.S(request, "targetName") != "" ? J.S(request, "targetName") : file.Name;
                    if (!names.Add(relative)) throw new ToolException("Duplicate destination: " + relative);
                    plan.Add(new PlanItem { Source = file, Relative = relative });
                }
            }
            return plan;
        }

        static byte[] TarHeader(string name, long size)
        {
            var header = new byte[512];
            Action<string, int> put = (s, at) => Encoding.ASCII.GetBytes(s).CopyTo(header, at);
            put(name, 0); put("0000644", 100); put("0000000", 108); put("0000000", 116);
            if (size < 8589934592L) put(Convert.ToString(size, 8).PadLeft(11, '0'), 124);
            else
            {
                // GNU base-256 size field for files of 8 GiB or more.
                header[124] = 0x80;
                for (int i = 0; i < 8; i++) header[135 - i] = (byte)((size >> (8 * i)) & 0xFF);
            }
            long mtime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            put(Convert.ToString(mtime, 8).PadLeft(11, '0'), 136);
            for (int i = 148; i < 156; i++) header[i] = 0x20;
            header[156] = (byte)'0';
            put("ustar", 257); put("00", 263);
            int sum = header.Sum(b => (int)b);
            put(Convert.ToString(sum, 8).PadLeft(6, '0'), 148);
            header[154] = 0; header[155] = 0x20;
            return header;
        }

        // Streams the files as a tar archive into the guest spool, hashing each one on the way.
        static void SendTar(Map config, string remote, List<PlanItem> plan)
        {
            using (var p = Core.StartHidden("ssh.exe", Core.SshTo(config, "mkdir -p -- " + remote + " && tar -x --no-same-owner --no-same-permissions -f - -C " + remote)))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                var output = p.StandardInput.BaseStream;
                var buffer = new byte[1 << 20];
                var zeros = new byte[1024];
                var files = new List<object>();
                try
                {
                    for (int i = 0; i < plan.Count; i++)
                    {
                        var item = plan[i];
                        long size = item.Source.Length;
                        string blob = "file-" + i.ToString("D8");
                        output.Write(TarHeader(blob, size), 0, 512);
                        string hash;
                        using (var sha = SHA256.Create())
                        using (var input = new FileStream(item.Source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            ReportTransfer("upload", item.Relative, i + 1, 0, true);
                            long remaining = size;
                            while (remaining > 0)
                            {
                                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                                if (n <= 0) throw new ToolException("File changed while copying: " + item.Source.FullName);
                                sha.TransformBlock(buffer, 0, n, null, 0);
                                output.Write(buffer, 0, n);
                                remaining -= n;
                                ReportTransfer("upload", item.Relative, i + 1, n);
                            }
                            if (input.ReadByte() != -1) throw new ToolException("File changed while copying: " + item.Source.FullName);
                            sha.TransformFinalBlock(buffer, 0, 0);
                            hash = Core.Hex(sha.Hash);
                        }
                        int pad = (int)((512 - (size % 512)) % 512);
                        if (pad > 0) output.Write(zeros, 0, pad);
                        files.Add(J.Obj("relative", item.Relative, "blob", blob, "size", size, "sha256", hash));
                    }
                    byte[] manifest = Core.Utf8.GetBytes(J.Write(J.Obj("files", files.ToArray())));
                    output.Write(TarHeader("manifest.json", manifest.Length), 0, 512);
                    output.Write(manifest, 0, manifest.Length);
                    int mpad = (512 - (manifest.Length % 512)) % 512;
                    if (mpad > 0) output.Write(zeros, 0, mpad);
                    output.Write(zeros, 0, 1024);
                    output.Close();
                }
                catch (Exception e)
                {
                    try { p.Kill(); } catch { }
                    string detail = "";
                    try { detail = stderr.Result; } catch { }
                    throw new ToolException("Upload to Linux failed: " + e.Message + " " + detail);
                }
                if (!p.WaitForExit(600000)) { try { p.Kill(); } catch { } throw new ToolException("Upload to Linux timed out."); }
                p.WaitForExit();
                if (p.ExitCode != 0) throw new ToolException("Upload to Linux failed: " + stderr.Result);
            }
        }

        // Receives one file streamed by the agent; its JSON result arrives on stderr.
        static Map ReceiveStream(Map config, Map request, string local, string label, int index = 1)
        {
            using (var p = Core.StartHidden("ssh.exe", Core.SshTo(config, "sudo -n /usr/local/sbin/usbproxy-agent")))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                Core.WriteLine(p, J.Write(request));
                p.StandardInput.Close();
                var input = p.StandardOutput.BaseStream;
                var buffer = new byte[1 << 20];
                long size = 0;
                string hash;
                using (var sha = SHA256.Create())
                {
                    using (var file = new FileStream(local, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ReportTransfer("download", label, index, 0, true);
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            sha.TransformBlock(buffer, 0, n, null, 0);
                            file.Write(buffer, 0, n);
                            size += n;
                            ReportTransfer("download", label, index, n);
                        }
                    }
                    sha.TransformFinalBlock(buffer, 0, 0);
                    hash = Core.Hex(sha.Hash);
                }
                p.WaitForExit();
                var line = stderr.Result.Split('\n').LastOrDefault(l => Regex.IsMatch(l, "^\\s*\\{"));
                if (line == null) throw new ToolException("Linux did not return a result (exit " + p.ExitCode + ").");
                var response = J.Read(line);
                if (!J.B(response, "ok")) throw new ToolException(J.S(response, "error"));
                if (p.ExitCode != 0) throw new ToolException("Linux returned exit " + p.ExitCode);
                var data = J.M(response, "data");
                if (size != J.L(data, "size") || hash != J.S(data, "sha256")) throw new ToolException("Downloaded file failed SHA-256 verification.");
                return data;
            }
        }

        // Unmounts a drive's partitions in the guest, then hands the device back to Windows.
        static void Eject(Map config, string uuid, bool forget)
        {
            bool running = State(config) == "running";
            foreach (var drive in Attached(config).Where(d => uuid == "" || J.S(d, "uuid") == uuid).ToList())
            {
                // Without a known serial the guest unmounts everything; other drives remount on next use.
                if (running) Core.Guest(config, J.Obj("action", "eject", "serial", J.S(drive, "serial")));
                // Given back to Windows on purpose: forget it before releasing it, so VirtualBox does not grab it again.
                var vmDrive = VmUsb(config).FirstOrDefault(d => J.S(d, "uuid") == J.S(drive, "uuid"));
                if (forget && vmDrive != null && J.S(vmDrive, "serial") != "") try { Forget(config, new List<string> { J.S(vmDrive, "serial") }); } catch { }
                try { VBox("controlvm", J.S(config, "vmName"), "usbdetach", J.S(drive, "uuid")); }
                catch
                {
                    // An unplugged/already released device needs only local state cleanup.
                    if (UsbDevices(true).Any(d => J.S(d, "uuid") == J.S(drive, "uuid") && J.S(d, "Current State") == "Captured")) throw;
                }
                SetAttached(config, Attached(config).Where(d => J.S(d, "uuid") != J.S(drive, "uuid")).ToList());
            }
            // An ejected drive must not be grabbed again when it is next plugged in.
            if (running) try { RemovePendingFilters(config); } catch { }
        }

        static bool Installed() { return File.Exists(Core.VBoxManage) && File.Exists(Core.ConfigFile); }

        static void RequireRunning(Map config) { if (State(config) != "running") throw new ToolException("Linux is not running or is shutting down."); }

        // ---------------- Operations ----------------
        static Map Operate(Map request)
        {
            string action = J.S(request, "action");
            if (action == "setup") return Setup.Run();
            if (!Installed())
            {
                if (action == "status") return J.Obj("installed", false, "vm", J.Obj("state", "notinstalled", "attached", new object[0]), "guest", null, "message", "", "usb", new object[0]);
                throw new ToolException(T("尚未安裝 Linux 環境，請先按「安裝」。"));
            }
            var config = Core.ReadConfig();
            RepairVmLocation(config);
            ProtectSshKey();
            switch (action)
            {
                case "status": return Status(config, request);
                case "start": return Start(config, request);
                case "attach": return Attach(config, request);
                case "copy": return Copy(config, request);
                case "list": case "delete": case "mkdir": case "rename": case "info": case "format":
                    {
                        if (action != "list") RequireRunning(config);
                        var g = J.Obj("action", action, "device", J.S(request, "device"));
                        foreach (string key in new[] { "path", "name", "fstype", "label" }) if (J.Get(request, key) != null) g[key] = J.S(request, key);
                        if (J.Get(request, "paths") != null) g["paths"] = J.Strings(request, "paths").ToArray();
                        try { return Core.Guest(config, g, 600); }
                        catch { RequireRunning(config); throw; }
                    }
                case "fetch": return Fetch(config, request);
                case "transfer": return Transfer(config, request);
                case "download": return Download(config, request);
                case "eject": Eject(config, J.S(request, "uuid"), true); return J.Obj("ejected", true);
                case "forget": Forget(config, J.Strings(request, "serials")); return J.Obj("remembered", Remembered(config).ToArray());
                case "shutdown": return Shutdown(config);
                default: throw new ToolException("Unknown operation.");
            }
        }

        static Map Status(Map config, Map request)
        {
            // "remember" follows the auto-switch setting (absent = on); turning it off forgets every drive.
            bool remember = J.Get(request, "remember") == null || J.B(request, "remember");
            Map guest = null;
            string message = "";
            Map info;
            // Keep listing USB drives even when VirtualBox cannot report the VM.
            try { info = Info(config); }
            catch (Exception e) { info = J.Obj("state", "error", "attached", new object[0], "vmName", J.S(config, "vmName")); message = e.Message; }
            if (J.S(info, "state") == "running")
            {
                try { guest = GuestStatus(config); } catch (Exception e) { message = e.Message; }
                // An already-running guest picks up a newer agent without a restart.
                if (guest != null && J.S(config, "agentHash") != LocalAgentHash()) try { SyncGuestAgent(config); } catch { }
                // Pick up drives that arrived in the VM on replug (see SyncAttached).
                try
                {
                    var before = Attached(config).Select(d => J.S(d, "uuid")).ToList();
                    SyncAttached(config, guest, remember);
                    // A drive that just arrived needs a few seconds before Linux shows its partitions.
                    if (guest != null && Attached(config).Any(d => !before.Contains(J.S(d, "uuid"))))
                    {
                        int count = J.Maps(guest, "volumes").Count;
                        DateTime deadline = DateTime.Now.AddSeconds(10);
                        while (DateTime.Now < deadline)
                        {
                            Core.Sleep(1000);
                            try { guest = GuestStatus(config); } catch { continue; }
                            if (J.Maps(guest, "volumes").Count > count) break;
                        }
                        SyncAttached(config, guest, remember);
                    }
                    info["attached"] = Attached(config).ToArray();
                }
                catch { }
            }
            var letters = UsbDriveLetters();
            // "Captured" but not in this VM = stuck capture (or another VM). Without a serial it cannot be told
            // apart from the plugged-in drive, so drop it; with one, show it once, flagged as stuck.
            var mine = J.Maps(info, "attached").Select(d => J.S(d, "uuid")).ToList();
            var usb = new List<object>();
            foreach (var d in UsbDevices(false))
            {
                bool captured = J.S(d, "Current State") == "Captured";
                if (captured && !mine.Contains(J.S(d, "uuid")) && J.S(d, "SerialNumber") == "") continue;
                bool stuck = captured && !mine.Contains(J.S(d, "uuid"));
                // A stuck drive is one the user asked to switch: arm capture-on-insert so replugging it is enough.
                if (stuck && J.S(info, "state") == "running") try { AddPendingFilter(config, d); } catch { }
                d["stuck"] = stuck;
                string serial = J.S(d, "SerialNumber").Trim();
                d["drives"] = serial != "" && letters.ContainsKey(serial) ? letters[serial].ToArray() : new string[0];
                usb.Add(d);
            }
            var remembered = new List<Map>();
            try
            {
                if (!remember) Forget(config, null);
                remembered = Remembered(config);
            }
            catch { }
            return J.Obj("vm", info, "guest", guest, "message", message, "usb", usb.ToArray(), "remembered", remembered.ToArray());
        }

        static Map Start(Map config, Map request)
        {
            var info = Info(config);
            string state = J.S(info, "state");
            var history = J.A(config, "bootSeconds").Select(x => Convert.ToInt32(x)).Where(x => x > 0).ToList();
            bool booted = false;
            // Windows restarted/crashed under a running VM: VirtualBox leaves it "saved" or "aborted".
            // The disk resets to the clean image on every boot, so the saved memory is worthless: discard and cold-boot.
            if (state == "saved") { VBox("discardstate", J.S(config, "vmName")); state = "poweroff"; }
            if (state == "aborted") state = "poweroff";
            if (state == "poweroff")
            {
                if (J.I(request, "cpus") >= 1) config["vmCpus"] = J.I(request, "cpus");
                if (J.I(request, "memory") >= 512) config["vmMemory"] = J.I(request, "memory");
                SetAttached(config, new List<Map>());   // drives cannot still be in a VM that was not running
                RemovePendingFilters(config);            // one-shot filters do not outlive a boot; remembered drives are grabbed at boot
                config["shutdownRequestedAt"] = null;
                Core.SaveConfig(config);
                StartGuestVm(config);
                booted = true;
            }
            else if (state != "running") throw new ToolException("VM is " + state + ". Resolve this state before starting.");
            var watch = Stopwatch.StartNew();
            DateTime deadline = DateTime.Now.AddMinutes(8);
            // A boot that has completed before may stall silently under Hyper-V; the first boot installs packages.
            int stallSeconds = history.Count > 0 ? 90 : 240;
            bool retried = false;
            long lastSize = -1; DateTime lastGrowth = DateTime.Now;
            while (DateTime.Now < deadline)
            {
                bool ready = false;
                try { ready = J.B(GuestStatus(config), "ready"); } catch { }
                if (ready)
                {
                    if (booted)
                    {
                        history.Add((int)watch.Elapsed.TotalSeconds);
                        config["bootSeconds"] = history.Skip(Math.Max(0, history.Count - 5)).ToArray();
                        Core.SaveConfig(config);
                    }
                    bool updated = SyncGuestAgent(config);
                    bool sealedNow = false;
                    if (!DiskSealed(config))
                    {
                        // First run after provisioning: freeze this clean state as the image every start resets to.
                        Phase("sealing");
                        StopGuestVm(config);
                        SetDiskType(config, "immutable");
                        StartGuestVm(config);
                        if (!WaitGuestReady(config, 300)) throw new ToolException("Linux did not start from the sealed image.");
                        SyncGuestAgent(config);
                        sealedNow = true;
                    }
                    return J.Obj("ready", true, "vmName", J.S(config, "vmName"), "retried", retried, "agentUpdated", updated, "sealed", sealedNow);
                }
                long size = SerialLogSize();
                if (size != lastSize) { lastSize = size; lastGrowth = DateTime.Now; }
                else if (booted && !retried && (DateTime.Now - lastGrowth).TotalSeconds > stallSeconds)
                {
                    // Console output stopped and the guest never answered: power-cycle once.
                    Phase("retry");
                    VBox("controlvm", J.S(config, "vmName"), "poweroff");
                    Core.Sleep(2000);
                    StartGuestVm(config);
                    retried = true;
                    watch.Restart();
                    deadline = DateTime.Now.AddMinutes(6);
                    lastSize = -1; lastGrowth = DateTime.Now;
                }
                Core.Sleep(3000);
            }
            throw new ToolException("Linux is not ready after eight minutes. See logs\\vm-serial.log for the boot console.");
        }

        static Map Attach(Map config, Map request)
        {
            if (State(config) != "running") throw new ToolException("Start Linux first.");
            string uuid = J.S(request, "uuid");
            var device = UsbDevices(true).Where(d => J.S(d, "uuid") == uuid).ToList();   // the UI already chose a drive; only its state is needed
            if (device.Count != 1) throw new ToolException("USB device is no longer available. Refresh the list.");
            string current = J.S(device[0], "Current State");
            string replug = T("請拔下這支隨身碟再插回，就會出現在 Linux 中。");
            if (current == "Captured")
            {
                // VirtualBox took the drive from Windows but never handed it to a VM (seen with USBSTOR disabled).
                if (!Attached(config).Any(d => J.S(d, "uuid") == uuid))
                {
                    AddPendingFilter(config, device[0]);
                    StartRecordCleanup(device[0]);
                    throw new ToolException(replug);
                }
                throw new ToolException(T("這支隨身碟已在 Linux 中。"));
            }
            if (current != "Available" && current != "Busy") throw new ToolException(string.Format(T("這支隨身碟目前無法切換（狀態：{0}）。請拔下再插回後重試。"), current));
            var before = new List<string>();
            try { before = J.Maps(GuestStatus(config), "volumes").Select(v => J.S(v, "path")).ToList(); } catch { }
            // The UI requires explicit selection. No wildcard filter captures keyboards or other USB devices.
            VBox("controlvm", J.S(config, "vmName"), "usbattach", uuid);
            var list = Attached(config);
            list.Add(J.Obj("uuid", uuid, "name", J.S(request, "name"), "serial", J.S(device[0], "SerialNumber").Trim()));
            SetAttached(config, list);
            // The guest needs a few seconds to enumerate the disk. It waits for the new partitions itself (one SSH
            // call, checking every 0.2 s) and mounts a single new volume so opening it is immediate.
            Map guest = null;
            try { guest = Core.Guest(config, J.Obj("action", "wait-volume", "before", before.ToArray(), "timeout", 20), 45); } catch { }
            Func<Map, List<Map>> newOf = g => J.Maps(g, "volumes").Where(v => !before.Contains(J.S(v, "path"))).ToList();
            var fresh = newOf(guest);
            if (guest == null) guest = GuestStatus(config);
            if (fresh.Count == 0 && !VBox("showvminfo", J.S(config, "vmName"), "--machinereadable").Contains(uuid))
            {
                // Capturing an already-plugged drive did not complete (unreliable on some machines), but capture
                // at insertion works: arm a filter for this drive and let the user replug it.
                SetAttached(config, Attached(config).Where(d => J.S(d, "uuid") != uuid).ToList());
                AddPendingFilter(config, device[0]);
                StartRecordCleanup(device[0]);
                throw new ToolException(replug);
            }
            if (J.Get(request, "remember") == null || J.B(request, "remember"))
                try { Remember(config, J.S(device[0], "VendorId"), J.S(device[0], "ProductId"), J.S(device[0], "SerialNumber").Trim(), J.S(request, "name")); } catch { }
            // Remember the serial Linux reports, which can differ in format from VirtualBox's.
            var reported = fresh.Select(v => J.S(v, "serial")).Where(s => s != "").Distinct().ToList();
            if (reported.Count == 1)
            {
                var updated = Attached(config);
                foreach (var d in updated) if (J.S(d, "uuid") == uuid) d["serial"] = reported[0];
                SetAttached(config, updated);
            }
            return J.Obj("attached", uuid, "guest", guest, "volumes", fresh.Select(v => (object)J.S(v, "path")).ToArray());
        }

        static Map Copy(Map config, Map request)
        {
            RequireRunning(config);
            var guest = GuestStatus(config);
            var selected = J.Maps(guest, "volumes").Where(v => J.S(v, "path") == J.S(request, "device")).ToList();
            if (selected.Count != 1) throw new ToolException("Select a supported USB volume.");
            var plan = CopyPlan(request);
            if (plan.Count == 0) throw new ToolException("The selection contains no files. Empty directories are not copied.");
            long total = plan.Sum(p => p.Source.Length);
            if (J.S(selected[0], "fstype") == "vfat" && plan.Any(p => p.Source.Length >= 4294967296L)) throw new ToolException("FAT32 cannot store a file of 4 GiB or larger.");
            long free = long.Parse(Core.Ssh(config, "df -B1 --output=avail /home/usbproxy/incoming | tail -n 1"), CultureInfo.InvariantCulture);
            if (free < total + (1 << 20)) throw new ToolException("Not enough staging space in the Linux VM. Copy a smaller batch.");
            // Progress units: upload (1x) then the guest writes and reads back each byte (2x).
            StartTransfer(total * 3, plan.Count);
            string remote = "/home/usbproxy/incoming/" + Core.NewId();
            try
            {
                SendTar(config, remote, plan);
                return Core.Guest(config, J.Obj("action", "copy", "device", J.S(request, "device"), "spool", remote,
                    "dest", J.S(request, "dest"), "overwrite", J.B(request, "overwrite")), 7200, GuestProgressPoll(config, remote, false, total));
            }
            finally
            {
                // The job id is a locally generated lowercase GUID. Remove only this job's guest spool.
                try { Core.Ssh(config, "rm -rf -- " + remote); } catch { }
            }
        }

        static Map Fetch(Map config, Map request)
        {
            // No VirtualBox state check up front (it costs a VBoxManage call); a failure explains itself below.
            string leaf = J.S(request, "path").Split('/').Last();
            string folder = Path.Combine(Core.Runtime, "open", Core.NewId());
            Directory.CreateDirectory(folder);
            string local = Path.Combine(folder, WindowsName(leaf));
            StartTransfer(J.L(request, "size"), 1);
            Map result;
            try { result = ReceiveStream(config, J.Obj("action", "stream", "device", J.S(request, "device"), "path", J.S(request, "path")), local, leaf); }
            catch { try { Directory.Delete(folder, true); } catch { } RequireRunning(config); throw; }
            string saveTo = J.S(request, "saveTo");
            if (saveTo != "")
            {
                if (File.Exists(saveTo)) File.Delete(saveTo);
                File.Move(local, saveTo);
                Directory.Delete(folder, true);
                return J.Obj("saved", saveTo, "path", J.S(result, "path"));
            }
            return J.Obj("local", local, "path", J.S(result, "path"), "name", J.S(result, "name"));
        }

        // Volume to volume (or folder to folder) entirely inside the guest.
        static Map Transfer(Map config, Map request)
        {
            RequireRunning(config);
            string remote = "/home/usbproxy/incoming/" + Core.NewId();
            Core.Ssh(config, "mkdir -- " + remote);
            StartTransfer(1, 0);
            try
            {
                return Core.Guest(config, J.Obj("action", "transfer", "source", J.S(request, "source"), "device", J.S(request, "device"),
                    "paths", J.Strings(request, "paths").ToArray(), "dest", J.S(request, "dest"), "spool", remote), 7200, GuestProgressPoll(config, remote, true, 0));
            }
            finally { try { Core.Ssh(config, "rm -rf -- " + remote); } catch { } }
        }

        // Files and folders from a volume into a Windows folder; existing names are never overwritten.
        static Map Download(Map config, Map request)
        {
            RequireRunning(config);
            string destination = J.S(request, "destination");
            if (!Directory.Exists(destination)) throw new ToolException("Destination folder not found.");
            var plan = Core.Guest(config, J.Obj("action", "walk", "device", J.S(request, "device"), "paths", J.Strings(request, "paths").ToArray()), 600);
            var files = J.Maps(plan, "files");
            var folders = J.Strings(plan, "folders");
            Func<string, string> toLocal = relative => Path.Combine(destination, string.Join("\\", relative.Split('/').Select(WindowsName)));
            foreach (string item in files.Select(f => J.S(f, "relative")).Concat(folders))
            {
                string top = Path.Combine(destination, WindowsName(item.Split('/')[0]));
                if (File.Exists(top) || Directory.Exists(top)) throw new ToolException("Already exists on this PC: " + Path.GetFileName(top));
            }
            long total = files.Sum(f => J.L(f, "size"));
            if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(destination))).AvailableFreeSpace < total + (1 << 20)) throw new ToolException("Not enough free space on this PC.");
            StartTransfer(total, files.Count);
            foreach (string folder in folders) Directory.CreateDirectory(toLocal(folder));
            int index = 0;
            foreach (var file in files)
            {
                index++;
                string target = toLocal(J.S(file, "relative"));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                string partial = target + ".usbproxy-partial";
                try
                {
                    ReceiveStream(config, J.Obj("action", "stream", "device", J.S(request, "device"), "path", J.S(file, "path")), partial, J.S(file, "relative"), index);
                    File.Move(partial, target);
                }
                catch { try { File.Delete(partial); } catch { } throw; }
            }
            return J.Obj("downloaded", files.Count, "bytes", total, "destination", destination);
        }

        static Map Shutdown(Map config)
        {
            // Safely eject first: unmount (sync) in the guest, then hand the devices back to Windows.
            bool ejected = Attached(config).Count > 0;
            if (ejected) Eject(config, "", false);
            if (State(config) != "running") return J.Obj("shutdown", true, "ejected", ejected);
            // The sealed disk discards every change at the next start, so cutting the power loses nothing.
            // The drives were already unmounted and synced above, which is what protects their data.
            if (DiskSealed(config))
            {
                VBox("controlvm", J.S(config, "vmName"), "poweroff");
                config["shutdownRequestedAt"] = null;
                Core.SaveConfig(config);
                return J.Obj("shutdown", true, "ejected", ejected, "off", true);
            }
            // Not sealed yet (first run): a clean shutdown, so the image that gets sealed is consistent.
            // The agent syncs, unmounts and schedules a fallback 'shutdown -h +1'.
            Core.Guest(config, J.Obj("action", "shutdown"));
            config["shutdownRequestedAt"] = DateTime.Now.ToString("o");
            Core.SaveConfig(config);
            // An ACPI power-button press makes systemd power off now instead of a minute later.
            try { VBox("controlvm", J.S(config, "vmName"), "acpipowerbutton"); } catch { }
            DateTime deadline = DateTime.Now.AddSeconds(75);
            while (DateTime.Now < deadline)
            {
                if (State(config) == "poweroff") return J.Obj("shutdown", true, "ejected", ejected, "off", true);
                Core.Sleep(2000);
            }
            return J.Obj("shutdown", true, "ejected", ejected, "off", false);
        }
    }
}
