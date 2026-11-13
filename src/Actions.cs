// USB Proxy window, part 2: actions, drag and drop, menus, background jobs.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualBasic.FileIO;
using UsbProxyUi;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace UsbProxy
{
    partial class MainUi
    {
        // ---------------- Actions ----------------
        // First run: install VirtualBox (one administrator prompt), build Linux, then start it.
        void StartSetup()
        {
            if (busy) return;
            string text = T("第一次使用需要安裝 VirtualBox 並建立 Linux 環境，約需 3～5 分鐘，不需要網路。") + "\n\n" + T("安裝 VirtualBox 時 Windows 會詢問系統管理員權限，請按「是」。要現在安裝嗎？");
            if (MessageBox.Show(text, T("安裝 USB Proxy"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK) BeginOperation(Req("action", "setup"));
        }
        void SwitchToLinux(FileEntry entry)
        {
            if (busy || entry == null) return;
            if (vmState == "notinstalled") { StartSetup(); return; }
            string text = string.Format(T("要將「{0}」切換到 Linux 嗎？"), entry.Name) + "\n\n" + T("切換後 Windows 會暫時無法使用這個隨身碟，請先關閉其中開啟的檔案。在右側按右鍵「退出」即可交還 Windows。");
            if (vmState != "running") text += "\n\n" + T("Linux 尚未啟動，會先自動啟動。");
            if (entry.Kind == "usb-drive")
            {
                var choice = MessageBox.Show(text + "\n\n" + T("是：切換到 Linux　否：在 Windows 中開啟"), T("切換到 Linux"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (choice == DialogResult.No) { Navigate(win, Loc.WinFolder(entry.Path)); return; }
                if (choice != DialogResult.Yes) return;
            }
            else if (MessageBox.Show(text, T("切換到 Linux"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
            // Leave the drive's folders in the Windows pane before Windows loses the drive.
            if (win.Location.Kind == "folder" && !string.IsNullOrEmpty(entry.Path) && win.Location.Path.StartsWith(entry.Path, StringComparison.OrdinalIgnoreCase)) Navigate(win, Loc.Pc());
            switchTarget = J.Obj("uuid", entry.Uuid, "name", entry.Device);
            if (vmState == "running" && ready) BeginOperation(Req("action", "attach", "uuid", entry.Uuid, "name", entry.Device));
            else BeginOperation(Req("action", "start"));
        }
        void EjectDrive(string uuid)
        {
            if (busy || string.IsNullOrEmpty(uuid)) return;
            autoSkip.Add(uuid);   // handed back on purpose: don't grab it again until replugged
            BeginOperation(Req("action", "eject", "uuid", uuid));
        }
        void OpenEntry(Pane pane)
        {
            var selected = Selected(pane);
            if (selected.Count != 1) return;
            var entry = selected[0];
            switch (entry.Kind)
            {
                case "device": case "usb-drive": SwitchToLinux(entry); break;
                case "drive": case "special": Navigate(win, Loc.WinFolder(entry.Path)); break;
                case "volume": Navigate(lin, Loc.LinFolder(entry.Device, "")); break;
                case "linux-device": MessageBox.Show(T("這個隨身碟沒有支援的分割區（FAT32、exFAT、NTFS、ext4）。"), "USB Proxy"); break;
                case "link": MessageBox.Show(T("不支援開啟符號連結。"), "USB Proxy"); break;
                case "folder":
                    if (pane == win) Navigate(win, Loc.WinFolder(entry.Path)); else Navigate(lin, Loc.LinFolder(entry.Device, entry.Path));
                    break;
                default:
                    if (pane == win) { try { Process.Start(entry.Path); } catch { MessageBox.Show(T("沒有可開啟這個檔案的程式。"), "USB Proxy"); } }
                    else if (!busy) BeginOperation(Req("action", "fetch", "device", entry.Device, "path", entry.Path, "size", entry.Size, "name", entry.Name));
                    break;
            }
        }
        void SaveLinuxFile()
        {
            var selected = Selected(lin);
            if (busy || selected.Count != 1 || selected[0].Kind != "file") return;
            using (var dialog = new SaveFileDialog { FileName = selected[0].Name, OverwritePrompt = true })
                if (dialog.ShowDialog() == DialogResult.OK)
                    BeginOperation(Req("action", "fetch", "device", selected[0].Device, "path", selected[0].Path, "size", selected[0].Size, "name", selected[0].Name, "saveTo", dialog.FileName));
        }
        void RemoveLinuxItems()
        {
            var selected = Selected(lin).Where(e => In(e.Kind, "file", "folder", "link")).ToList();
            if (busy || selected.Count == 0) return;
            string what = selected.Count == 1 ? "「" + selected[0].Name + "」" : string.Format(T("這 {0} 個項目"), selected.Count);
            if (MessageBox.Show(string.Format(T("確定要刪除{0}嗎？"), what), T("刪除"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "delete", "device", lin.Location.Device, "paths", selected.Select(e => e.Path).ToArray()));
        }
        void RenameLinuxItem()
        {
            if (busy || lin.List.SelectedItems.Count != 1 || lin.Location.Kind != "folder") return;
            lin.List.SelectedItems[0].BeginEdit();
        }
        void NewLinuxFolder()
        {
            if (busy || lin.Location.Kind != "folder") return;
            var names = lin.List.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();
            string name = T("新增資料夾"); int n = 2;
            while (names.Contains(name)) { name = string.Format(T("新增資料夾 ({0})"), n); n++; }
            editAfter = name;
            followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "mkdir", "device", lin.Location.Device, "path", lin.Location.Path, "name", name));
        }

        void NewLinuxTextFile()
        {
            if (busy || lin.Location.Kind != "folder") return;
            var names = lin.List.Items.Cast<ListViewItem>().Select(i => i.Text).ToList();
            string name = T("新增文字文件") + ".txt"; int n = 2;
            while (names.Contains(name)) { name = string.Format(T("新增文字文件 ({0})"), n) + ".txt"; n++; }
            string folder = Path.Combine(Core.Runtime, "new");   // the previous empty file has been copied by now
            try { Directory.Delete(folder, true); } catch { }
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, name);
            File.WriteAllBytes(file, new byte[0]);
            editAfter = name;
            CopyToLinux(new[] { file }, lin.Location.Device, lin.Location.Path);
        }

        // Copies in every direction: Windows -> Linux (copy), Linux -> Linux (transfer), Linux -> Windows (download).
        void CopyToLinux(IEnumerable<string> paths, string device, string folder, bool move = false)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (busy || string.IsNullOrEmpty(device) || list.Count == 0) return;
            List<string> names, replace;
            if (!PlanNames(list, WinInfo, n => LinuxExisting(device, folder, n), false, Path.GetDirectoryName(list[0]), LinPlace(device, folder), out names, out replace) || list.Count == 0) return;
            if (lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "copy", "device", device, "dest", folder, "paths", list.ToArray(), "names", names.ToArray(), "replace", replace.ToArray(), "move", move));
        }
        void TransferInLinux(Clip clip, string device, string folder)
        {
            if (busy || clip == null || string.IsNullOrEmpty(device)) return;
            // Pasted into their own folder: copies named "name - 複製" as in Explorer; a cut there does nothing.
            bool sameFolder = clip.Device == device && clip.Paths.All(p => ParentPath(p) == folder);
            if (sameFolder && clip.Cut) return;
            var list = clip.Paths.ToList();
            List<string> names, replace;
            if (!PlanNames(list, p => EntryInfo(clip.Entries == null ? null : clip.Entries.FirstOrDefault(e => e.Path == p)),
                n => LinuxExisting(device, folder, n), sameFolder, LinPlace(clip.Device, ParentPath(list[0])), LinPlace(device, folder), out names, out replace) || list.Count == 0) return;
            if (lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "transfer", "source", clip.Device, "paths", list.ToArray(), "names", names.ToArray(), "replace", replace.ToArray(),
                "device", device, "dest", folder, "move", clip.Cut));
        }
        void DownloadToWindows(Clip clip, string folder)
        {
            if (busy || clip == null || string.IsNullOrEmpty(folder)) return;
            var list = clip.Paths.ToList();
            List<string> names, replace;
            if (!PlanNames(list, p => EntryInfo(clip.Entries == null ? null : clip.Entries.FirstOrDefault(e => e.Path == p)),
                n => { try { return WinInfo(Path.Combine(folder, n)); } catch { return null; } }, false, LinPlace(clip.Device, ParentPath(list[0])), folder, out names, out replace) || list.Count == 0) return;
            followUp = new FollowUp { RefreshWin = true };
            BeginOperation(Req("action", "download", "device", clip.Device, "paths", list.ToArray(), "names", names.ToArray(), "replace", replace.ToArray(),
                "destination", folder, "move", clip.Cut));
        }
        // ---------------- Name conflicts, as in Explorer ----------------
        class ItemInfo { public string Name, Place = ""; public bool Folder; public long Size; public DateTime Modified; }
        string LinPlace(string device, string folder) { return "Linux  ›  " + VolumeLabel(device) + string.Concat(folder.Split('/').Where(x => x != "").Select(x => "  ›  " + x)); }
        static string Leaf(string path) { return path.TrimEnd('/', '\\').Split('/', '\\').Last(); }
        static ItemInfo WinInfo(string path)
        {
            try
            {
                if (Directory.Exists(path)) { var d = new DirectoryInfo(path); return new ItemInfo { Name = d.Name, Folder = true, Modified = d.LastWriteTime }; }
                if (File.Exists(path)) { var f = new FileInfo(path); return new ItemInfo { Name = f.Name, Size = f.Length, Modified = f.LastWriteTime }; }
            }
            catch { }
            return null;
        }
        static ItemInfo EntryInfo(FileEntry e) { return e == null ? null : new ItemInfo { Name = e.Name, Folder = e.Kind == "folder", Size = e.Size, Modified = e.Modified }; }
        // What is already in a Linux folder is known from its listing when that folder is the one on screen.
        ItemInfo LinuxExisting(string device, string folder, string name)
        {
            if (lin.Location.Kind != "folder" || lin.Location.Device != device || lin.Location.Path != folder) return null;
            return EntryInfo(lin.List.Items.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag)
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)));
        }
        // Decides each item's destination name. Same folder: "name - 複製". A name that exists: ask to replace,
        // keep both ("name (2)") or skip. False when the user cancels; skipped items are removed from paths.
        bool PlanNames(List<string> paths, Func<string, ItemInfo> source, Func<string, ItemInfo> existing, bool sameFolder, string sourcePlace, string destPlace, out List<string> names, out List<string> replace)
        {
            var chosen = new List<string>(); var replaced = new List<string>(); names = chosen; replace = replaced;
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Func<string, bool> free = n => !taken.Contains(n) && existing(n) == null;
            int left = sameFolder ? 0 : paths.Count(p => existing(Leaf(p)) != null);
            string always = null; var keep = new List<string>();
            foreach (string p in paths)
            {
                string leaf = Leaf(p), name = leaf;
                var src = source(p) ?? new ItemInfo { Name = leaf };
                var dst = sameFolder ? null : existing(leaf);
                if (sameFolder) name = NextName(leaf, src.Folder, T(" - 複製"), T(" - 複製 ({0})"), free);
                else if (dst != null)
                {
                    left--;
                    src.Place = sourcePlace; dst.Place = destPlace;
                    bool canReplace = src.Folder == dst.Folder;   // a file cannot replace a folder or the other way round
                    string choice = always;
                    if (choice == null)
                    {
                        bool all;
                        choice = AskConflict(src, dst, canReplace, left, out all);
                        if (choice == null) return false;
                        if (all) always = choice;
                    }
                    if (choice == "replace" && !canReplace) choice = "both";
                    if (choice == "skip") continue;
                    if (choice == "replace") replaced.Add(leaf);
                    else name = NextName(leaf, src.Folder, null, " ({0})", free);
                }
                keep.Add(p); chosen.Add(name); taken.Add(name);
            }
            paths.Clear(); paths.AddRange(keep);
            return true;
        }
        static string NextName(string name, bool folder, string first, string numbered, Func<string, bool> free)
        {
            int dot = folder ? -1 : name.LastIndexOf('.');
            string stem = dot > 0 ? name.Substring(0, dot) : name, ext = dot > 0 ? name.Substring(dot) : "";
            if (first != null && free(stem + first + ext)) return stem + first + ext;
            for (int n = 2; ; n++) { string candidate = stem + string.Format(numbered, n) + ext; if (free(candidate)) return candidate; }
        }
        string AskConflict(ItemInfo src, ItemInfo dst, bool canReplace, int left, out bool all)
        {
            string choice = null; CheckBox applyAll;
            using (var dialog = ConflictDialog(src, dst, canReplace, left, c => choice = c, out applyAll))
            {
                dialog.ShowDialog(form);
                all = applyAll.Checked;
            }
            return choice;
        }
        // Explorer-style comparison: what is being pasted on the left, what is already there on the right.
        Form ConflictDialog(ItemInfo src, ItemInfo dst, bool canReplace, int left, Action<string> choose, out CheckBox applyAll)
        {
            var dialog = NewDialog(T("取代或略過"), 760, 356);
            NewText(dialog, string.Format(dst.Folder ? T("目的地已經有名為「{0}」的資料夾") : T("目的地已經有名為「{0}」的檔案"), dst.Name), 24, 18, 712, 28, headlineFont);
            bool same = !src.Folder && !dst.Folder && src.Size == dst.Size && Math.Abs((src.Modified - dst.Modified).TotalSeconds) < 2;
            NewText(dialog, !canReplace ? T("檔案和資料夾不能互相取代，可以保留兩者或略過。")
                : same ? T("兩個檔案的大小和修改時間都相同，可能是同一個檔案。")
                : dst.Folder ? T("取代會把兩個資料夾合併，同名的檔案以要貼上的為準。") : T("比較兩個檔案，選擇要怎麼處理。"),
                24, 50, 712, 22, captionFont, same ? Theme.Accent : Theme.SecondaryLabel);
            FileCard(dialog, T("要貼上的"), src, dst, 24);
            FileCard(dialog, T("目的地現有的"), dst, src, 392);
            applyAll = new CheckBox { Text = string.Format(T("其餘 {0} 個衝突也這樣處理"), left), Location = new Point(24, 258), Size = new Size(712, 26),
                Visible = left > 0, BackColor = dialog.BackColor, ForeColor = Theme.Label };
            dialog.Controls.Add(applyAll);
            var replaceButton = NewButton(dialog, T("取代"), ButtonKind.Primary, 336, 302, 100, () => { choose("replace"); dialog.Close(); });
            replaceButton.Enabled = canReplace;
            NewButton(dialog, T("保留兩者"), ButtonKind.Secondary, 444, 302, 110, () => { choose("both"); dialog.Close(); });
            NewButton(dialog, T("略過"), ButtonKind.Secondary, 562, 302, 82, () => { choose("skip"); dialog.Close(); });
            NewButton(dialog, T("取消"), ButtonKind.Plain, 652, 302, 84, () => dialog.Close());
            dialog.KeyPreview = true;
            dialog.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) dialog.Close(); };
            return dialog;
        }
        void FileCard(Form dialog, string title, ItemInfo item, ItemInfo other, int x)
        {
            var card = new Card { Location = new Point(x, 86), Size = new Size(344, 160) };
            dialog.Controls.Add(card);
            int px = (int)(40 * scale);
            Image icon = null;
            try { icon = ShellIcons.ForFile(item.Name, item.Folder, px); } catch { }
            card.Controls.Add(new PictureBox { Image = icon, Location = new Point(14, 36), Size = new Size(40, 40), SizeMode = PictureBoxSizeMode.Zoom, BackColor = card.FillColor });
            NewText(card, item.Name, 64, 34, 266, 22);
            NewText(card, item.Place, 64, 58, 266, 20, captionFont, Theme.SecondaryLabel);
            int y = 88;
            Action<string, string, bool> row = (k, v, mark) =>
            {
                NewText(card, k, 14, y, 84, 20, captionFont, Theme.SecondaryLabel);
                NewText(card, v, 100, y, 232, 20, captionFont, mark ? Theme.Accent : Theme.Label);
                y += 22;
            };
            bool both = !item.Folder && !other.Folder;
            bool larger = both && item.Size > other.Size, newer = item.Modified > other.Modified.AddSeconds(2);
            // The card title carries the comparison ("newer · larger"); the matching rows are in the accent colour.
            var tags = new List<string>(); if (newer) tags.Add(T("較新")); if (larger) tags.Add(T("較大"));
            var head = NewText(card, title, 14, 10, 150, 20, captionFont, Theme.SecondaryLabel);
            if (tags.Count > 0) NewText(card, string.Join(" · ", tags), 166, 10, 164, 20, captionFont, Theme.Accent).TextAlign = ContentAlignment.TopRight;
            row(T("大小"), item.Folder ? T("資料夾") : FormatSize(item.Size) + " (" + item.Size.ToString("N0") + " " + T("位元組") + ")", larger);
            row(T("修改日期"), item.Modified.ToString("yyyy/MM/dd HH:mm:ss"), newer);
            string type = item.Folder ? T("檔案資料夾") : "";
            if (!item.Folder) try { type = ShellIcons.TypeName(item.Name, false); } catch { }
            row(T("類型"), type, false);
        }

        Clip LinuxSelection()
        {
            var selected = Selected(lin).Where(e => In(e.Kind, "file", "folder")).ToList();
            if (selected.Count == 0) return null;
            return new Clip { Device = selected[0].Device, Paths = selected.Select(e => e.Path).ToList(), Entries = selected };
        }
        List<string> WinSelectionPaths() { return Selected(win).Where(e => In(e.Kind, "file", "folder")).Select(e => e.Path).ToList(); }
        // Cut is a copy whose sources are deleted once the copy has succeeded (and been verified).
        void CopySelection(Pane pane, bool cut = false)
        {
            if (pane == lin)
            {
                linuxClip = LinuxSelection();
                if (linuxClip != null)
                {
                    linuxClip.Cut = cut && lin.Location.Kind == "folder";
                    // Also on the Windows clipboard, so Explorer and the desktop can paste it (fetched at paste time).
                    try { Directory.Delete(Path.Combine(Core.Runtime, "clip"), true); } catch { }
                    try { Clipboard.SetDataObject(new LinuxClipData(this, linuxClip), false); } catch { }
                }
            }
            else
            {
                var paths = WinSelectionPaths();
                if (paths.Count > 0)
                {
                    // The same clipboard format as Explorer, so cut/copy also pastes there and Explorer's pastes here.
                    var collection = new StringCollection(); collection.AddRange(paths.ToArray());
                    var data = new DataObject(); data.SetFileDropList(collection);
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
                    Clipboard.SetDataObject(data, true);
                    linuxClip = null;
                }
            }
            UpdateCounts();
        }
        static bool ClipboardIsCut()
        {
            try { var s = Clipboard.GetData("Preferred DropEffect") as MemoryStream; return s != null && (s.ReadByte() & 2) != 0; }
            catch { return false; }
        }
        void PasteInto(Pane pane)
        {
            if (busy) return;
            // Something copied elsewhere since (e.g. in Explorer) replaces the Linux selection, as on any clipboard.
            try { if (linuxClip != null && !Clipboard.ContainsData(LinuxClipData.Marker)) linuxClip = null; } catch { }
            if (pane == lin)
            {
                if (lin.Location.Kind != "folder") return;
                if (linuxClip != null) TransferInLinux(linuxClip, lin.Location.Device, lin.Location.Path);
                else if (Clipboard.ContainsFileDropList()) CopyToLinux(Clipboard.GetFileDropList().Cast<string>(), lin.Location.Device, lin.Location.Path, ClipboardIsCut());
            }
            else if (win.Location.Kind == "folder")
            {
                if (linuxClip != null) DownloadToWindows(linuxClip, win.Location.Path);
                else if (Clipboard.ContainsFileDropList()) PasteWindowsFiles(Clipboard.GetFileDropList().Cast<string>().ToList(), win.Location.Path, ClipboardIsCut());
            }
        }
        // Linux files on the Windows clipboard. Explorer asks for the file list only when it pastes; the files are
        // then downloaded (SHA-256 verified) into runtime\clip and handed over. A Linux cut pasted in Explorer copies.
        class LinuxClipData : DataObject
        {
            public const string Marker = "UsbProxyLinuxClip";
            readonly MainUi ui; readonly Clip clip; string[] files;
            public LinuxClipData(MainUi ui, Clip clip)
            {
                this.ui = ui; this.clip = clip;
                SetData(DataFormats.FileDrop, new string[0]);   // advertises the format; filled in GetData
                SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(5)));
                SetData(Marker, "1");
            }
            public override object GetData(string format, bool autoConvert)
            {
                if (format == DataFormats.FileDrop) return files ?? (files = ui.FetchClip(clip));
                return base.GetData(format, autoConvert);
            }
        }
        string[] FetchClip(Clip clip)
        {
            if (busy) { SetMessage(T("請等目前的工作完成後再貼上。")); return null; }
            string dir = Path.Combine(Core.Runtime, "clip", Core.NewId());
            Directory.CreateDirectory(dir);
            var request = Req("action", "download", "device", clip.Device, "paths", clip.Paths.ToArray(), "destination", dir);
            SetBusy(true); SetMessage(T("正在從 Linux 取出檔案…"));
            Map response = null;
            // Runs off the UI thread; the window keeps painting while Explorer waits for the files.
            var thread = new Thread(() => { try { response = Worker.Run(request); } catch (Exception e) { response = Req("ok", false, "error", e.Message); } }) { IsBackground = true };
            thread.Start();
            while (thread.IsAlive) { Application.DoEvents(); Thread.Sleep(30); }
            SetBusy(false);
            if (!J.B(response, "ok"))
            {
                LogLine(T("失敗：") + T("貼上") + " — " + J.S(response, "error"));
                SetMessage(T("操作失敗：") + J.S(response, "error"));
                return null;
            }
            SetMessage(T("已從 Linux 取出，交給 Windows 貼上"));
            return clip.Paths.Select(p => Path.Combine(dir, p.TrimEnd('/').Split('/').Last())).ToArray();
        }

        // Windows to Windows: the shell's own copy/move, with its progress and replace dialogs.
        void PasteWindowsFiles(List<string> paths, string folder, bool move)
        {
            foreach (string src in paths)
            {
                string name = Path.GetFileName(src.TrimEnd('\\'));
                string dest = Path.Combine(folder, name);
                bool sameFolder = string.Equals(Path.GetDirectoryName(src.TrimEnd('\\')), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
                if (sameFolder)
                {
                    if (move) continue;
                    string stem = Directory.Exists(src) ? name : Path.GetFileNameWithoutExtension(name), ext = Directory.Exists(src) ? "" : Path.GetExtension(name);
                    dest = Path.Combine(folder, stem + T(" - 複製") + ext);
                    for (int n = 2; File.Exists(dest) || Directory.Exists(dest); n++) dest = Path.Combine(folder, stem + string.Format(T(" - 複製 ({0})"), n) + ext);
                }
                try
                {
                    if (Directory.Exists(src)) { if (move) FileSystem.MoveDirectory(src, dest, UIOption.AllDialogs); else FileSystem.CopyDirectory(src, dest, UIOption.AllDialogs); }
                    else if (move) FileSystem.MoveFile(src, dest, UIOption.AllDialogs); else FileSystem.CopyFile(src, dest, UIOption.AllDialogs);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { MessageBox.Show(e.Message, "USB Proxy"); break; }
            }
            if (move) Clipboard.Clear();
            Navigate(win, win.Location, true);
        }
        // Windows-side sources of a finished cut; the Recycle Bin keeps them recoverable.
        static void RecycleWindows(IEnumerable<string> paths)
        {
            foreach (string p in paths)
                try
                {
                    if (Directory.Exists(p)) FileSystem.DeleteDirectory(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                    else if (File.Exists(p)) FileSystem.DeleteFile(p, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e) { MessageBox.Show(e.Message, "USB Proxy"); break; }
        }

        // ---------------- Windows side: new, rename, delete ----------------
        void NewWindowsItem(bool folder)
        {
            if (win.Location.Kind != "folder") return;
            string dir = win.Location.Path, ext = folder ? "" : ".txt";
            string name = (folder ? T("新增資料夾") : T("新增文字文件")) + ext;
            for (int n = 2; File.Exists(Path.Combine(dir, name)) || Directory.Exists(Path.Combine(dir, name)); n++)
                name = string.Format(folder ? T("新增資料夾 ({0})") : T("新增文字文件 ({0})"), n) + ext;
            try { if (folder) Directory.CreateDirectory(Path.Combine(dir, name)); else File.WriteAllBytes(Path.Combine(dir, name), new byte[0]); }
            catch (Exception e) { MessageBox.Show(T("無法建立：") + e.Message, "USB Proxy"); return; }
            Navigate(win, win.Location, true);
            EditItem(win, name);
        }
        static void EditItem(Pane pane, string name)
        {
            foreach (ListViewItem item in pane.List.Items)
                if (item.Text == name) { pane.List.SelectedItems.Clear(); item.Selected = true; item.Focused = true; item.EnsureVisible(); pane.List.Focus(); item.BeginEdit(); return; }
        }
        void RenameWindowsItem()
        {
            if (win.List.SelectedItems.Count != 1 || win.Location.Kind != "folder") return;
            win.List.SelectedItems[0].BeginEdit();
        }
        void RemoveWindowsItems()
        {
            var paths = WinSelectionPaths();
            if (paths.Count == 0 || win.Location.Kind != "folder") return;
            string what = paths.Count == 1 ? "「" + Path.GetFileName(paths[0]) + "」" : string.Format(T("這 {0} 個項目"), paths.Count);
            if (MessageBox.Show(string.Format(T("確定要將{0}移到資源回收筒嗎？"), what), T("刪除"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            RecycleWindows(paths);
            Navigate(win, win.Location, true);
        }
        // Sends the selection to the folder open in the other pane.
        void SendToOtherPane(Pane pane)
        {
            if (pane == win)
            {
                if (lin.Location.Kind != "folder") { MessageBox.Show(T("請先在右側開啟隨身碟上的資料夾。"), "USB Proxy"); return; }
                CopyToLinux(WinSelectionPaths(), lin.Location.Device, lin.Location.Path);
            }
            else
            {
                if (win.Location.Kind != "folder") { MessageBox.Show(T("請先在左側開啟電腦上的資料夾。"), "USB Proxy"); return; }
                DownloadToWindows(LinuxSelection(), win.Location.Path);
            }
        }

        // Files opened from the USB are edited in a local copy; saved changes are offered back to the volume.
        void WatchOpenFiles()
        {
            if (busy || prompting || openFiles.Count == 0) return;
            if ((DateTime.Now - lastOpenCheck).TotalSeconds < 1.5) return;
            lastOpenCheck = DateTime.Now;
            foreach (var file in openFiles.ToArray())
            {
                if (!File.Exists(file.Local)) continue;
                DateTime stamp = File.GetLastWriteTimeUtc(file.Local);
                if (stamp == file.Stamp) { file.Seen = null; continue; }
                // Wait until the timestamp is stable so a save in progress is not uploaded half-written.
                if (file.Seen != stamp) { file.Seen = stamp; continue; }
                file.Stamp = stamp; file.Seen = null;
                if (!volumes.Any(v => J.S(v, "path") == file.Device)) continue;
                DialogResult choice;
                prompting = true;
                try { choice = MessageBox.Show(string.Format(T("「{0}」已在電腦上修改。要將變更寫回隨身碟嗎？隨身碟上的原檔案會被取代。"), file.Name), T("寫回隨身碟"), MessageBoxButtons.YesNo, MessageBoxIcon.Question); }
                finally { prompting = false; }
                if (choice == DialogResult.Yes)
                {
                    if (lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
                    BeginOperation(Req("action", "copy", "device", file.Device, "dest", ParentPath(file.Remote), "paths", new[] { file.Local }, "overwrite", true, "targetName", file.Name));
                }
                return;
            }
        }

        void SetBusy(bool value)
        {
            busy = value;
            if (!value) { bootProgress.Visible = false; transfer.Visible = false; transferGap.Visible = false; lin.List.Cursor = Cursors.Default; }
            else if (In(pendingAction, "list", "status", "delete", "rename", "mkdir", "eject", "attach")) lin.List.Cursor = Cursors.AppStarting;
            UpdateState();
        }
        void UpdateGuest(Map guest)
        {
            volumes = new List<Map>();
            if (guest != null)
            {
                ready = J.B(guest, "ready");
                volumes = J.Maps(guest, "volumes");
            }
        }

        // ---------------- Drag and drop between the panes and from Explorer ----------------
        Map DropTarget(Pane pane, DragEventArgs e)
        {
            var point = pane.List.PointToClient(new Point(e.X, e.Y));
            var item = pane.List.GetItemAt(point.X, point.Y);
            var entry = item != null ? (FileEntry)item.Tag : null;
            if (pane == win)
            {
                if (entry != null && In(entry.Kind, "folder", "drive", "special")) return J.Obj("path", entry.Path);
                if (win.Location.Kind == "folder") return J.Obj("path", win.Location.Path);
                return null;
            }
            if (entry != null && entry.Kind == "folder") return J.Obj("device", entry.Device, "path", entry.Path);
            if (entry != null && entry.Kind == "volume") return J.Obj("device", entry.Device, "path", "");
            if (lin.Location.Kind == "folder") return J.Obj("device", lin.Location.Device, "path", lin.Location.Path);
            return null;
        }
        void BuildDragDrop()
        {
            foreach (var pane in panes)
            {
                var p = pane;
                DragEventHandler over = (s, e) =>
                {
                    var effect = DragDropEffects.None;
                    if (!busy && DropTarget(p, e) != null)
                    {
                        bool fromLinux = e.Data.GetDataPresent(linuxFormat);
                        if (p == lin && (fromLinux || e.Data.GetDataPresent(DataFormats.FileDrop))) effect = DragDropEffects.Copy;
                        if (p == win && fromLinux) effect = DragDropEffects.Copy;
                    }
                    e.Effect = effect;
                };
                DragEventHandler drop = (s, e) =>
                {
                    var target = DropTarget(p, e);
                    if (target == null) return;
                    if (e.Data.GetDataPresent(linuxFormat))
                    {
                        var data = J.Read((string)e.Data.GetData(linuxFormat));
                        var clip = new Clip { Device = J.S(data, "device"), Paths = J.Strings(data, "paths") };
                        if (p == lin) TransferInLinux(clip, J.S(target, "device"), J.S(target, "path"));
                        else DownloadToWindows(clip, J.S(target, "path"));
                    }
                    else if (p == lin) CopyToLinux((string[])e.Data.GetData(DataFormats.FileDrop), J.S(target, "device"), J.S(target, "path"));
                };
                foreach (Control c in new Control[] { p.List, p.Empty })
                {
                    c.AllowDrop = true;
                    c.DragEnter += over; c.DragOver += over; c.DragDrop += drop;
                }
            }
            win.List.ItemDrag += (s, e) =>
            {
                var paths = WinSelectionPaths();
                if (paths.Count == 0) return;
                var data = new DataObject();
                var collection = new StringCollection(); collection.AddRange(paths.ToArray());
                data.SetFileDropList(collection);
                win.List.DoDragDrop(data, DragDropEffects.Copy);
            };
            lin.List.ItemDrag += (s, e) =>
            {
                var clip = LinuxSelection();
                if (clip == null) return;
                var data = new DataObject();
                data.SetData(linuxFormat, J.Write(J.Obj("device", clip.Device, "paths", clip.Paths.ToArray())));
                lin.List.DoDragDrop(data, DragDropEffects.Copy);
            };
        }

        // ---------------- Pane events: activation, keys, zoom, rename ----------------
        void SetZoom(Pane pane, int step)
        {
            step = Math.Max(0, Math.Min(zoomSizes.Length - 1, step));
            ui["zoom_" + pane.Key] = step;
            var list = pane.List;
            if (step >= 2)
            {
                ui["icon_" + pane.Key] = step;
                // Image lists hold at most 256 px images.
                int px = Math.Min(256, (int)(zoomSizes[step] * scale));
                if (pane.Large.ImageSize.Width != px)
                {
                    list.BeginUpdate();
                    pane.Large.Images.Clear();
                    pane.Large.ImageSize = new Size(px, px);
                    foreach (ListViewItem item in list.Items) IconKey((FileEntry)item.Tag, pane);
                    list.LargeImageList = null; list.LargeImageList = pane.Large;
                    list.EndUpdate();
                }
                list.SetIconSpacing((int)(px + 44 * scale), (int)(px + 48 * scale));
                list.View = View.LargeIcon;
            }
            else if (step == 1) list.View = View.List;
            else list.View = View.Details;
            pane.Settling = true;
            pane.View.SelectedIndex = step >= 2 ? 0 : step == 1 ? 1 : 2;
            pane.Settling = false;
        }
        void BuildPaneEvents()
        {
            foreach (var pane in panes)
            {
                var p = pane;
                p.View.SelectedIndexChanged += (s, e) =>
                {
                    if (p.Settling) return;
                    switch (p.View.SelectedIndex) { case 0: SetZoom(p, UiI("icon_" + p.Key)); break; case 1: SetZoom(p, 1); break; default: SetZoom(p, 0); break; }
                };
                p.List.CtrlWheel += (s, e) => SetZoom(p, UiI("zoom_" + p.Key) + Math.Sign(e.Delta));
                p.List.HandleCreated += (s, e) => SetZoom(p, UiI("zoom_" + p.Key));
                p.List.Enter += (s, e) => { active = p; UpdateCounts(); };
                p.List.ItemActivate += (s, e) => OpenEntry(p);
                p.List.SelectedIndexChanged += (s, e) => { active = p; UpdateCounts(); };
                p.List.KeyDown += (s, e) =>
                {
                    switch (e.KeyData)
                    {
                        case Keys.Alt | Keys.Left: case Keys.Back: case Keys.BrowserBack: GoBack(p); break;
                        case Keys.Alt | Keys.Right: case Keys.BrowserForward: GoForward(p); break;
                        case Keys.Alt | Keys.Up: GoUp(p); break;
                        case Keys.Control | Keys.A: foreach (ListViewItem item in p.List.Items) item.Selected = true; break;
                        case Keys.Control | Keys.C: CopySelection(p); break;
                        case Keys.Control | Keys.X: CopySelection(p, true); break;
                        case Keys.Control | Keys.V: PasteInto(p); break;
                        case Keys.F5: case Keys.Control | Keys.R: case Keys.BrowserRefresh: RefreshPane(p); break;
                        case Keys.Delete: if (p == lin) RemoveLinuxItems(); else RemoveWindowsItems(); break;
                        case Keys.F2: if (p == lin) RenameLinuxItem(); else RenameWindowsItem(); break;
                        case Keys.Control | Keys.Shift | Keys.N: if (p == lin) NewLinuxFolder(); else NewWindowsItem(true); break;
                        case Keys.Alt | Keys.Enter: if (p == lin) ShowLinuxInfo(); break;
                        default: return;
                    }
                    e.Handled = e.SuppressKeyPress = true;
                };
                // Mouse side buttons go back and forward, as in Explorer.
                MouseEventHandler sideButtons = (s, e) =>
                {
                    if (e.Button == MouseButtons.XButton1) GoBack(p);
                    else if (e.Button == MouseButtons.XButton2) GoForward(p);
                };
                p.List.MouseDown += sideButtons; p.Empty.MouseDown += sideButtons;
                // An empty folder shows a label over the list; clicking it keeps the keyboard shortcuts working.
                p.Empty.MouseDown += (s, e) => p.List.Focus();
                SetZoom(p, UiI("zoom_" + p.Key));
            }
            win.List.LabelEdit = true;
            win.List.BeforeLabelEdit += (s, e) => { if (win.Location.Kind != "folder" || !In(((FileEntry)win.List.Items[e.Item].Tag).Kind, "file", "folder")) e.CancelEdit = true; };
            win.List.AfterLabelEdit += (s, e) =>
            {
                var entry = (FileEntry)win.List.Items[e.Item].Tag;
                string name = e.Label != null ? e.Label.Trim() : "";
                e.CancelEdit = true;   // the list is re-read after the rename
                if (name == "" || name == entry.Name) return;
                try { if (entry.Kind == "folder") FileSystem.RenameDirectory(entry.Path, name); else FileSystem.RenameFile(entry.Path, name); }
                catch (Exception ex) { MessageBox.Show(T("無法重新命名：") + ex.Message, "USB Proxy"); return; }
                form.BeginInvoke((Action)(() =>
                {
                    Navigate(win, win.Location, true);
                    foreach (ListViewItem item in win.List.Items) if (item.Text == name) { item.Selected = true; item.Focused = true; item.EnsureVisible(); }
                }));
            };
            lin.List.LabelEdit = true;
            lin.List.BeforeLabelEdit += (s, e) => { if (busy || lin.Location.Kind != "folder" || ((FileEntry)lin.List.Items[e.Item].Tag).IsLink) e.CancelEdit = true; };
            lin.List.AfterLabelEdit += (s, e) =>
            {
                var entry = (FileEntry)lin.List.Items[e.Item].Tag;
                string name = e.Label != null ? e.Label.Trim() : "";
                // The list keeps the old name until the rename succeeds and the folder is re-read.
                e.CancelEdit = true;
                if (name == "" || name == entry.Name || busy) return;
                followUp = FollowUp.NavTo(lin.Location);
                BeginOperation(Req("action", "rename", "device", entry.Device, "path", entry.Path, "name", name));
            };
        }

        // ---------------- Context menus ----------------
        ContextMenuStrip NewMenu() { return new ContextMenuStrip { Renderer = new MenuRenderer(), Font = bodyFont }; }
        ToolStripMenuItem AddMenuItem(ContextMenuStrip menu, string text, Action handler, string keys = "")
        {
            var item = new ToolStripMenuItem(text);
            if (keys != "") item.ShortcutKeyDisplayString = keys;
            item.Click += (s, e) => handler();
            menu.Items.Add(item);
            return item;
        }
        ToolStripMenuItem NewSubmenu(ContextMenuStrip menu, Action folder, Action text)
        {
            var item = new ToolStripMenuItem(T("新增"));
            item.DropDown.Renderer = menu.Renderer; item.DropDown.Font = menu.Font;
            var f = new ToolStripMenuItem(T("資料夾")) { ShortcutKeyDisplayString = "Ctrl+Shift+N" };
            f.Click += (s, e) => folder();
            var t = new ToolStripMenuItem(T("文字文件"));
            t.Click += (s, e) => text();
            item.DropDownItems.Add(f); item.DropDownItems.Add(t);
            menu.Items.Add(item);
            return item;
        }
        string LocationDrive()
        {
            if (lin.Location.Kind != "folder") return "";
            var v = volumes.FirstOrDefault(x => J.S(x, "path") == lin.Location.Device);
            var d = v != null ? DriveOf(v) : null;
            return d != null ? J.S(d, "uuid") : "";
        }
        void BuildMenus()
        {
            winMenu = NewMenu();
            var wOpen = AddMenuItem(winMenu, T("開啟"), () => OpenEntry(win), "Enter");
            var wSwitch = AddMenuItem(winMenu, T("切換到 Linux"), () => SwitchToLinux(Selected(win).FirstOrDefault()));
            var wSend = AddMenuItem(winMenu, T("複製到右側 Linux 資料夾"), () => SendToOtherPane(win));
            var wCut = AddMenuItem(winMenu, T("剪下"), () => CopySelection(win, true), "Ctrl+X");
            var wCopy = AddMenuItem(winMenu, T("複製"), () => CopySelection(win), "Ctrl+C");
            var wPaste = AddMenuItem(winMenu, T("貼上"), () => PasteInto(win), "Ctrl+V");
            var wRename = AddMenuItem(winMenu, T("重新命名"), RenameWindowsItem, "F2");
            var wDelete = AddMenuItem(winMenu, T("刪除"), RemoveWindowsItems, "Del");
            var wShow = AddMenuItem(winMenu, T("在檔案總管中顯示"), () => { var e = Selected(win).FirstOrDefault(); if (e != null && !string.IsNullOrEmpty(e.Path)) Process.Start("explorer.exe", "/select,\"" + e.Path + "\""); });
            winMenu.Items.Add(new ToolStripSeparator());
            var wNew = NewSubmenu(winMenu, () => NewWindowsItem(true), () => NewWindowsItem(false));
            AddMenuItem(winMenu, T("重新整理"), () => RefreshPane(win), "F5");
            winMenu.Opening += (s, e) =>
            {
                var selected = Selected(win); string kind = selected.Count == 1 ? selected[0].Kind : "";
                int files = selected.Count(x => In(x.Kind, "file", "folder"));
                wOpen.Visible = selected.Count == 1 && kind != "device";
                wSwitch.Visible = In(kind, "device", "usb-drive"); wSwitch.Enabled = !busy;
                wSend.Visible = files > 0; wSend.Enabled = !busy;
                bool inFolder = win.Location.Kind == "folder";
                wCopy.Visible = files > 0; wCut.Visible = inFolder && files > 0;
                wPaste.Visible = inFolder; wPaste.Enabled = !busy && (linuxClip != null || Clipboard.ContainsFileDropList());
                wRename.Visible = inFolder && files == 1 && selected.Count == 1; wDelete.Visible = inFolder && files > 0;
                wNew.Visible = inFolder && selected.Count == 0;
                wShow.Visible = selected.Count == 1 && In(kind, "file", "folder");
            };
            win.List.ContextMenuStrip = winMenu; win.Empty.ContextMenuStrip = winMenu;

            linMenu = NewMenu();
            var lOpen = AddMenuItem(linMenu, T("開啟"), () => OpenEntry(lin), "Enter");
            var lSave = AddMenuItem(linMenu, T("存到電腦…"), SaveLinuxFile);
            var lSend = AddMenuItem(linMenu, T("複製到左側 Windows 資料夾"), () => SendToOtherPane(lin));
            var lCut = AddMenuItem(linMenu, T("剪下"), () => CopySelection(lin, true), "Ctrl+X");
            var lCopy = AddMenuItem(linMenu, T("複製"), () => CopySelection(lin), "Ctrl+C");
            var lPaste = AddMenuItem(linMenu, T("貼上"), () => PasteInto(lin), "Ctrl+V");
            var lRename = AddMenuItem(linMenu, T("重新命名"), RenameLinuxItem, "F2");
            var lDelete = AddMenuItem(linMenu, T("刪除"), RemoveLinuxItems, "Del");
            var lEject = AddMenuItem(linMenu, T("退出（交還 Windows）"), () =>
            {
                var e = Selected(lin).FirstOrDefault();
                EjectDrive(e != null && !string.IsNullOrEmpty(e.Uuid) ? e.Uuid : LocationDrive());
            });
            var lFormat = AddMenuItem(linMenu, T("格式化…"), () => { string d = MenuVolume(); if (d != null) ShowFormat(d); });
            linMenu.Items.Add(new ToolStripSeparator());
            var lNew = NewSubmenu(linMenu, NewLinuxFolder, NewLinuxTextFile);
            var lRefresh = AddMenuItem(linMenu, T("重新整理"), () => RefreshPane(lin), "F5");
            var lInfoGap = new ToolStripSeparator();
            linMenu.Items.Add(lInfoGap);
            var lInfo = AddMenuItem(linMenu, T("內容"), ShowLinuxInfo, "Alt+Enter");
            linMenu.Opening += (s, e) =>
            {
                var selected = Selected(lin); bool idle = !busy;
                bool one = selected.Count == 1; string kind = one ? selected[0].Kind : "";
                bool inFolder = lin.Location.Kind == "folder";
                int files = selected.Count(x => In(x.Kind, "file", "folder"));
                lOpen.Visible = one;
                lSave.Visible = inFolder && kind == "file";
                lSend.Visible = files > 0; lCopy.Visible = files > 0; lCut.Visible = inFolder && files > 0; lCut.Enabled = idle;
                lPaste.Visible = inFolder; lPaste.Enabled = idle && (linuxClip != null || Clipboard.ContainsFileDropList());
                lRename.Visible = inFolder && one; lDelete.Visible = inFolder && selected.Count > 0;
                lEject.Visible = In(kind, "volume", "linux-device") || (inFolder && selected.Count == 0);
                lNew.Visible = inFolder && selected.Count == 0;
                lInfo.Visible = lInfoGap.Visible = MenuVolume() != null;
                lFormat.Visible = kind == "volume";   // like Explorer: only on the drive itself, not inside its folders
                foreach (var item in new[] { lOpen, lSave, lSend, lRename, lDelete, lEject, lNew, lRefresh, lInfo, lFormat }) item.Enabled = idle;
            };
            lin.List.ContextMenuStrip = linMenu; lin.Empty.ContextMenuStrip = linMenu;
        }

        // ---------------- Background jobs ----------------
        void BeginOperation(Map request)
        {
            if (busy || updating) return;
            string action = J.S(request, "action");
            if (action == "start") { request["cpus"] = UiI("cpus"); request["memory"] = UiI("memory"); }
            if (action == "status" || action == "attach") request["remember"] = UiB("autoLinux");
            try
            {
                pendingAction = action;
                pendingRequest = request;
                Worker.Progress = null;
                var started = new Job();
                job = started;
                // Background thread: closing the window must not wait for a status check or a boot.
                new Thread(() =>
                {
                    started.Response = Worker.Run(request); started.Done = true;
                    try { form.BeginInvoke((Action)Tick); } catch { }   // the timer would pick it up anyway
                }) { IsBackground = true }.Start();
                opStarted = DateTime.Now;
                if (In(action, "start", "setup"))
                {
                    bootStarted = DateTime.Now; bootPhase = "";
                    bootEstimate = action == "setup" ? 75 : BootEstimate();
                    bootProgress.Value = 0; bootProgress.Visible = true;
                }
                if (In(action, "copy", "transfer", "download") || (action == "fetch" && J.L(request, "size") >= 5L << 20)) StartTransferView(request);
                SetBusy(true);
                if (!quietActions.Contains(action))
                {
                    SetMessage(actionNames[action] + "…");
                    LogLine(T("開始：") + actionNames[action]);
                }
            }
            catch (Exception e) { LogLine(e.Message); followUp = null; SetBusy(false); }
        }

        void StartTransferView(Map request)
        {
            samples.Clear();
            int count = J.A(request, "paths").Count;
            switch (J.S(request, "action"))
            {
                case "fetch":
                    transferTitle.Text = string.Format(J.S(request, "saveTo") != "" ? T("正在存到電腦：{0}") : T("正在開啟：{0}"), J.S(request, "name")); break;
                case "transfer":
                    transferTitle.Text = string.Format(T("正在傳輸 {0} 個項目：{1} → {2}"), count, VolumeLabel(J.S(request, "source")), VolumeLabel(J.S(request, "device"))); break;
                case "download":
                    transferTitle.Text = string.Format(T("正在複製 {0} 個項目到電腦：{1}"), count, J.S(request, "destination")); break;
                default:
                    transferTitle.Text = J.B(request, "overwrite") ? string.Format(T("正在寫回隨身碟：{0}"), J.S(request, "targetName"))
                        : string.Format(T("正在複製 {0} 個項目到 {1}"), count, VolumeLabel(J.S(request, "device")));
                    break;
            }
            transferFile.Text = T("準備中…");
            transferStats.Text = T("正在計算剩餘時間…");
            transferBar.Value = 0;
            transferGap.Visible = true; transfer.Visible = true;
        }
        void UpdateTransferView()
        {
            var p = Worker.Progress;
            if (p == null || J.L(p, "total") == 0 || J.S(p, "stage") == "") return;
            string stage;
            switch (J.S(p, "stage"))
            {
                case "upload": stage = T("傳送到 Linux"); break;
                case "write": stage = T("寫入隨身碟"); break;
                case "verify": stage = T("驗證 SHA-256"); break;
                case "download": stage = T("從隨身碟讀取"); break;
                default: stage = T("處理中"); break;
            }
            string which = J.I(p, "count") > 1 ? string.Format(T("（第 {0} / {1} 個）"), J.I(p, "index"), J.I(p, "count")) : "";
            transferFile.Text = string.Format("{0}：{1}{2}", stage, J.S(p, "file"), which);
            double done = J.D(p, "done"), total = J.D(p, "total");
            double fraction = Math.Min(1.0, done / total);
            transferBar.Value = (int)(100 * fraction);
            // Remaining time from the throughput over the last few seconds, which follows stage changes.
            DateTime now = DateTime.Now;
            samples.Add(new KeyValuePair<DateTime, double>(now, done));
            while (samples.Count > 2 && (now - samples[0].Key).TotalSeconds > 8) samples.RemoveAt(0);
            var first = samples[0];
            double span = (now - first.Key).TotalSeconds;
            double rate = span > 0 ? (done - first.Value) / span : 0;
            if ((now - opStarted).TotalSeconds < 2 || rate <= 0) transferStats.Text = string.Format(T("{0:P0} · 正在計算剩餘時間…"), fraction);
            else
            {
                int remaining = (int)Math.Ceiling((total - done) / rate);
                transferStats.Text = string.Format(T("{0:P0} · 剩餘約 {1} · 預計 {2} 完成"), fraction, FormatDuration(remaining), now.AddSeconds(remaining).ToString("HH:mm:ss"));
            }
        }

        // Boot time estimate: average of recent successful boots recorded by the worker.
        int BootEstimate()
        {
            if (vmState == "running") return 30;
            try
            {
                var history = J.A(Core.ReadConfig(), "bootSeconds").Select(x => Convert.ToDouble(x)).Where(x => x > 0).ToList();
                if (history.Count > 0) return Math.Max(15, (int)history.Average());
            }
            catch { }
            // The very first start installs packages offline and seals the image.
            return 120;
        }
        void UpdateBootProgress()
        {
            string phase = J.S(Worker.Progress, "phase");
            if (phase != "" && phase != bootPhase)
            {
                // The estimate restarts for a power-cycled boot, an agent update or sealing the image.
                bootPhase = phase; bootStarted = DateTime.Now;
                if (phase == "retry") LogLine(T("Linux 開機停滯，已自動重新啟動。"));
                if (phase == "updating") { bootEstimate = Math.Max(bootEstimate, 20); LogLine(T("正在更新 Linux 內的元件。")); }
                if (phase == "virtualbox") { bootEstimate = 75; LogLine(T("正在安裝 VirtualBox（請在系統管理員提示按「是」）。")); }
                if (phase == "prepare") { bootEstimate = 40; LogLine(T("正在建立 Linux 虛擬機。")); }
                if (phase == "sealing") { bootEstimate = Math.Max(bootEstimate, 40); LogLine(T("正在保存乾淨的 Linux 映像（只需一次），之後每次啟動都從這個狀態開始。")); }
            }
            double elapsed = (DateTime.Now - bootStarted).TotalSeconds;
            string prefix;
            switch (bootPhase)
            {
                case "virtualbox": prefix = T("正在安裝 VirtualBox"); break;
                case "prepare": prefix = T("正在建立 Linux"); break;
                case "retry": prefix = T("重新啟動中"); break;
                case "updating": prefix = T("正在更新元件"); break;
                case "sealing": prefix = T("正在保存乾淨映像"); break;
                default: prefix = T("正在啟動"); break;
            }
            if (elapsed < bootEstimate)
            {
                bootProgress.Value = (int)(90 * elapsed / bootEstimate);
                linuxDetail.Text = string.Format(T("{0}… 預估剩餘 {1}"), prefix, FormatDuration((int)Math.Ceiling(bootEstimate - elapsed)));
            }
            else
            {
                // Past the estimate: creep toward the end without claiming completion.
                bootProgress.Value = (int)(90 + 8 * (1 - Math.Exp(-(elapsed - bootEstimate) / 60)));
                linuxDetail.Text = string.Format(T("{0}… 比預期久，仍在等待（已 {1}）"), prefix, FormatDuration((int)elapsed));
            }
        }

        void Tick()
        {
            if (!busy)
            {
                if (vmState == "stopping")
                {
                    // Nothing else changes this state; poll until the VM has actually powered off.
                    if ((DateTime.Now - lastStoppingPoll).TotalSeconds >= 5) { lastStoppingPoll = DateTime.Now; BeginOperation(Req("action", "status")); }
                    return;
                }
                // Devices settle for a moment after a plug or unplug before the lists are re-read.
                if (refreshWanted && (DateTime.Now - lastDeviceChange).TotalSeconds >= 1.5)
                {
                    refreshWanted = false;
                    BeginOperation(Req("action", "status"));
                    return;
                }
                WatchOpenFiles();
                return;
            }
            if (job == null) return;
            if (!job.Done)
            {
                if (In(pendingAction, "start", "setup")) UpdateBootProgress();
                if (In(pendingAction, "copy", "fetch", "transfer", "download")) UpdateTransferView();
                return;
            }
            // Claim the finished job first: message boxes below keep this timer ticking, and a second tick
            // must not handle (and report) the same response again.
            var finished = job; job = null;
            string action = pendingAction;
            var request = pendingRequest;
            Map data = null;
            try
            {
                var response = finished.Response;
                if (!J.B(response, "ok")) throw new ToolException(J.S(response, "error"));
                data = J.M(response, "data") ?? new Map();
                Complete(action, request, data);
                if (!quietActions.Contains(action))
                {
                    LogLine(T("完成：") + actionNames[action]);
                    string done;
                    switch (action)
                    {
                        case "copy": done = J.B(request, "overwrite") ? T("已寫回隨身碟，SHA-256 驗證通過") : T("複製完成，SHA-256 驗證通過"); break;
                        case "transfer": done = T("隨身碟間傳輸完成，SHA-256 驗證通過"); break;
                        case "download": done = T("已複製到電腦，SHA-256 驗證通過"); break;
                        case "fetch": done = J.S(data, "saved") != "" ? T("已存到電腦") : T("已開啟；修改並儲存後，可寫回隨身碟"); break;
                        case "eject": done = T("已退出，可以安全拔除隨身碟"); break;
                        case "shutdown": done = J.B(data, "off") ? T("Linux 已關閉") : T("Linux 約一分鐘內關機"); break;
                        default: done = string.Format(T("{0}完成"), actionNames[action]); break;
                    }
                    SetMessage(done);
                }
            }
            catch (Exception e)
            {
                string reason = e.Message;
                LogLine(T("失敗：") + actionNames[action] + " — " + reason);
                SetMessage(T("操作失敗：") + reason);
                switchTarget = null; pendingNav = false; editAfter = null;
                // A failed start still needs a status refresh; writes re-read the folder they touched.
                followUp = action == "start" ? FollowUp.Do(Req("action", "status"))
                    : In(action, "delete", "rename", "mkdir", "copy", "transfer") && lin.Location.Kind == "folder" ? FollowUp.NavTo(lin.Location)
                    : action == "download" ? new FollowUp { RefreshWin = true } : null;
                if (!quietActions.Contains(action)) MessageBox.Show(string.Format(T("{0}失敗："), actionNames[action]) + "\n\n" + reason, "USB Proxy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { SetBusy(false); }
            if (followUp != null)
            {
                var next = followUp; followUp = null;
                if (next.OfferSetup) StartSetup();
                else if (next.RefreshWin) { if (win.Location.Kind == "folder") Navigate(win, win.Location, true); }
                else if (next.Nav != null)
                {
                    if (next.ThenStatus) { refreshWanted = true; lastDeviceChange = DateTime.MinValue; }
                    if (next.Nav.Kind != "folder") ShowLinComputer(); else Navigate(lin, next.Nav, !next.Push);
                }
                else if (next.Op != null) BeginOperation(next.Op);
            }
            UpdateCounts();
            if (!busy && pendingUpdate != null) { var info = pendingUpdate; pendingUpdate = null; InstallUpdate(info); }
        }

        // What each finished job changes in the window.
        void Complete(string action, Map request, Map data)
        {
            switch (action)
            {
                case "status":
                    {
                        usb = J.Maps(data, "usb");
                        remembered = J.Maps(data, "remembered");
                        var oldAttached = attached.Select(d => J.S(d, "uuid")).ToList();
                        var oldVolumes = volumes.Select(v => J.S(v, "path")).ToList();
                        var vm = J.M(data, "vm");
                        vmState = J.S(vm, "state");
                        attached = J.Maps(vm, "attached").Where(d => J.S(d, "uuid") != "").ToList();
                        ready = false;
                        UpdateGuest(J.M(data, "guest"));
                        if (J.S(data, "message") != "") LogLine(T("Linux 尚未就緒或連線失敗：") + J.S(data, "message"));
                        // A remembered drive went straight to Linux when it was plugged in: say so and open it.
                        var arrivedNow = attached.Where(d => !oldAttached.Contains(J.S(d, "uuid"))).ToList();
                        if (arrivedNow.Count > 0 && switchTarget == null)
                        {
                            foreach (var d in arrivedNow) LogLine(string.Format(T("「{0}」已直接切換到 Linux"), J.S(d, "name") != "" ? J.S(d, "name") : T("隨身碟")));
                            var newVolumes = volumes.Where(v => !oldVolumes.Contains(J.S(v, "path"))).ToList();
                            if (newVolumes.Count == 1 && lin.Location.Kind == "pc" && followUp == null)
                                followUp = new FollowUp { Nav = Loc.LinFolder(J.S(newVolumes[0], "path"), ""), Push = true };
                        }
                        if (switchTarget != null && ready) followUp = FollowUp.Do(Req("action", "attach", "uuid", J.S(switchTarget, "uuid"), "name", J.S(switchTarget, "name")));
                        else if (switchTarget != null) { LogLine(T("Linux 尚未就緒，無法切換隨身碟。")); switchTarget = null; }
                        if (win.Location.Kind == "pc") ShowWinComputer();
                        // A Linux folder whose volume disappeared falls back to the Linux root.
                        if (lin.Location.Kind == "folder" && !volumes.Any(v => J.S(v, "path") == lin.Location.Device)) ResetLin();
                        else if (lin.Location.Kind == "pc") ShowLinComputer();
                        // On a machine without the Linux environment, offer to install it once per session.
                        if (vmState == "notinstalled" && !setupOffered) { setupOffered = true; followUp = new FollowUp { OfferSetup = true }; }
                        // Boot Linux in the background as soon as the window opens, so it is ready when a drive is plugged in.
                        // A guest still booting from last time is also "started": start waits until it answers.
                        if (!autoStarted && followUp == null)
                        {
                            autoStarted = true;
                            if (In(vmState, "poweroff", "saved", "aborted") || (vmState == "running" && !ready)) followUp = FollowUp.Do(Req("action", "start"));
                        }
                        // Auto-switch: hand the next Windows-side drive to Linux, one per status (attach re-runs status).
                        if (UiB("autoLinux") && followUp == null && vmState == "running" && ready)
                        {
                            var mine = attached.Select(d => J.S(d, "uuid")).ToList();
                            var d0 = usb.FirstOrDefault(d => J.S(d, "uuid") != "" && !J.B(d, "stuck") && J.S(d, "Current State") != "Captured"
                                && !mine.Contains(J.S(d, "uuid")) && !autoSkip.Contains(J.S(d, "uuid")));
                            if (d0 != null)
                            {
                                autoSkip.Add(J.S(d0, "uuid"));
                                string name = DeviceName(d0);
                                if (name == "") name = T("隨身碟");
                                foreach (var l in J.A(d0, "drives").Select(x => Convert.ToString(x)).Where(x => x != ""))
                                    if (win.Location.Kind == "folder" && win.Location.Path.StartsWith(l, StringComparison.OrdinalIgnoreCase)) Navigate(win, Loc.Pc());
                                LogLine(string.Format(T("自動切換「{0}」到 Linux（可在設定關閉）"), name));
                                followUp = FollowUp.Do(Req("action", "attach", "uuid", J.S(d0, "uuid"), "name", name));
                            }
                        }
                        break;
                    }
                case "forget":
                    remembered = J.Maps(data, "remembered");
                    break;
                case "info":
                    ShowVolumeInfo(data);
                    break;
                case "format":
                    LogLine(string.Format(T("已格式化 {0}（{1}）。"), J.S(request, "device"), FsName(J.S(request, "fstype"))));
                    if (lin.Location.Kind == "folder" && lin.Location.Device == J.S(request, "device")) ResetLin();
                    followUp = FollowUp.Do(Req("action", "status"));
                    break;
                case "setup":
                    LogLine(T("安裝完成，耗時 ") + FormatDuration((int)(DateTime.Now - opStarted).TotalSeconds) + T("。接著第一次啟動 Linux。"));
                    vmState = "poweroff";
                    followUp = FollowUp.Do(Req("action", "start"));
                    break;
                case "start":
                    vmState = "running"; ready = true;
                    LogLine(T("Linux 啟動耗時 ") + FormatDuration((int)(DateTime.Now - bootStarted).TotalSeconds));
                    if (J.B(data, "agentUpdated")) LogLine(T("Linux 內的元件已更新。"));
                    followUp = FollowUp.Do(Req("action", "status"));
                    break;
                case "attach":
                    {
                        UpdateGuest(J.M(data, "guest"));
                        switchTarget = null;
                        // Known before the next status, so that status does not report it as arriving on its own.
                        attached.Add(J.Obj("uuid", J.S(request, "uuid"), "name", J.S(request, "name")));
                        var fresh = J.Strings(data, "volumes");
                        // Open the drive's only partition; otherwise show every drive in Linux.
                        if (fresh.Count == 1)
                        {
                            lin.History.Clear(); lin.Forward.Clear(); lin.Location = Loc.Pc();
                            followUp = new FollowUp { Nav = Loc.LinFolder(fresh[0], ""), Push = true, ThenStatus = true };
                        }
                        else followUp = FollowUp.Do(Req("action", "status"));
                        break;
                    }
                case "list":
                    {
                        bool push = pendingNav && pendingNavPush; pendingNav = false;
                        var target = Loc.LinFolder(J.S(request, "device"), J.S(data, "path"));
                        if (push && !lin.Location.Same(target)) { lin.History.Add(lin.Location); lin.Forward.Clear(); }
                        lin.Location = target;
                        ShowLinFolder(data, J.S(request, "device"));
                        break;
                    }
                case "copy":
                    if (J.B(request, "overwrite")) LogLine(string.Format(T("已寫回「{0}」，SHA-256 驗證通過。"), J.S(request, "targetName")));
                    else LogLine(string.Format(T("已複製並驗證 {0} 個檔案，共 {1}。"), J.A(data, "copied").Count, FormatSize(J.D(data, "bytes"))));
                    if (J.B(request, "move"))
                    {
                        RecycleWindows(J.Strings(request, "paths"));
                        try { Clipboard.Clear(); } catch { }
                        if (win.Location.Kind == "folder") Navigate(win, win.Location, true);
                    }
                    break;
                case "delete":
                    if (followUp == null && lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
                    break;
                case "transfer":
                    LogLine(string.Format(T("隨身碟間傳輸完成：{0} 個檔案，共 {1}，SHA-256 驗證通過。"), J.A(data, "copied").Count, FormatSize(J.D(data, "bytes"))));
                    if (J.B(request, "move")) { linuxClip = null; followUp = FollowUp.Do(Req("action", "delete", "device", J.S(request, "source"), "paths", J.Strings(request, "paths").ToArray())); }
                    break;
                case "download":
                    LogLine(string.Format(T("已複製 {0} 個檔案（{1}）到 {2}，SHA-256 驗證通過。"), J.I(data, "downloaded"), FormatSize(J.D(data, "bytes")), J.S(data, "destination")));
                    if (J.B(request, "move"))
                    {
                        linuxClip = null;
                        if (win.Location.Kind == "folder") Navigate(win, win.Location, true);
                        followUp = FollowUp.Do(Req("action", "delete", "device", J.S(request, "device"), "paths", J.Strings(request, "paths").ToArray()));
                    }
                    break;
                case "fetch":
                    if (J.S(data, "saved") != "") LogLine(T("已存到 ") + J.S(data, "saved"));
                    else
                    {
                        string local = J.S(data, "local");
                        openFiles.Add(new OpenFile { Local = local, Remote = J.S(data, "path"), Name = J.S(data, "name"), Device = J.S(request, "device"), Stamp = File.GetLastWriteTimeUtc(local) });
                        try { Process.Start(local); }
                        catch { MessageBox.Show(T("沒有可開啟這個檔案的程式。"), "USB Proxy"); }
                    }
                    break;
                case "eject":
                    followUp = FollowUp.Do(Req("action", "status"));
                    break;
                case "shutdown":
                    vmState = J.B(data, "off") ? "poweroff" : "stopping";
                    ready = false;
                    if (J.B(data, "ejected")) { attached = new List<Map>(); UpdateGuest(null); openFiles.Clear(); ResetLin(); LogLine(T("已自動退出所有隨身碟。")); }
                    followUp = FollowUp.Do(Req("action", restartAfter && J.B(data, "off") ? "start" : "status"));
                    restartAfter = false;
                    break;
            }
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            bool shutdown = UiB("shutdownOnClose") && !relaunch;   // not when restarting after an update
            if (busy)
            {
                // Starting or checking Linux touches no files, so it can be abandoned; file work must finish first.
                if (!In(pendingAction, "status", "start", "list"))
                {
                    e.Cancel = true; MessageBox.Show(T("正在處理隨身碟上的檔案，請等待完成再關閉，以免資料損壞。"), "USB Proxy"); return;
                }
                if (!shutdown && !relaunch && MessageBox.Show(T("仍在啟動或檢查 Linux。要中止並關閉視窗嗎？（Linux 會在背景繼續開機，下次開啟時自動接上）"), "USB Proxy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { e.Cancel = true; return; }
            }
            SaveWindowState();
            if (!shutdown) return;   // Linux and its drives keep running for next time
            // Shut Linux down with the tool. The ACPI power button lets Debian unmount and flush the drives itself,
            // so the window closes at once; remembered drives are captured again when Linux next starts.
            // A boot in progress has nothing mounted and is just stopped.
            try
            {
                string vm = J.S(Core.ReadConfig(), "vmName");
                if (busy && pendingAction == "start") Core.VBox("controlvm", vm, "poweroff");
                else if (!In(vmState, "poweroff", "notinstalled", "saved", "aborted")) Core.VBox("controlvm", vm, "acpipowerbutton");
            }
            catch { }
        }
        // Remember the views, split and window size for next time.
        void SaveWindowState()
        {
            try
            {
                var bounds = form.WindowState == FormWindowState.Normal ? form.ClientSize : form.RestoreBounds.Size;
                ui["width"] = (int)(bounds.Width / scale); ui["height"] = (int)(bounds.Height / scale);
                ui["maximized"] = form.WindowState == FormWindowState.Maximized;
                if (!split.Panel1Collapsed) ui["split"] = Math.Round(split.SplitterDistance / (double)Math.Max(1, split.Width), 3);
                SaveUi();
            }
            catch { }
        }
    

        // ---------------- Drive properties and format (right-click a drive in Linux) ----------------
        // The drive the Linux menu acts on: a selected volume at the top level, or the volume being browsed.
        string MenuVolume()
        {
            var selected = Selected(lin);
            if (selected.Count == 1 && selected[0].Kind == "volume") return selected[0].Device;
            if (selected.Count == 0 && lin.Location.Kind == "folder") return lin.Location.Device;
            return null;
        }
        void ShowLinuxInfo() { string d = MenuVolume(); if (d != null && !busy) BeginOperation(Req("action", "info", "device", d)); }
        static string FsName(string fstype)
        {
            switch (fstype) { case "vfat": return "FAT32"; case "exfat": return "exFAT"; case "ntfs": return "NTFS"; case "ext4": return "ext4"; default: return fstype.ToUpperInvariant(); }
        }
        Form NewDialog(string title, int width, int height)
        {
            return new Form
            {
                Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = bodyFont,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                BackColor = Theme.Background, ForeColor = Theme.Label, ClientSize = new Size(width, height)
            };
        }
        void ShowVolumeInfo(Map info)
        {
            string device = J.S(info, "path");
            long total = J.L(info, "total"), free = J.L(info, "free"), used = J.L(info, "used");
            string name = J.S(info, "label") != "" ? J.S(info, "label") : T("USB 隨身碟");
            bool format = false;
            using (var dialog = NewDialog(name + " " + T("內容"), 400, 330))
            {
                NewText(dialog, name, 20, 16, 360, 26, headlineFont);
                int y = 56;
                Action<string, string> row = (k, v) => { NewText(dialog, k, 20, y, 110, 22, captionFont, Theme.SecondaryLabel); NewText(dialog, v, 130, y, 250, 22); y += 26; };
                row(T("類型"), T("USB 隨身碟") + (J.S(info, "model") != "" ? " · " + J.S(info, "model") : ""));
                row(T("檔案系統"), FsName(J.S(info, "fstype")));
                row(T("已使用空間"), FormatSize(used) + "  (" + used.ToString("N0") + " " + T("位元組") + ")");
                row(T("可用空間"), FormatSize(free) + "  (" + free.ToString("N0") + " " + T("位元組") + ")");
                row(T("容量"), FormatSize(total) + "  (" + total.ToString("N0") + " " + T("位元組") + ")");
                // Usage bar, like Explorer's: used share in the accent colour.
                var bar = new Panel { Location = new Point(20, y + 6), Size = new Size(360, 14), BackColor = Theme.Fill };
                bar.Controls.Add(new Panel { Dock = DockStyle.Left, Width = total > 0 ? (int)(360.0 * used / total) : 0, BackColor = Theme.Accent });
                dialog.Controls.Add(bar);
                y += 32;
                row(T("裝置"), device + (J.S(info, "serial") != "" ? " · " + T("序號") + " " + J.S(info, "serial") : ""));
                var formatButton = new Button { Text = T("格式化…"), Location = new Point(20, 284), Size = new Size(110, 30), Enabled = !busy };
                formatButton.Click += (s, e) => { format = true; dialog.Close(); };
                var ok = new Button { Text = T("確定"), DialogResult = DialogResult.OK, Location = new Point(290, 284), Size = new Size(90, 30) };
                dialog.Controls.Add(formatButton); dialog.Controls.Add(ok); dialog.AcceptButton = ok; dialog.CancelButton = ok;
                dialog.ShowDialog(form);
            }
            if (format) ShowFormat(device);
        }
        void ShowFormat(string device)
        {
            if (busy) return;
            var volume = volumes.FirstOrDefault(v => J.S(v, "path") == device);
            if (volume == null) return;
            string name = (J.S(volume, "label") != "" ? J.S(volume, "label") : T("USB 隨身碟")) + " (" + device + ")";
            string[] types = { "exfat", "vfat", "ntfs", "ext4" };
            string fstype, label;
            using (var dialog = NewDialog(T("格式化") + " " + name, 400, 270))
            {
                NewText(dialog, T("檔案系統"), 20, 20, 110, 24);
                var typeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(140, 18), Width = 240 };
                typeBox.Items.AddRange(new object[] { T("exFAT（建議，Windows／Mac 通用）"), T("FAT32（單檔上限 4 GB）"), "NTFS", T("ext4（只有 Linux 能讀）") });
                int current = Array.IndexOf(types, J.S(volume, "fstype"));
                typeBox.SelectedIndex = current >= 0 ? current : 0;
                dialog.Controls.Add(typeBox);
                NewText(dialog, T("磁碟區標籤"), 20, 60, 110, 24);
                var labelBox = new TextBox { Text = J.S(volume, "label"), Location = new Point(140, 58), Width = 240 };
                dialog.Controls.Add(labelBox);
                NewText(dialog, T("格式化會清除這個分割區上的所有資料，無法復原。只會格式化這個分割區，不會變更分割表。"), 20, 100, 360, 66, captionFont, Theme.Red);
                var ok = new Button { Text = T("開始"), DialogResult = DialogResult.OK, Location = new Point(194, 222), Size = new Size(90, 30) };
                var cancel = new Button { Text = T("取消"), DialogResult = DialogResult.Cancel, Location = new Point(290, 222), Size = new Size(90, 30) };
                dialog.Controls.Add(ok); dialog.Controls.Add(cancel); dialog.CancelButton = cancel;
                if (dialog.ShowDialog(form) != DialogResult.OK) return;
                fstype = types[typeBox.SelectedIndex]; label = labelBox.Text.Trim();
            }
            if (MessageBox.Show(form, string.Format(T("確定要把「{0}」格式化為 {1} 嗎？\n\n上面所有的檔案都會被清除，無法復原。"), name, FsName(fstype)),
                T("格式化"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            BeginOperation(Req("action", "format", "device", device, "fstype", fstype, "label", label));
        }
    }
}
