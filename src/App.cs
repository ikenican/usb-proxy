// USB Proxy window: Windows files on the left, the Linux VM's USB volumes on the right.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using UsbProxyUi;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace UsbProxy
{
    static class Program
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        [STAThread]
        static int Main(string[] args)
        {
            // Elevated helpers started by this same exe (UAC prompt).
            if (args.Length == 2 && args[0] == "--cleanup") { Worker.CleanupRecord(args[1]); return 0; }
            if (args.Length == 2 && args[0] == "--install-host") return Setup.InstallHostElevated(args[1]);
            // Diagnostics: run one background request from a JSON file and print the response.
            if (args.Length == 2 && args[0] == "--run")
            {
                byte[] output = Core.Utf8.GetBytes(J.Write(Worker.Run(J.Read(File.ReadAllText(args[1], Encoding.UTF8)))));
                using (var stdout = Console.OpenStandardOutput()) stdout.Write(output, 0, output.Length);
                return 0;
            }
            // Diagnostics: check GitHub and install a newer release without the window.
            if (args.Length == 1 && args[0] == "--update")
            {
                var info = Updater.Check();
                Console.WriteLine(info == null ? "latest " + Updater.Current : "installing " + J.S(info, "version"));
                if (info != null) Updater.Install(info);
                return 0;
            }
            bool smoke = args.Length == 1 && args[0] == "--smoke-test";
            if (!smoke)
            {
                // Already open: bring it forward instead of starting a second copy.
                IntPtr existing = FindWindow(null, "USB Proxy");
                if (existing != IntPtr.Zero) { ShowWindow(existing, 9); SetForegroundWindow(existing); return 0; }
            }
            Updater.CleanOld();
            Core.LoadLanguage();
            Native.EnableDpiAwareness();
            Application.EnableVisualStyles();
            // An error in any event handler is logged and the window stays open, instead of the whole tool closing.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            var app = new MainUi();
            Application.ThreadException += (s, e) => app.OnError(e.Exception);
            return app.Run(smoke);
        }
    }

    class Loc
    {
        public string Kind = "pc", Path = "", Device = "";
        public static Loc Pc() { return new Loc(); }
        public static Loc WinFolder(string path) { return new Loc { Kind = "folder", Path = path }; }
        public static Loc LinFolder(string device, string path) { return new Loc { Kind = "folder", Device = device ?? "", Path = path ?? "" }; }
        public bool Same(Loc o) { return o != null && Kind == o.Kind && Path == o.Path && Device == o.Device; }
    }

    class Pane
    {
        public string Key, Root;
        public ExplorerListView List;
        public Label Empty, Address;
        public ImageList Large;
        public System.Collections.IComparer Sorter;
        public Loc Location = Loc.Pc();
        public List<Loc> History = new List<Loc>(), Forward = new List<Loc>();
        public bool Settling;
        public PillButton Back, Up, Refresh;
        public PopupSelect View;
        public Card Body;
    }

    class OpenFile { public string Local, Remote, Name, Device; public DateTime Stamp; public DateTime? Seen; }
    class Clip { public string Device; public List<string> Paths; public bool Cut; }
    class FollowUp
    {
        public Map Op; public bool OfferSetup, RefreshWin, Push, ThenStatus; public Loc Nav;
        public static FollowUp Do(Map op) { return new FollowUp { Op = op }; }
        public static FollowUp NavTo(Loc nav) { return new FollowUp { Nav = nav }; }
    }
    class Job { public volatile bool Done; public Map Response; }

    partial class MainUi
    {
        static string T(string s) { return Core.T(s); }
        static Map Req(params object[] pairs) { return J.Obj(pairs); }
        static bool In(string value, params string[] set) { return set.Contains(value); }

        // --- State ---
        bool busy;
        Job job;
        string pendingAction = "";
        Map pendingRequest;
        FollowUp followUp;
        List<Map> usb = new List<Map>(), volumes = new List<Map>(), attached = new List<Map>();
        bool ready;
        string vmState = "unknown";
        DateTime lastStoppingPoll = DateTime.MinValue;
        bool pendingNav, pendingNavPush;   // a Linux folder listing is on its way (and whether it goes into history)
        Dictionary<string, long[]> volumeInfo = new Dictionary<string, long[]>();   // device -> {free, total}
        Map switchTarget;
        // Drives auto-switch already tried (or the user ejected) while plugged in; VirtualBox gives a replugged drive a new UUID.
        HashSet<string> autoSkip = new HashSet<string>();
        List<Map> remembered = new List<Map>();   // drives that go straight to Linux when plugged in (VM filters)
        string editAfter;
        bool refreshWanted;
        DateTime lastDeviceChange = DateTime.MinValue;
        List<OpenFile> openFiles = new List<OpenFile>();
        DateTime lastOpenCheck = DateTime.MinValue;
        bool prompting;
        List<KeyValuePair<DateTime, double>> samples = new List<KeyValuePair<DateTime, double>>();
        Clip linuxClip;
        Dictionary<string, string> typeNames = new Dictionary<string, string>();
        Dictionary<string, string> actionNames;
        bool setupOffered, autoStarted, restartAfter, relaunch;
        static readonly string[] quietActions = { "status", "list" };
        DateTime opStarted, bootStarted;
        string bootPhase = "";
        int bootEstimate;
        DateTime? winAwaySince;
        Map ui = J.Obj("zoom_win", 0, "zoom_lin", 0, "icon_win", 2, "icon_lin", 2, "width", 1200, "height", 760, "maximized", false,
            "split", 0.5, "pin_win", true, "lang", "zh", "theme", "light", "cpus", 1, "memory", 1024, "autoLinux", true, "autoUpdate", true, "shutdownOnClose", false);
        int UiI(string k) { return J.I(ui, k); }
        bool UiB(string k) { return J.B(ui, k); }
        void SaveUi() { try { Core.WriteUtf8(Core.UiFile, J.Write(ui)); } catch { } }

        // --- Controls ---
        Font bodyFont, headlineFont, captionFont, glyphFont;
        Form form, logWindow;
        Image penguin;
        float scale;
        SplitContainer split;
        Card transfer, linuxCard;
        Label transferTitle, transferFile, transferStats, statusText, linuxTitle, linuxDetail;
        ThinProgress transferBar, bootProgress;
        Panel transferGap, statusBar, winGap;
        PillButton linuxToggle, winStrip, winPin;
        PenguinBadge dot;
        TextBox log;
        ImageList smallIcons;
        Pane win, lin, active;
        Pane[] panes;
        ToolTip pinTip;
        ContextMenuStrip winMenu, linMenu;
        System.Windows.Forms.Timer timer;
        DeviceChangeWatcher deviceWatcher;
        const string linuxFormat = "UsbProxy.LinuxItems";
        static readonly int[] zoomSizes = { 0, 0, 48, 64, 96, 128, 192, 256 };

        public void OnError(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(Core.Root, "logs"));
                File.AppendAllText(Path.Combine(Core.Root, @"logs\ui-errors.txt"), "=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\r\n" + ex + "\r\n", Encoding.UTF8);
            }
            catch { }
            try { LogLine(T("程式內部錯誤（已記錄到 logs\\ui-errors.txt）：") + ex.Message); } catch { }
        }

        public int Run(bool smoke)
        {
            try
            {
                var saved = J.Read(Core.ReadText(Core.UiFile));
                foreach (string key in ui.Keys.ToList()) if (J.Get(saved, key) != null) ui[key] = saved[key];
            }
            catch { }
            BuildNames();
            Build();
            // Local copies of files opened in an earlier session; files still open elsewhere are skipped.
            try { foreach (string d in Directory.GetDirectories(Path.Combine(Core.Runtime, "open"))) try { Directory.Delete(d, true); } catch { } } catch { }
            ShowWinComputer();
            ShowLinComputer();
            UpdateState();
            if (smoke)
            {
                Console.WriteLine("UI constructed: " + form.Controls.Count + " top-level controls");
                form.Show();
                Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(Core.Root, @"logs\ui-preview.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                ui["theme"] = J.S(ui, "theme") == "dark" ? "light" : "dark"; ui["lang"] = J.S(ui, "lang") == "en" ? "zh" : "en";
                SwapWindow();
                Application.DoEvents();
                using (var bitmap = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.Combine(Core.Root, @"logs\ui-preview-swapped.png"), System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.WriteLine("Swapped: " + form.Controls.Count + " top-level controls");
                timer.Dispose(); form.Dispose();
                return 0;
            }
            form.Shown += (s, e) => { timer.Start(); BeginOperation(Req("action", "status")); if (UiB("autoUpdate")) CheckUpdate(false); };
            // A context, not Application.Run(form): switching theme or language swaps in a new window.
            appContext = new ApplicationContext(form);
            try { Application.Run(appContext); }
            finally { timer.Stop(); timer.Dispose(); form.Dispose(); }
            if (relaunch) Process.Start(Core.ExePath);
            return 0;
        }
        ApplicationContext appContext;

        void BuildNames()
        {
            actionNames = new Dictionary<string, string> {
                {"status",T("重新整理")}, {"start",T("啟動 Linux")}, {"shutdown",T("關閉 Linux")}, {"attach",T("切換到 Linux")}, {"eject",T("退出隨身碟")},
                {"copy",T("複製到隨身碟")}, {"transfer",T("隨身碟間傳輸")}, {"download",T("複製到電腦")}, {"list",T("讀取資料夾")}, {"delete",T("刪除")},
                {"mkdir",T("新增資料夾")}, {"rename",T("重新命名")}, {"fetch",T("讀取檔案")}, {"setup",T("安裝 Linux 環境")}, {"forget",T("忘記隨身碟")}, {"info",T("讀取內容")}, {"format",T("格式化")} };
        }

        // ---------------- Layout ----------------
        Label NewText(Control parent, string text, int x, int y, int width, int height = 22, Font font = null, Color? color = null, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
        {
            font = font ?? bodyFont;
            var label = new Label { Text = text, Font = font, ForeColor = color ?? Theme.Label, AutoEllipsis = true, UseMnemonic = false, Anchor = anchor };
            label.BackColor = parent is Card ? ((Card)parent).FillColor : parent.BackColor;
            // Never shorter than the font: fonts follow the display scale, and a CJK line that does not fit is not drawn at all.
            label.Location = new Point(x, y); label.Size = new Size(width, Math.Max(height, font.Height));
            parent.Controls.Add(label);
            return label;
        }
        PillButton NewButton(Control parent, string text, ButtonKind kind, int x, int y, int width, Action handler, int height = 32, AnchorStyles anchor = AnchorStyles.Top | AnchorStyles.Left)
        {
            var button = new PillButton { Text = text, Kind = kind, Anchor = anchor, Location = new Point(x, y), Size = new Size(width, height) };
            button.Click += (s, e) => handler();
            parent.Controls.Add(button);
            return button;
        }
        Panel NewSpacer(DockStyle dock, int size) { return new Panel { Dock = dock, Height = size, BackColor = form.BackColor }; }

        const AnchorStyles TLR = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        const AnchorStyles TR = AnchorStyles.Top | AnchorStyles.Right;

        void Build()
        {
            if (J.S(ui, "theme") == "dark") Theme.UseDark(); else Theme.UseLight();
            const string family = "Microsoft JhengHei UI";
            bodyFont = new Font(family, 9.75f);
            headlineFont = new Font(family, 11, FontStyle.Bold);
            captionFont = new Font(family, 9);
            glyphFont = new Font("Segoe UI Symbol", 11);

            const int W = 1200, H = 760;
            form = new Form
            {
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, Text = "USB Proxy",
                Icon = new Icon(Path.Combine(Core.Root, @"src\app.ico")), ClientSize = new Size(W, H), MinimumSize = new Size(900, 560),
                BackColor = Theme.Background, ForeColor = Theme.Label, Font = bodyFont, StartPosition = FormStartPosition.CenterScreen,
                Padding = new Padding(16, 12, 16, 6)
            };
            penguin = Image.FromFile(Path.Combine(Core.Root, @"src\penguin.png"));
            scale = form.DeviceDpi / 96f;

            // --- Layout (docked, so the window can be resized and maximized) ---
            int innerWidth = W - 32;
            split = new SplitContainer { Size = new Size(innerWidth, H - 12 - 64 - 10 - 36), SplitterWidth = 12, BackColor = form.BackColor };
            split.Panel1.BackColor = form.BackColor; split.Panel2.BackColor = form.BackColor;
            split.SplitterDistance = (innerWidth - 12) / 2;

            transfer = new Card { Size = new Size(innerWidth, 92) };
            transferTitle = NewText(transfer, "", 16, 6, innerWidth - 32, 22, headlineFont, Theme.Label, TLR);
            transferFile = NewText(transfer, "", 16, transferTitle.Bottom, innerWidth - 32, 20, captionFont, Theme.SecondaryLabel, TLR);
            transferBar = new ThinProgress { Location = new Point(16, transferFile.Bottom + 2), Size = new Size(innerWidth - 32, 6), Anchor = TLR };
            transfer.Controls.Add(transferBar);
            transferStats = NewText(transfer, "", 16, transferBar.Bottom + 2, innerWidth - 32, 20, captionFont, Theme.SecondaryLabel, TLR);
            transfer.Dock = DockStyle.Bottom; transfer.Visible = false;
            transferGap = NewSpacer(DockStyle.Bottom, 10); transferGap.Visible = false;

            statusBar = new Panel { Size = new Size(innerWidth, 30), BackColor = form.BackColor };
            statusText = NewText(statusBar, "", 2, 6, innerWidth - 270, 22, captionFont, Theme.SecondaryLabel, TLR);
            NewButton(statusBar, T("活動記錄"), ButtonKind.Plain, innerWidth - 96, 1, 96, () => { logWindow.Show(); logWindow.Activate(); }, 28, TR);
            // Language / theme icons: switch at once (see SetPreference).
            var iconFont = new Font("Segoe MDL2 Assets", 11);
            var tips = new ToolTip();
            var langButton = NewButton(statusBar, "", ButtonKind.Plain, innerWidth - 96 - 40, 1, 36, () => SetPreference("lang", J.S(ui, "lang") == "en" ? "zh" : "en"), 28, TR);
            langButton.Font = iconFont; tips.SetToolTip(langButton, J.S(ui, "lang") == "en" ? "切換成中文" : "Switch to English");
            var themeButton = NewButton(statusBar, J.S(ui, "theme") == "dark" ? "" : "", ButtonKind.Plain, innerWidth - 96 - 80, 1, 36, () => SetPreference("theme", J.S(ui, "theme") == "dark" ? "light" : "dark"), 28, TR);
            themeButton.Font = iconFont; tips.SetToolTip(themeButton, J.S(ui, "theme") == "dark" ? T("切換成白天") : T("切換成黑夜"));
            var settingsButton = NewButton(statusBar, "", ButtonKind.Plain, innerWidth - 96 - 120, 1, 36, ShowSettings, 28, TR);   // gear
            settingsButton.Font = iconFont; tips.SetToolTip(settingsButton, T("設定"));
            statusBar.Dock = DockStyle.Bottom;

            linuxCard = new Card { Size = new Size(innerWidth, 64) };
            dot = new PenguinBadge { Location = new Point(14, 9), Size = new Size(38, 44) };
            linuxCard.Controls.Add(dot);
            linuxTitle = NewText(linuxCard, "Linux", 62, 5, 500, 25, headlineFont);
            linuxDetail = NewText(linuxCard, T("正在檢查狀態…"), 62, linuxTitle.Bottom - 1, innerWidth - 62 - 170, 22, captionFont, Theme.SecondaryLabel, TLR);
            bootProgress = new ThinProgress { Location = new Point(62, Math.Min(57, linuxDetail.Bottom)), Size = new Size(innerWidth - 62 - 170, 5), Anchor = TLR, Visible = false };
            linuxCard.Controls.Add(bootProgress);
            linuxToggle = NewButton(linuxCard, T("啟動 Linux"), ButtonKind.Primary, innerWidth - 14 - 128, 15, 128, () =>
            {
                if (vmState == "notinstalled") { StartSetup(); return; }
                if (vmState == "running")
                {
                    if (attached.Count > 0 && MessageBox.Show(T("關閉 Linux 前會先安全退出所有隨身碟並交還 Windows。請確認沒有正在進行的複製。"), T("關閉 Linux"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                    BeginOperation(Req("action", "shutdown"));
                }
                else BeginOperation(Req("action", "start"));
            }, 34, TR);
            linuxCard.Dock = DockStyle.Top;

            // Docking order: the fill control is added first, edges after it.
            form.Controls.Add(split); split.Dock = DockStyle.Fill;
            // Auto-collapse strip for the Windows pane: hover (or drag over) to slide the pane out.
            winGap = new Panel { Dock = DockStyle.Left, Width = 8, BackColor = form.BackColor };
            winStrip = new PillButton { Text = "›", Kind = ButtonKind.Secondary, Font = glyphFont, Dock = DockStyle.Left, Width = 28, AllowDrop = true };
            winStrip.MouseEnter += (s, e) => SetWinPaneOpen(true); winStrip.DragEnter += (s, e) => SetWinPaneOpen(true); winStrip.Click += (s, e) => SetWinPaneOpen(true);
            form.Controls.Add(winGap); form.Controls.Add(winStrip);
            form.Controls.Add(transferGap); form.Controls.Add(transfer); form.Controls.Add(statusBar);
            form.Controls.Add(NewSpacer(DockStyle.Top, 10)); form.Controls.Add(linuxCard);

            logWindow = new Form { Text = T("活動記錄"), Icon = form.Icon, ShowInTaskbar = false, Size = new Size(640, 360), StartPosition = FormStartPosition.CenterParent, Owner = form };
            log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Label, Font = captionFont };
            logWindow.Controls.Add(log);
            logWindow.FormClosing += (s, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; logWindow.Hide(); } };

            smallIcons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size((int)(16 * scale), (int)(16 * scale)) };

            win = NewPane(split.Panel1, "win", T("本機"));
            lin = NewPane(split.Panel2, "lin", "Linux");
            panes = new[] { win, lin };
            active = win;

            // Windows side: blue frame and tinted background, so it is never mistaken for the Linux side.
            win.Body.BorderColor = Theme.WindowsBlue; win.Body.BorderWidth = 2; win.Body.FillColor = Theme.WindowsTint;
            win.List.BackColor = Theme.WindowsTint; win.Empty.BackColor = Theme.WindowsTint;
            var winAddress = (Card)win.Address.Parent;
            winAddress.BorderColor = Theme.WindowsBlue; win.Address.ForeColor = Theme.WindowsBlue;
            winStrip.ForeColor = Theme.WindowsBlue;
            winPin = NewButton(win.View.Parent, "", ButtonKind.Secondary, win.View.Left - 40, 2, 36, () => SetWinPin(!UiB("pin_win")), 32, TR);
            winPin.Font = new Font("Segoe MDL2 Assets", 11);
            winAddress.Width -= 40;
            pinTip = new ToolTip();
            foreach (var pane in panes) pinTip.SetToolTip(pane.Refresh, T("重新整理 (F5)"));

            BuildDragDrop();
            BuildPaneEvents();
            BuildMenus();

            deviceWatcher = new DeviceChangeWatcher(form);
            deviceWatcher.Changed += (s, e) => { lastDeviceChange = DateTime.Now; refreshWanted = true; };
            timer = new System.Windows.Forms.Timer { Interval = 250 };
            timer.Tick += (s, e) => UpdateWinAutoHide();
            timer.Tick += (s, e) => Tick();

            form.FormClosing += OnClosing;
            form.Shown += (s, e) =>
            {
                Native.StyleWindow(form.Handle, Theme.Background, Theme.Label);
                if (UiB("maximized")) form.WindowState = FormWindowState.Maximized;
                double ratio = J.D(ui, "split");
                if (ratio > 0.2 && ratio < 0.8) split.SplitterDistance = (int)(split.Width * ratio);
                SetWinPin(UiB("pin_win"));
            };
            if (UiI("width") >= 900 && UiI("height") >= 560) form.ClientSize = new Size(UiI("width"), UiI("height"));
        }

        // A pane: header (back, up, address, view) above an Explorer-style list.
        Pane NewPane(Control container, string key, string root)
        {
            int width = container.Width;
            var pane = new Pane { Key = key, Root = root };
            var body = new Card { Padding = new Padding(6), Dock = DockStyle.Fill };
            var list = new ExplorerListView { Dock = DockStyle.Fill, BackColor = Theme.Surface, Font = bodyFont };
            var large = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size((int)(48 * scale), (int)(48 * scale)) };
            list.LargeImageList = large; list.SmallImageList = smallIcons;
            body.Controls.Add(list);
            var empty = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.SecondaryLabel, BackColor = Theme.Surface, AllowDrop = true, Visible = false };
            list.Controls.Add(empty);
            var header = new Panel { Size = new Size(width, 42), BackColor = form.BackColor };
            pane.List = list; pane.Empty = empty; pane.Large = large; pane.Sorter = list.ListViewItemSorter;
            pane.Back = NewButton(header, "←", ButtonKind.Secondary, 0, 2, 36, () => GoBack(pane), 32);
            pane.Up = NewButton(header, "↑", ButtonKind.Secondary, 40, 2, 36, () => GoUp(pane), 32);
            pane.Refresh = NewButton(header, "", ButtonKind.Secondary, 80, 2, 36, () => RefreshPane(pane), 32);   // MDL2 Refresh glyph
            pane.Back.Font = glyphFont; pane.Up.Font = glyphFont; pane.Refresh.Font = new Font("Segoe MDL2 Assets", 10);
            var address = new Card { Location = new Point(122, 2), Size = new Size(width - 122 - 122, 32), Anchor = TLR };
            header.Controls.Add(address);
            pane.Address = NewText(address, root, 12, 6, width - 122 - 122 - 24, 20, bodyFont, Theme.Label, TLR);
            var view = new PopupSelect { Location = new Point(width - 114, 2), Size = new Size(114, 32), Anchor = TR };
            view.SetItems(new[] { T("大圖示"), T("清單"), T("詳細資料") });
            header.Controls.Add(view);
            pane.View = view;
            header.Dock = DockStyle.Top;
            container.Controls.Add(body); container.Controls.Add(header);
            pane.Body = body;
            return pane;
        }

        // Windows pane: pinned (always shown) or auto-collapse (hidden until the strip is hovered).
        void SetWinPaneOpen(bool open)
        {
            split.Panel1Collapsed = !open;
            winStrip.Text = open ? "‹" : "›";
            winAwaySince = null;
        }
        void SetWinPin(bool pinned)
        {
            ui["pin_win"] = pinned;
            winPin.Text = pinned ? "" : "";   // Pinned / Unpin glyphs
            winPin.Kind = pinned ? ButtonKind.Primary : ButtonKind.Secondary; winPin.Invalidate();
            winStrip.Visible = !pinned; winGap.Visible = !pinned;
            pinTip.SetToolTip(winPin, pinned ? T("已釘選：按一下改為自動摺疊") : T("自動摺疊：按一下釘選"));
            SetWinPaneOpen(pinned);
        }
        // Called from the UI timer: collapse once the cursor has left the pane/strip for a moment.
        void UpdateWinAutoHide()
        {
            if (UiB("pin_win") || split.Panel1Collapsed) return;
            var cursor = Cursor.Position;
            bool inside = split.Panel1.RectangleToScreen(split.Panel1.ClientRectangle).Contains(cursor) || winStrip.RectangleToScreen(winStrip.ClientRectangle).Contains(cursor);
            // Never collapse mid-drag, with the context menu open, or while a dialog is up.
            bool held = Control.MouseButtons != MouseButtons.None || winMenu.Visible || Form.ActiveForm != form;
            if (inside || held) { winAwaySince = null; return; }
            if (winAwaySince == null) { winAwaySince = DateTime.Now; return; }
            if ((DateTime.Now - winAwaySince.Value).TotalMilliseconds >= 600) SetWinPaneOpen(false);
        }

        // Linux CPU / memory: saved to ui.json and sent with every start; VirtualBox applies them while the VM is off.
        void ShowSettings()
        {
            bool changed;
            using (var dialog = new Form
            {
                Text = T("設定"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false, Font = bodyFont,
                AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
                BackColor = Theme.Background, ForeColor = Theme.Label, ClientSize = new Size(420, 544)
            })
            {
                NewText(dialog, T("Linux 虛擬機"), 20, 14, 380, 24, headlineFont);
                NewText(dialog, T("CPU 數量"), 20, 54, 150, 24);
                var cpuBox = new NumericUpDown { Minimum = 1, Maximum = Math.Max(1, Math.Min(8, Environment.ProcessorCount)), Location = new Point(180, 52), Width = 90 };
                cpuBox.Value = Math.Min(cpuBox.Maximum, Math.Max(1, UiI("cpus")));
                dialog.Controls.Add(cpuBox);
                NewText(dialog, T("記憶體（MB）"), 20, 90, 150, 24);
                var memBox = new NumericUpDown { Minimum = 512, Maximum = 8192, Increment = 256, Location = new Point(180, 88), Width = 90 };
                memBox.Value = Math.Min(8192, Math.Max(512, UiI("memory")));
                dialog.Controls.Add(memBox);
                NewText(dialog, T("下次啟動 Linux 時套用。這台電腦開著 Hyper-V，CPU 多於 1 顆時偶爾會開機卡住（程式會自動重試一次）；卡住請改回 1。"), 20, 124, 380, 52, captionFont, Theme.SecondaryLabel);
                var autoBox = new CheckBox { Text = T("插入隨身碟時自動切換到 Linux"), Checked = UiB("autoLinux"), Location = new Point(20, 184), Size = new Size(380, 26) };
                dialog.Controls.Add(autoBox);
                var shutdownBox = new CheckBox { Text = T("關閉工具時一併關閉 Linux（記住的隨身碟下次開啟會自動接上）"), Checked = UiB("shutdownOnClose"), Location = new Point(20, 212), Size = new Size(390, 26) };
                dialog.Controls.Add(shutdownBox);
                NewText(dialog, T("記住的隨身碟（插上就直接進 Linux；按「退出」會忘記該支）"), 20, 246, 380, 22, captionFont, Theme.SecondaryLabel);
                var keepList = new ListBox { Location = new Point(20, 270), Size = new Size(280, 84), IntegralHeight = false };
                foreach (var d in remembered) keepList.Items.Add((J.S(d, "name") != "" ? J.S(d, "name") + "  " : "") + "(" + J.S(d, "serial") + ")");
                if (remembered.Count == 0) { keepList.Items.Add(T("（尚無，切換到 Linux 一次就會記住）")); keepList.Enabled = false; }
                dialog.Controls.Add(keepList);
                var forgetSerials = new List<string>();
                var shown = remembered.Select(d => J.S(d, "serial")).ToList();
                var forgetButton = new Button { Text = T("移除"), Location = new Point(316, 270), Size = new Size(90, 30), Enabled = remembered.Count > 0 };
                forgetButton.Click += (s, e) =>
                {
                    int i = keepList.SelectedIndex;
                    if (i < 0 || !keepList.Enabled) return;
                    forgetSerials.Add(shown[i]); shown.RemoveAt(i); keepList.Items.RemoveAt(i);
                };
                dialog.Controls.Add(forgetButton);
                var updateBox = new CheckBox { Text = T("開啟時自動檢查更新"), Checked = UiB("autoUpdate"), Location = new Point(20, 394), Size = new Size(380, 26) };
                dialog.Controls.Add(updateBox);
                NewText(dialog, T("目前版本") + " " + Updater.Current, 20, 434, 200, 24, captionFont, Theme.SecondaryLabel);
                var checkNow = new Button { Text = T("檢查更新"), Location = new Point(220, 430), Size = new Size(186, 30) };
                checkNow.Click += (s, e) => { dialog.Close(); CheckUpdate(true); };
                dialog.Controls.Add(checkNow);
                var ok = new Button { Text = T("確定"), DialogResult = DialogResult.OK, Location = new Point(220, 498), Size = new Size(90, 30) };
                var cancel = new Button { Text = T("取消"), DialogResult = DialogResult.Cancel, Location = new Point(316, 498), Size = new Size(90, 30) };
                dialog.Controls.Add(ok); dialog.Controls.Add(cancel); dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(form) != DialogResult.OK) return;
                changed = (int)cpuBox.Value != UiI("cpus") || (int)memBox.Value != UiI("memory");
                ui["cpus"] = (int)cpuBox.Value; ui["memory"] = (int)memBox.Value; ui["autoLinux"] = autoBox.Checked; ui["autoUpdate"] = updateBox.Checked; ui["shutdownOnClose"] = shutdownBox.Checked;
                if (autoBox.Checked) { autoSkip.Clear(); refreshWanted = true; }
                else if (remembered.Count > 0) refreshWanted = true;   // the next status forgets every remembered drive
                if (forgetSerials.Count > 0)
                {
                    if (busy) MessageBox.Show(T("目前有工作進行中，請稍後再移除。"), "USB Proxy");
                    else BeginOperation(Req("action", "forget", "serials", forgetSerials.ToArray()));
                }
                SaveUi();
            }
            if (!changed || vmState != "running") return;
            if (busy || attached.Count > 0) { MessageBox.Show(T("已儲存，下次啟動 Linux 時套用。"), "USB Proxy"); return; }
            if (MessageBox.Show(T("已儲存。要立即重新啟動 Linux 套用嗎？"), "USB Proxy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                restartAfter = true; BeginOperation(Req("action", "shutdown"));
            }
        }
        // GitHub releases: checked in the background; installing replaces the program files and reopens the window.
        bool checkingUpdate;
        void CheckUpdate(bool manual)
        {
            if (checkingUpdate) return;
            checkingUpdate = true;
            if (manual) LogLine(T("正在檢查更新…"));
            new Thread(() =>
            {
                Map info = null; string error = null;
                try { info = Updater.Check(); } catch (Exception ex) { error = ex.Message; }
                form.BeginInvoke((Action)(() => { checkingUpdate = false; OfferUpdate(info, error, manual); }));
            }) { IsBackground = true }.Start();
        }
        void OfferUpdate(Map info, string error, bool manual)
        {
            if (error != null)
            {
                LogLine(T("無法檢查更新：") + error);
                if (manual) MessageBox.Show(form, T("無法檢查更新：") + error, "USB Proxy");
                return;
            }
            if (info == null) { if (manual) MessageBox.Show(form, T("已是最新版本") + "（" + Updater.Current + "）", "USB Proxy"); return; }
            string notes = J.S(info, "notes");
            if (notes.Length > 600) notes = notes.Substring(0, 600) + "…";
            if (MessageBox.Show(form, string.Format(T("有新版本 {0}（目前 {1}）。要現在更新嗎？更新後程式會自動重新開啟。"), J.S(info, "version"), Updater.Current) + "\n\n" + notes,
                "USB Proxy", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            if (busy || attached.Count > 0) { MessageBox.Show(form, T("請先等工作完成，並退出 Linux 中的隨身碟，再到「設定」按「檢查更新」。"), "USB Proxy"); return; }
            pendingAction = "update"; SetBusy(true);
            LogLine(T("正在下載並安裝更新…"));
            new Thread(() =>
            {
                string failed = null;
                try { Updater.Install(info); } catch (Exception ex) { failed = ex.Message; }
                form.BeginInvoke((Action)(() =>
                {
                    pendingAction = ""; SetBusy(false);
                    if (failed != null) { LogLine(T("更新失敗：") + failed); MessageBox.Show(form, T("更新失敗：") + failed, "USB Proxy"); return; }
                    relaunch = true; form.Close();
                }));
            }) { IsBackground = true }.Start();
        }
        void SetPreference(string key, string value)
        {
            // Colours and texts are set when controls are created, so an identical window is built with the new
            // palette/language and swapped in. Everything else (Linux, drives, open folders, history) lives in this
            // object and carries over. Not during a job: its progress card belongs to the old window.
            if (busy) { MessageBox.Show(T("目前有工作進行中，完成後再切換。"), "USB Proxy"); return; }
            ui[key] = value;
            SaveWindowState();
            // After the click that got us here has finished with the old window.
            form.BeginInvoke((Action)SwapWindow);
        }
        void SwapWindow()
        {
            Core.UseLanguage(J.S(ui, "lang"));
            BuildNames();
            var old = form; var oldTimer = timer; string oldLog = log.Text;
            bool logShown = logWindow.Visible;
            Loc winLoc = win.Location, linLoc = lin.Location;
            List<Loc> winHistory = win.History, linHistory = lin.History, winForward = win.Forward, linForward = lin.Forward;
            old.FormClosing -= OnClosing;
            oldTimer.Stop();
            Build();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = old.WindowState == FormWindowState.Normal ? old.Location : old.RestoreBounds.Location;
            log.Text = oldLog;
            win.History = winHistory; lin.History = linHistory; win.Forward = winForward; lin.Forward = linForward;
            ShowWinComputer(); ShowLinComputer(); UpdateState();
            if (winLoc.Kind == "folder") Navigate(win, winLoc, true);
            if (linLoc.Kind == "folder") Navigate(lin, linLoc, true);
            // Invisible until maximize, splitter and pin (Build's Shown handler) are done and it has painted once;
            // then it appears in one step over the old window, which closes behind it.
            form.Opacity = 0;
            form.Shown += (s, e) => form.BeginInvoke((Action)(() =>
            {
                form.Update();
                form.Opacity = 1;
                if (appContext != null) appContext.MainForm = form;
                old.Close(); old.Dispose(); oldTimer.Dispose();
                timer.Start();
                if (logShown) logWindow.Show();
            }));
            form.Show();
        }

        // ---------------- Helpers ----------------
        void LogLine(string text) { log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "   " + text + "\r\n"); }
        void SetMessage(string text) { statusText.Text = text; }
        static string FormatSize(double bytes)
        {
            if (bytes < 1024) return string.Format(T("{0} 位元組"), bytes);
            if (bytes < 1048576) return string.Format("{0:N1} KB", bytes / 1024);
            if (bytes < 1073741824) return string.Format("{0:N1} MB", bytes / 1048576);
            return string.Format("{0:N1} GB", bytes / 1073741824);
        }
        static string FormatDuration(int seconds)
        {
            if (seconds >= 3600) return string.Format(T("{0} 小時 {1} 分"), seconds / 3600, (seconds % 3600) / 60);
            if (seconds >= 60) return string.Format(T("{0} 分 {1:00} 秒"), seconds / 60, seconds % 60);
            return string.Format(T("{0} 秒"), seconds);
        }
        static string ParentPath(string path) { int i = path.LastIndexOf('/'); return i < 0 ? "" : path.Substring(0, i); }
        static string JoinRemote(string folder, string name) { return folder != "" ? folder + "/" + name : name; }
        static List<FileEntry> Selected(Pane pane) { return pane.List.SelectedItems.Cast<ListViewItem>().Select(i => (FileEntry)i.Tag).ToList(); }
        string VolumeLabel(string device)
        {
            var v = volumes.FirstOrDefault(x => J.S(x, "path") == device);
            return v != null && J.S(v, "label") != "" ? J.S(v, "label") : T("USB 隨身碟");
        }
        // The attached drive a Linux volume belongs to (matched by the serial Linux reports).
        Map DriveOf(Map volume)
        {
            var match = attached.FirstOrDefault(d => J.S(d, "serial") != "" && J.S(d, "serial") == J.S(volume, "serial"));
            if (match != null) return match;
            return attached.Count == 1 ? attached[0] : null;
        }
        string TypeName(string name, bool folder)
        {
            string key = folder ? "~folder" : Path.GetExtension(name).ToLowerInvariant();
            if (!typeNames.ContainsKey(key)) typeNames[key] = ShellIcons.TypeName(name, folder);
            return typeNames[key];
        }
        static string DeviceName(Map d)
        {
            return string.Join(" ", new[] { J.S(d, "Manufacturer"), J.S(d, "Product") }.Where(x => x.Trim() != "").Select(x => x.Trim()));
        }

        // Icons come from the Windows shell, cached per extension (or per path for drives and special folders).
        string IconKey(FileEntry entry, Pane pane)
        {
            string key;
            switch (entry.Kind)
            {
                case "device": key = "~device"; break;
                case "volume": case "linux-device": key = "~volume"; break;
                case "drive": case "usb-drive": case "special": key = "path:" + entry.Path; break;
                case "folder": key = "~folder"; break;
                default: { string ext = Path.GetExtension(entry.Name).ToLowerInvariant(); key = ext != "" ? ext : "~file"; break; }
            }
            foreach (var images in new[] { pane.Large, smallIcons })
            {
                if (images.Images.ContainsKey(key)) continue;
                int size = images.ImageSize.Width;
                Image image;
                if (key == "~device") image = ShellIcons.Stock(ShellIcons.DriveRemovable, size);
                else if (key == "~volume") image = ShellIcons.WithBadge(ShellIcons.Stock(ShellIcons.DriveRemovable, size), penguin);
                else if (key.StartsWith("path:")) image = ShellIcons.ForPath(key.Substring(5), size);
                else if (key == "~folder") image = ShellIcons.ForFile("folder", true, size);
                else if (key == "~file") image = ShellIcons.ForFile("file", false, size);
                else image = ShellIcons.ForFile("file" + key, false, size);
                images.Images.Add(key, image);
            }
            return key;
        }
        ListViewItem NewRow(FileEntry entry, Pane pane, string column1, string column3)
        {
            var item = new ListViewItem(entry.Name, IconKey(entry, pane));
            item.SubItems.Add(column1); item.SubItems.Add(entry.TypeName ?? ""); item.SubItems.Add(column3);
            item.Tag = entry;
            return item;
        }
        void SetItems(Pane pane, List<ListViewItem> items, string[] columns, int[] widths, bool sorted)
        {
            var list = pane.List;
            list.BeginUpdate();
            list.ListViewItemSorter = null;
            list.Items.Clear();
            list.Columns.Clear();
            for (int i = 0; i < columns.Length; i++)
            {
                var column = list.Columns.Add(columns[i], (int)(widths[i] * scale));
                if (i == 3) column.TextAlign = HorizontalAlignment.Right;
            }
            if (items.Count > 0) list.Items.AddRange(items.ToArray());
            // 本機 keeps its natural order; folders sort like Explorer.
            if (sorted) list.ListViewItemSorter = pane.Sorter;
            list.EndUpdate();
        }
        static void SetEmpty(Pane pane, string text) { pane.Empty.Text = text; pane.Empty.Visible = text != ""; }
        static FileEntry NewEntry(string kind, string name, string path, string typeName) { return new FileEntry { Kind = kind, Name = name, Path = path, TypeName = typeName }; }

        // ---------------- Windows pane ----------------
        void ShowWinComputer()
        {
            var items = new List<ListViewItem>();
            var special = new[] {
                new[] { T("桌面"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) },
                new[] { T("下載"), Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "Downloads") },
                new[] { T("文件"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) },
                new[] { T("圖片"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) } };
            foreach (var s in special)
                if (s[1] != "" && Directory.Exists(s[1])) items.Add(NewRow(NewEntry("special", s[0], s[1], T("資料夾")), win, "", ""));
            var letters = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in usb) foreach (var l in J.A(d, "drives")) if (Convert.ToString(l) != "") letters[Convert.ToString(l)] = d;
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;
                string letter = drive.Name.TrimEnd('\\');
                string label = drive.VolumeLabel != "" ? drive.VolumeLabel : drive.DriveType == DriveType.Fixed ? T("本機磁碟") : T("磁碟");
                Map usbDevice; letters.TryGetValue(letter, out usbDevice);
                var entry = NewEntry(usbDevice != null ? "usb-drive" : "drive", string.Format("{0} ({1})", label, letter), drive.Name, "");
                entry.TypeName = usbDevice != null ? T("USB 隨身碟 · 可切換到 Linux") : ShellIcons.PathTypeName(drive.Name);
                entry.Size = drive.TotalSize;
                if (usbDevice != null) { entry.Uuid = J.S(usbDevice, "uuid"); entry.Device = entry.Name; }
                items.Add(NewRow(entry, win, FormatSize(drive.AvailableFreeSpace) + T(" 可用"), FormatSize(drive.TotalSize)));
            }
            // Drives Windows cannot mount (no letter) can still be switched to Linux.
            foreach (var d in usb)
            {
                if (J.A(d, "drives").Count > 0) continue;
                string name = DeviceName(d);
                string type = J.B(d, "stuck") ? T("USB 隨身碟 · VirtualBox 接管卡住 · 請拔下再插回") : T("USB 隨身碟 · Windows 無法存取 · 可切換到 Linux");
                var entry = NewEntry("device", name != "" ? name : T("USB 隨身碟"), "", type);
                entry.Uuid = J.S(d, "uuid"); entry.Device = entry.Name;
                items.Add(NewRow(entry, win, "", ""));
            }
            SetItems(win, items, new[] { T("名稱"), T("可用空間"), T("類型"), T("總大小") }, new[] { 190, 110, 170, 80 }, false);
            SetEmpty(win, "");
            win.Address.Text = T("本機");
        }
        void ShowWinFolder(string path)
        {
            var directory = new DirectoryInfo(path);
            var hidden = FileAttributes.Hidden | FileAttributes.System;
            var items = new List<ListViewItem>();
            foreach (var child in directory.EnumerateFileSystemInfos().Where(c => (c.Attributes & hidden) == 0))
            {
                bool folder = (child.Attributes & FileAttributes.Directory) != 0;
                var entry = NewEntry(folder ? "folder" : "file", child.Name, child.FullName, TypeName(child.Name, folder));
                entry.Modified = child.LastWriteTime;
                if (!folder) entry.Size = ((FileInfo)child).Length;
                items.Add(NewRow(entry, win, entry.Modified.ToString("yyyy/MM/dd HH:mm"), folder ? "" : FormatSize(entry.Size)));
            }
            SetItems(win, items, new[] { T("名稱"), T("修改日期"), T("類型"), T("大小") }, new[] { 210, 130, 130, 80 }, true);
            SetEmpty(win, items.Count > 0 ? "" : T("這個資料夾是空的\n\n把右側 Linux 的檔案拖到這裡即可複製到電腦"));
            win.Address.Text = T("本機  ›  ") + string.Join("  ›  ", path.TrimEnd('\\').Split('\\').Where(x => x != ""));
        }

        // ---------------- Linux pane ----------------
        void ShowLinComputer()
        {
            var items = new List<ListViewItem>();
            foreach (var v in volumes)
            {
                string label = J.S(v, "label") != "" ? J.S(v, "label") : T("USB 隨身碟");
                var drive = DriveOf(v);
                var entry = NewEntry("volume", string.Format("{0} ({1})", label, J.S(v, "fstype").ToUpperInvariant()), "", "");
                entry.Device = J.S(v, "path"); entry.Size = J.L(v, "size");
                entry.TypeName = T("USB 隨身碟") + (drive != null && J.S(drive, "name") != "" ? " · " + J.S(drive, "name") : "");
                if (drive != null) entry.Uuid = J.S(drive, "uuid");
                string free = volumeInfo.ContainsKey(entry.Device) ? FormatSize(volumeInfo[entry.Device][0]) + T(" 可用") : "";
                items.Add(NewRow(entry, lin, free, FormatSize(J.L(v, "size"))));
            }
            foreach (var drive in attached)
            {
                if (volumes.Any(v => { var d = DriveOf(v); return d != null && J.S(d, "uuid") == J.S(drive, "uuid"); })) continue;
                var entry = NewEntry("linux-device", J.S(drive, "name") != "" ? J.S(drive, "name") : T("USB 隨身碟"), "", T("找不到支援的分割區"));
                entry.Uuid = J.S(drive, "uuid");
                items.Add(NewRow(entry, lin, "", ""));
            }
            SetItems(lin, items, new[] { T("名稱"), T("可用空間"), T("類型"), T("總大小") }, new[] { 190, 110, 170, 80 }, false);
            string message = items.Count > 0 ? ""
                : vmState == "notinstalled" ? T("尚未安裝 Linux 環境\n\n按上方「安裝」開始（約 3～5 分鐘，不需要網路）")
                : vmState != "running" ? T("Linux 未啟動\n\n在左側按兩下隨身碟，即可切換到 Linux（會自動啟動）")
                : T("沒有隨身碟在 Linux 中\n\n在左側按兩下隨身碟，即可切換到這裡");
            SetEmpty(lin, message);
            lin.Address.Text = "Linux";
        }
        void ShowLinFolder(Map data, string device)
        {
            var items = new List<ListViewItem>();
            string folderPath = J.S(data, "path");
            foreach (var item in J.Maps(data, "entries"))
            {
                string kind = J.B(item, "link") ? "link" : J.B(item, "dir") ? "folder" : "file";
                var entry = NewEntry(kind, J.S(item, "name"), JoinRemote(folderPath, J.S(item, "name")), "");
                entry.Device = device; entry.Size = J.L(item, "size");
                entry.Modified = DateTimeOffset.FromUnixTimeSeconds(J.L(item, "mtime")).LocalDateTime;
                entry.TypeName = entry.IsLink ? T("符號連結（不支援）") : TypeName(entry.Name, entry.IsFolder);
                items.Add(NewRow(entry, lin, entry.Modified.ToString("yyyy/MM/dd HH:mm"), kind == "file" ? FormatSize(entry.Size) : ""));
            }
            SetItems(lin, items, new[] { T("名稱"), T("修改日期"), T("類型"), T("大小") }, new[] { 210, 130, 130, 80 }, true);
            SetEmpty(lin, items.Count > 0 ? "" : T("這個資料夾是空的\n\n把左側或檔案總管的檔案拖到這裡即可複製到隨身碟"));
            volumeInfo[device] = new[] { J.L(data, "free"), J.L(data, "total") };
            lin.Address.Text = string.Join("  ›  ", new[] { "Linux", VolumeLabel(device) }.Concat(folderPath.Split('/').Where(x => x != "")));
            if (editAfter != null)
            {
                foreach (ListViewItem item in lin.List.Items)
                    if (item.Text == editAfter) { lin.List.SelectedItems.Clear(); item.Selected = true; item.Focused = true; item.EnsureVisible(); item.BeginEdit(); }
                editAfter = null;
            }
        }

        // ---------------- Navigation ----------------
        void Navigate(Pane pane, Loc target, bool noHistory = false)
        {
            if (pane == lin && target.Kind == "folder")
            {
                if (busy) return;
                // Linux folders are read by the worker; the view switches when the listing arrives.
                pendingNav = true; pendingNavPush = !noHistory;
                BeginOperation(Req("action", "list", "device", target.Device, "path", target.Path));
                return;
            }
            try
            {
                if (target.Kind == "pc") { if (pane == win) ShowWinComputer(); else ShowLinComputer(); }
                else ShowWinFolder(target.Path);
            }
            catch (Exception e) { MessageBox.Show(T("無法開啟：") + e.Message, "USB Proxy"); return; }
            if (!noHistory && !pane.Location.Same(target)) { pane.History.Add(pane.Location); pane.Forward.Clear(); }
            pane.Location = target;
            UpdateCounts(); UpdateState();
        }
        void GoBack(Pane pane)
        {
            if (pane.History.Count == 0) return;
            if (pane == lin && busy) return;
            var target = pane.History[pane.History.Count - 1];
            pane.History.RemoveAt(pane.History.Count - 1);
            pane.Forward.Add(pane.Location);
            Navigate(pane, target, true);
        }
        void GoForward(Pane pane)
        {
            if (pane.Forward.Count == 0) return;
            if (pane == lin && busy) return;
            var target = pane.Forward[pane.Forward.Count - 1];
            pane.Forward.RemoveAt(pane.Forward.Count - 1);
            pane.History.Add(pane.Location);
            Navigate(pane, target, true);
        }
        void GoUp(Pane pane)
        {
            var location = pane.Location;
            if (location.Kind != "folder") return;
            if (pane == win)
            {
                var parent = new DirectoryInfo(location.Path.TrimEnd('\\') + "\\").Parent;
                Navigate(win, parent == null ? Loc.Pc() : Loc.WinFolder(parent.FullName));
            }
            else if (location.Path != "") Navigate(lin, Loc.LinFolder(location.Device, ParentPath(location.Path)));
            else Navigate(lin, Loc.Pc());
        }
        void RefreshPane(Pane pane)
        {
            if (pane.Location.Kind == "pc") BeginOperation(Req("action", "status"));
            else Navigate(pane, pane.Location, true);
        }
        void ResetLin()
        {
            lin.History.Clear(); lin.Forward.Clear();
            lin.Location = Loc.Pc();
            ShowLinComputer();
        }

        void UpdateCounts()
        {
            var pane = active;
            var parts = new List<string> { string.Format(T("{0}：{1} 個項目"), pane == win ? "Windows" : "Linux", pane.List.Items.Count) };
            if (pane.List.SelectedItems.Count > 0) parts.Add(string.Format(T("已選取 {0} 個"), pane.List.SelectedItems.Count));
            if (pane == lin && pane.Location.Kind == "folder" && volumeInfo.ContainsKey(pane.Location.Device))
            {
                var v = volumeInfo[pane.Location.Device];
                parts.Add(string.Format(T("可用 {0}，共 {1}"), FormatSize(v[0]), FormatSize(v[1])));
            }
            if (linuxClip != null) parts.Add(string.Format(T("剪貼簿：{0} 個 Linux 項目"), linuxClip.Paths.Count));
            SetMessage(string.Join("  ·  ", parts));
        }

        void UpdateState()
        {
            bool idle = !busy;
            bool running = vmState == "running";
            bool starting = busy && In(pendingAction, "start", "setup");
            switch (vmState)
            {
                case "running":
                    if (ready) { linuxTitle.Text = T("Linux 已就緒"); dot.BadgeColor = Theme.Green; }
                    else { linuxTitle.Text = T("Linux 啟動中"); dot.BadgeColor = Theme.Orange; }
                    break;
                case "stopping": linuxTitle.Text = T("Linux 正在關機"); dot.BadgeColor = Theme.Orange; break;
                case "poweroff": linuxTitle.Text = T("Linux 未啟動"); dot.BadgeColor = Theme.Gray; break;
                case "unknown": linuxTitle.Text = T("正在連線 Linux…"); dot.BadgeColor = Theme.Gray; break;
                case "notinstalled": linuxTitle.Text = T("尚未安裝 Linux 環境"); dot.BadgeColor = Theme.Orange; break;
                case "error": linuxTitle.Text = T("Linux 無法使用（詳見活動記錄）"); dot.BadgeColor = Theme.Red; break;
                default: linuxTitle.Text = T("Linux 狀態：") + vmState; dot.BadgeColor = Theme.Gray; break;
            }
            if (starting) { linuxTitle.Text = pendingAction == "setup" ? T("正在安裝 Linux 環境") : T("Linux 啟動中"); dot.BadgeColor = Theme.Orange; }
            dot.Dimmed = !running && !starting;
            if (!starting)
            {
                int count = attached.Count;
                if (count > 1) linuxDetail.Text = string.Format(T("{0} 支隨身碟在 Linux 中，可在右側互相傳輸；Windows 暫時無法使用它們"), count);
                else if (count == 1) linuxDetail.Text = string.Format(T("「{0}」在 Linux 中，Windows 暫時無法使用"), J.S(attached[0], "name") != "" ? J.S(attached[0], "name") : T("隨身碟"));
                else if (running) linuxDetail.Text = T("在左側按兩下隨身碟，即可切換到 Linux 管理");
                else if (vmState == "poweroff") linuxDetail.Text = T("在左側按兩下隨身碟時會自動啟動");
                else if (vmState == "notinstalled") linuxDetail.Text = T("首次使用：按「安裝」設定 VirtualBox 與 Linux（約 3～5 分鐘，可離線）");
            }
            if (vmState == "notinstalled") { linuxToggle.Text = T("安裝"); linuxToggle.Kind = ButtonKind.Primary; }
            else if (running) { linuxToggle.Text = T("關閉 Linux"); linuxToggle.Kind = ButtonKind.Secondary; }
            else { linuxToggle.Text = T("啟動 Linux"); linuxToggle.Kind = ButtonKind.Primary; }
            linuxToggle.Enabled = idle && vmState != "stopping";
            foreach (var pane in panes)
            {
                bool free = idle || pane == win;
                pane.Back.Enabled = pane.History.Count > 0 && free;
                pane.Up.Enabled = pane.Location.Kind == "folder" && free;
            }
        }
    }
}
