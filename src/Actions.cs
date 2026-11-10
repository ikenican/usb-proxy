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

        // Copies in every direction: Windows -> Linux (copy), Linux -> Linux (transfer), Linux -> Windows (download).
        void CopyToLinux(IEnumerable<string> paths, string device, string folder)
        {
            var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToArray();
            if (busy || string.IsNullOrEmpty(device) || list.Length == 0) return;
            if (lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "copy", "device", device, "dest", folder, "paths", list));
        }
        void TransferInLinux(Clip clip, string device, string folder)
        {
            if (busy || clip == null || string.IsNullOrEmpty(device)) return;
            // Dropping items back into their own folder is a no-op.
            if (clip.Device == device && clip.Paths.All(p => ParentPath(p) == folder)) return;
            if (lin.Location.Kind == "folder") followUp = FollowUp.NavTo(lin.Location);
            BeginOperation(Req("action", "transfer", "source", clip.Device, "paths", clip.Paths.ToArray(), "device", device, "dest", folder));
        }
        void DownloadToWindows(Clip clip, string folder)
        {
            if (busy || clip == null || string.IsNullOrEmpty(folder)) return;
            followUp = new FollowUp { RefreshWin = true };
            BeginOperation(Req("action", "download", "device", clip.Device, "paths", clip.Paths.ToArray(), "destination", folder));
        }
        Clip LinuxSelection()
        {
            var selected = Selected(lin).Where(e => In(e.Kind, "file", "folder")).ToList();
            if (selected.Count == 0) return null;
            return new Clip { Device = selected[0].Device, Paths = selected.Select(e => e.Path).ToList() };
        }
        List<string> WinSelectionPaths() { return Selected(win).Where(e => In(e.Kind, "file", "folder")).Select(e => e.Path).ToList(); }
        void CopySelection(Pane pane)
        {
            if (pane == lin) linuxClip = LinuxSelection();
            else
            {
                var paths = WinSelectionPaths();
                if (paths.Count > 0)
                {
                    var collection = new StringCollection(); collection.AddRange(paths.ToArray());
                    Clipboard.SetFileDropList(collection);
                    linuxClip = null;
                }
            }
            UpdateCounts();
        }
        void PasteInto(Pane pane)
        {
            if (busy) return;
            if (pane == lin)
            {
                if (lin.Location.Kind != "folder") return;
                if (linuxClip != null) TransferInLinux(linuxClip, lin.Location.Device, lin.Location.Path);
                else if (Clipboard.ContainsFileDropList()) CopyToLinux(Clipboard.GetFileDropList().Cast<string>(), lin.Location.Device, lin.Location.Path);
            }
            else if (win.Location.Kind == "folder" && linuxClip != null) DownloadToWindows(linuxClip, win.Location.Path);
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
                    if (e.Alt && e.KeyCode == Keys.Left) { GoBack(p); e.Handled = true; return; }
                    if (e.Alt && e.KeyCode == Keys.Up) { GoUp(p); e.Handled = true; return; }
                    if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem item in p.List.Items) item.Selected = true; e.Handled = true; return; }
                    if (e.Control && e.KeyCode == Keys.C) { CopySelection(p); e.Handled = true; return; }
                    if (e.Control && e.KeyCode == Keys.V) { PasteInto(p); e.Handled = true; return; }
                    switch (e.KeyCode)
                    {
                        case Keys.Back: GoBack(p); break;
                        case Keys.F5: RefreshPane(p); break;
                        case Keys.Delete: if (p == lin) RemoveLinuxItems(); break;
                        case Keys.F2: if (p == lin) RenameLinuxItem(); break;
                    }
                };
                SetZoom(p, UiI("zoom_" + p.Key));
            }
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
            var wCopy = AddMenuItem(winMenu, T("複製"), () => CopySelection(win), "Ctrl+C");
            var wPaste = AddMenuItem(winMenu, T("貼上（從 Linux）"), () => PasteInto(win), "Ctrl+V");
            var wShow = AddMenuItem(winMenu, T("在檔案總管中顯示"), () => { var e = Selected(win).FirstOrDefault(); if (e != null && !string.IsNullOrEmpty(e.Path)) Process.Start("explorer.exe", "/select,\"" + e.Path + "\""); });
            winMenu.Items.Add(new ToolStripSeparator());
            AddMenuItem(winMenu, T("重新整理"), () => RefreshPane(win), "F5");
            winMenu.Opening += (s, e) =>
            {
                var selected = Selected(win); string kind = selected.Count == 1 ? selected[0].Kind : "";
                int files = selected.Count(x => In(x.Kind, "file", "folder"));
                wOpen.Visible = selected.Count == 1 && kind != "device";
                wSwitch.Visible = In(kind, "device", "usb-drive"); wSwitch.Enabled = !busy;
                wSend.Visible = files > 0; wSend.Enabled = !busy;
                wCopy.Visible = files > 0;
                wPaste.Visible = win.Location.Kind == "folder"; wPaste.Enabled = !busy && linuxClip != null;
                wShow.Visible = selected.Count == 1 && In(kind, "file", "folder");
            };
            win.List.ContextMenuStrip = winMenu; win.Empty.ContextMenuStrip = winMenu;

            linMenu = NewMenu();
            var lOpen = AddMenuItem(linMenu, T("開啟"), () => OpenEntry(lin), "Enter");
            var lSave = AddMenuItem(linMenu, T("存到電腦…"), SaveLinuxFile);
            var lSend = AddMenuItem(linMenu, T("複製到左側 Windows 資料夾"), () => SendToOtherPane(lin));
            var lCopy = AddMenuItem(linMenu, T("複製"), () => CopySelection(lin), "Ctrl+C");
            var lPaste = AddMenuItem(linMenu, T("貼上"), () => PasteInto(lin), "Ctrl+V");
            var lRename = AddMenuItem(linMenu, T("重新命名"), RenameLinuxItem, "F2");
            var lDelete = AddMenuItem(linMenu, T("刪除"), RemoveLinuxItems, "Del");
            var lEject = AddMenuItem(linMenu, T("退出（交還 Windows）"), () =>
            {
                var e = Selected(lin).FirstOrDefault();
                EjectDrive(e != null && !string.IsNullOrEmpty(e.Uuid) ? e.Uuid : LocationDrive());
            });
            linMenu.Items.Add(new ToolStripSeparator());
            var lNewFolder = AddMenuItem(linMenu, T("新增資料夾"), NewLinuxFolder);
            var lRefresh = AddMenuItem(linMenu, T("重新整理"), () => RefreshPane(lin), "F5");
            linMenu.Opening += (s, e) =>
            {
                var selected = Selected(lin); bool idle = !busy;
                bool one = selected.Count == 1; string kind = one ? selected[0].Kind : "";
                bool inFolder = lin.Location.Kind == "folder";
                int files = selected.Count(x => In(x.Kind, "file", "folder"));
                lOpen.Visible = one;
                lSave.Visible = inFolder && kind == "file";
                lSend.Visible = files > 0; lCopy.Visible = files > 0;
                lPaste.Visible = inFolder; lPaste.Enabled = idle && (linuxClip != null || Clipboard.ContainsFileDropList());
                lRename.Visible = inFolder && one; lDelete.Visible = inFolder && selected.Count > 0;
                lEject.Visible = In(kind, "volume", "linux-device") || (inFolder && selected.Count == 0);
                lNewFolder.Visible = inFolder;
                foreach (var item in new[] { lOpen, lSave, lSend, lRename, lDelete, lEject, lNewFolder, lRefresh }) item.Enabled = idle;
            };
            lin.List.ContextMenuStrip = linMenu; lin.Empty.ContextMenuStrip = linMenu;
        }

        // ---------------- Background jobs ----------------
        void BeginOperation(Map request)
        {
            if (busy) return;
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
                new Thread(() => { started.Response = Worker.Run(request); started.Done = true; }) { IsBackground = true }.Start();
                opStarted = DateTime.Now;
                if (In(action, "start", "setup"))
                {
                    bootStarted = DateTime.Now; bootPhase = "";
                    bootEstimate = action == "setup" ? 75 : BootEstimate();
                    bootProgress.Value = 0; bootProgress.Visible = true;
                }
                if (In(action, "copy", "fetch", "transfer", "download")) StartTransferView(request);
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
            bool succeeded = false;
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
                succeeded = true;
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
            if (closeWhenDone)
            {
                closeWhenDone = false;
                if (succeeded && action == "shutdown") { form.Close(); return; }
            }
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
                            lin.History.Clear(); lin.Location = Loc.Pc();
                            followUp = new FollowUp { Nav = Loc.LinFolder(fresh[0], ""), Push = true, ThenStatus = true };
                        }
                        else followUp = FollowUp.Do(Req("action", "status"));
                        break;
                    }
                case "list":
                    {
                        bool push = pendingNav && pendingNavPush; pendingNav = false;
                        var target = Loc.LinFolder(J.S(request, "device"), J.S(data, "path"));
                        if (push && !lin.Location.Same(target)) lin.History.Add(lin.Location);
                        lin.Location = target;
                        ShowLinFolder(data, J.S(request, "device"));
                        break;
                    }
                case "copy":
                    if (J.B(request, "overwrite")) LogLine(string.Format(T("已寫回「{0}」，SHA-256 驗證通過。"), J.S(request, "targetName")));
                    else LogLine(string.Format(T("已複製並驗證 {0} 個檔案，共 {1}。"), J.A(data, "copied").Count, FormatSize(J.D(data, "bytes"))));
                    break;
                case "transfer":
                    LogLine(string.Format(T("隨身碟間傳輸完成：{0} 個檔案，共 {1}，SHA-256 驗證通過。"), J.A(data, "copied").Count, FormatSize(J.D(data, "bytes"))));
                    break;
                case "download":
                    LogLine(string.Format(T("已複製 {0} 個檔案（{1}）到 {2}，SHA-256 驗證通過。"), J.I(data, "downloaded"), FormatSize(J.D(data, "bytes")), J.S(data, "destination")));
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
            if (busy)
            {
                // Starting or checking Linux touches no files, so it can be abandoned; the VM keeps booting on its own.
                if (In(pendingAction, "status", "start", "list"))
                {
                    if (MessageBox.Show(T("仍在啟動或檢查 Linux。要中止並關閉視窗嗎？（Linux 會在背景繼續開機，下次開啟時自動接上）"), "USB Proxy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) { e.Cancel = true; return; }
                    busy = false;
                }
                else
                {
                    e.Cancel = true; MessageBox.Show(T("正在處理隨身碟上的檔案，請等待完成再關閉，以免資料損壞。"), "USB Proxy"); return;
                }
            }
            if (attached.Count > 0 && !relaunch && !UiB("keepOnClose"))
            {
                e.Cancel = true;
                if (MessageBox.Show(T("仍有隨身碟在 Linux 中。要安全退出、關閉 Linux 後再結束嗎？"), "USB Proxy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                {
                    closeWhenDone = true; BeginOperation(Req("action", "shutdown"));
                }
                return;
            }
            SaveWindowState();
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
    }
}
