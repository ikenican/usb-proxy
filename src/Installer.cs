// USBProxy-Setup.exe: this exe with the full package zip appended (then the zip length as 8 bytes).
// Extracts per user (no admin), creates Start menu / desktop shortcuts and opens USB Proxy.
// The first start of USB Proxy then installs VirtualBox and the Linux VM from the bundled files.
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

static class Installer
{
    static readonly string[] keep = { "runtime", "logs", "backups" };   // existing installs keep their data

    [STAThread]
    static void Main(string[] args)
    {
        // Silent extract for testing: USBProxy-Setup.exe --target <folder>
        if (args.Length == 2 && args[0] == "--target") { Extract(args[1], (d, t) => { }); return; }
        Application.EnableVisualStyles();
        var form = new Form { Text = "USB Proxy 安裝", ClientSize = new Size(520, 210), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, Font = new Font("Microsoft JhengHei UI", 9.5f), AutoScaleMode = AutoScaleMode.Dpi };
        try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var label = new Label { Text = "安裝位置：", Location = new Point(16, 18), AutoSize = true };
        var path = new TextBox { Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "USBProxy"), Location = new Point(16, 44), Width = 400 };
        var browse = new Button { Text = "瀏覽…", Location = new Point(424, 42), Width = 80 };
        var desktop = new CheckBox { Text = "在桌面建立捷徑", Checked = true, Location = new Point(16, 80), AutoSize = true };
        var bar = new ProgressBar { Location = new Point(16, 116), Width = 488, Height = 18 };
        var status = new Label { Text = "需要約 3 GB 空間（含 Linux 虛擬機）。不需要系統管理員權限。", Location = new Point(16, 142), AutoSize = true };
        var install = new Button { Text = "安裝", Location = new Point(424, 168), Width = 80 };
        form.Controls.AddRange(new Control[] { label, path, browse, desktop, bar, status, install });
        form.AcceptButton = install;
        browse.Click += (s, e) =>
        {
            using (var dialog = new FolderBrowserDialog { SelectedPath = path.Text })
                if (dialog.ShowDialog(form) == DialogResult.OK) path.Text = Path.Combine(dialog.SelectedPath, "USBProxy");
        };
        install.Click += (s, e) =>
        {
            string target = path.Text.Trim();
            bool shortcut = desktop.Checked;
            foreach (Control c in form.Controls) c.Enabled = c == bar || c == status;
            new Thread(() =>
            {
                try
                {
                    Extract(target, (done, total) => form.BeginInvoke((Action)(() => { bar.Maximum = total; bar.Value = done; status.Text = "正在解壓縮… " + done + " / " + total; })));
                    string exe = Path.Combine(target, "USB Proxy.exe");
                    Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "USB Proxy.lnk"), exe);
                    if (shortcut) Shortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "USB Proxy.lnk"), exe);
                    form.BeginInvoke((Action)(() =>
                    {
                        MessageBox.Show(form, "安裝完成。接下來會開啟 USB Proxy，第一次開啟會詢問是否安裝 VirtualBox 與 Linux（約 3～5 分鐘，請在系統管理員確認時按「是」）。", "USB Proxy");
                        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = target });
                        form.Close();
                    }));
                }
                catch (Exception ex)
                {
                    form.BeginInvoke((Action)(() =>
                    {
                        MessageBox.Show(form, "安裝失敗：" + ex.Message, "USB Proxy");
                        foreach (Control c in form.Controls) c.Enabled = true;
                    }));
                }
            }) { IsBackground = true }.Start();
        };
        Application.Run(form);
    }

    static void Extract(string target, Action<int, int> progress)
    {
        string zip = Path.Combine(Path.GetTempPath(), "usbproxy-setup-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var self = File.OpenRead(Application.ExecutablePath))
            {
                var tail = new byte[8];
                self.Seek(-8, SeekOrigin.End); self.Read(tail, 0, 8);
                long length = BitConverter.ToInt64(tail, 0);
                if (length <= 0 || length > self.Length - 8) throw new InvalidDataException("安裝檔不完整，請重新下載。");
                self.Seek(self.Length - 8 - length, SeekOrigin.Begin);
                using (var output = File.Create(zip)) Copy(self, output, length);
            }
            Directory.CreateDirectory(target);
            string exe = Path.Combine(target, "USB Proxy.exe");
            if (File.Exists(exe))   // upgrading over a running copy: Windows allows renaming a running exe
            {
                try { File.Delete(exe + ".old"); } catch { }
                File.Move(exe, exe + ".old");
            }
            using (var archive = ZipFile.OpenRead(zip))
            {
                var files = archive.Entries.Where(en => en.Name != "").ToList();
                int done = 0;
                foreach (var entry in files)
                {
                    string rel = entry.FullName.Replace('/', '\\');
                    string dest = Path.GetFullPath(Path.Combine(target, rel));
                    if (!dest.StartsWith(Path.GetFullPath(target) + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!(keep.Contains(rel.Split('\\')[0], StringComparer.OrdinalIgnoreCase) && File.Exists(dest)))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dest));
                        entry.ExtractToFile(dest, true);
                    }
                    progress(++done, files.Count);
                }
                foreach (var entry in archive.Entries.Where(en => en.Name == ""))
                    Directory.CreateDirectory(Path.Combine(target, entry.FullName.Replace('/', '\\')));
            }
        }
        finally { try { File.Delete(zip); } catch { } }
    }

    static void Copy(Stream input, Stream output, long count)
    {
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (n <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, n); count -= n;
        }
    }

    static void Shortcut(string link, string exe)
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic lnk = shell.CreateShortcut(link);
        lnk.TargetPath = exe; lnk.WorkingDirectory = Path.GetDirectoryName(exe); lnk.IconLocation = exe + ",0";
        lnk.Save();
    }
}
