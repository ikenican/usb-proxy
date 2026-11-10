// Custom-drawn WinForms controls in the style of Apple's Human Interface Guidelines.
// Compiled at startup by Add-Type (C# 5, .NET Framework 4.7+), so avoid newer syntax.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace UsbProxyUi
{
    public static class Theme
    {
        public static Color Background = Color.FromArgb(245, 245, 247);
        public static Color Surface = Color.White;
        public static Color Label = Color.FromArgb(29, 29, 31);
        public static Color SecondaryLabel = Color.FromArgb(110, 110, 115);
        public static Color TertiaryLabel = Color.FromArgb(160, 160, 165);
        public static Color Separator = Color.FromArgb(226, 226, 231);
        public static Color Fill = Color.FromArgb(242, 242, 247);
        public static Color FillHover = Color.FromArgb(232, 232, 237);
        public static Color FillPressed = Color.FromArgb(220, 220, 226);
        public static Color Accent = Color.FromArgb(0, 122, 255);
        public static Color AccentHover = Color.FromArgb(0, 110, 235);
        public static Color AccentPressed = Color.FromArgb(0, 94, 204);
        public static Color AccentTint = Color.FromArgb(232, 241, 255);
        public static Color Green = Color.FromArgb(52, 199, 89);
        public static Color Orange = Color.FromArgb(255, 159, 10);
        public static Color Red = Color.FromArgb(255, 59, 48);
        public static Color Gray = Color.FromArgb(174, 174, 178);

        public static bool IsDark;
        // Identity colour of the Windows side, so it never looks like the Linux side.
        public static Color WindowsBlue = Color.FromArgb(0, 120, 212);
        public static Color WindowsTint = Color.FromArgb(238, 245, 255);

        // Night palette (Apple dark-mode system colours). Call before any control is created.
        public static void UseDark()
        {
            IsDark = true;
            Background = Color.FromArgb(28, 28, 30);
            Surface = Color.FromArgb(44, 44, 46);
            Label = Color.FromArgb(242, 242, 247);
            SecondaryLabel = Color.FromArgb(160, 160, 166);
            TertiaryLabel = Color.FromArgb(110, 110, 116);
            Separator = Color.FromArgb(58, 58, 60);
            Fill = Color.FromArgb(58, 58, 60);
            FillHover = Color.FromArgb(72, 72, 74);
            FillPressed = Color.FromArgb(86, 86, 88);
            Accent = Color.FromArgb(10, 132, 255);
            AccentHover = Color.FromArgb(40, 150, 255);
            AccentPressed = Color.FromArgb(0, 110, 225);
            AccentTint = Color.FromArgb(22, 50, 84);
            Gray = Color.FromArgb(99, 99, 102);
            WindowsBlue = Color.FromArgb(76, 160, 255);
            WindowsTint = Color.FromArgb(30, 40, 56);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // The colour a control visually sits on (cards paint a white surface over a grey BackColor).
        public static Color SurfaceOf(Control c)
        {
            var card = c.Parent as Card;
            if (card != null) return card.FillColor;
            return c.Parent != null ? c.Parent.BackColor : Background;
        }

        public static Color Blend(Color from, Color to, float amount)
        {
            return Color.FromArgb(
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        public static float Scale(Control c) { return c.DeviceDpi / 96f; }
    }

    public static class Native
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public static void EnableDpiAwareness()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        // Windows 11: tint the title bar to match the window and keep rounded corners.
        public static void StyleWindow(IntPtr hwnd, Color caption, Color text)
        {
            try
            {
                int color = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref color, 4);
                int textColor = text.R | (text.G << 8) | (text.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref textColor, 4);
                int dark = Theme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                int round = 2;
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            }
            catch { }
        }
    }

    public class Card : Panel
    {
        Color fill = Theme.Surface;
        public Color FillColor { get { return fill; } set { fill = value; Invalidate(); } }
        // Empty = the standard separator line.
        public Color BorderColor { get; set; }
        public float BorderWidth = 1;

        public Card()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Background;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Theme.Scale(this);
            float w = BorderWidth * s, h = w / 2;
            using (var path = Theme.RoundRect(new RectangleF(h, h, Width - w - 1, Height - w - 1), 12 * s))
            using (var brush = new SolidBrush(fill))
            using (var pen = new Pen(BorderColor.IsEmpty ? Theme.Separator : BorderColor, w))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }
        }
    }

    public enum ButtonKind { Primary, Secondary, Plain, Destructive }

    public class PillButton : Control
    {
        bool hover, pressed;
        ButtonKind kind = ButtonKind.Secondary;
        public ButtonKind Kind { get { return kind; } set { kind = value; Invalidate(); } }

        public PillButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = pressed = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = Theme.SurfaceOf(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Theme.Scale(this);
            Color fill = Color.Empty, text;
            switch (kind)
            {
                case ButtonKind.Primary:
                    fill = pressed ? Theme.AccentPressed : hover ? Theme.AccentHover : Theme.Accent;
                    text = Color.White;
                    break;
                case ButtonKind.Secondary:
                    fill = pressed ? Theme.FillPressed : hover ? Theme.FillHover : Theme.Fill;
                    text = Theme.Label;
                    break;
                case ButtonKind.Destructive:
                    if (hover) fill = Theme.Fill;
                    text = Theme.Red;
                    break;
                default:
                    if (hover) fill = Theme.Fill;
                    text = pressed ? Theme.AccentPressed : Theme.Accent;
                    break;
            }
            if (!Enabled)
            {
                if (!fill.IsEmpty) fill = Theme.Blend(surface, fill, kind == ButtonKind.Primary ? 0.35f : 0.7f);
                text = kind == ButtonKind.Primary ? Color.White : Theme.TertiaryLabel;
            }
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Math.Min(Height / 2f, 9 * s);
            if (!fill.IsEmpty)
            {
                using (var path = Theme.RoundRect(rect, radius))
                using (var brush = new SolidBrush(fill))
                    g.FillPath(brush, path);
            }
            if (Focused && ShowFocusCues)
            {
                using (var path = Theme.RoundRect(new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), radius))
                using (var pen = new Pen(Color.FromArgb(140, Theme.Accent), 2 * s))
                    g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    // Linux penguin with a status badge in the lower-right corner.
    public class PenguinBadge : Control
    {
        Color badge = Theme.Gray;
        bool dimmed;
        public Color BadgeColor { get { return badge; } set { badge = value; Invalidate(); } }
        public bool Dimmed { get { return dimmed; } set { dimmed = value; Invalidate(); } }

        public PenguinBadge()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        Color Tone(Color c)
        {
            if (!dimmed) return c;
            int gray = (int)(c.R * 0.3 + c.G * 0.59 + c.B * 0.11);
            return Theme.Blend(Color.FromArgb(gray, gray, gray), Theme.Surface, 0.35f);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Color surface = Theme.SurfaceOf(this);
            g.Clear(surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float w = Width, h = Height;
            Color black = Tone(Color.FromArgb(32, 32, 36));
            Color white = Tone(Color.FromArgb(250, 250, 250));
            Color yellow = Tone(Color.FromArgb(255, 190, 30));
            using (var bodyBrush = new SolidBrush(black))
            using (var bellyBrush = new SolidBrush(white))
            using (var beakBrush = new SolidBrush(yellow))
            {
                // Feet sit behind the body.
                g.FillEllipse(beakBrush, w * 0.12f, h * 0.82f, w * 0.34f, h * 0.15f);
                g.FillEllipse(beakBrush, w * 0.54f, h * 0.82f, w * 0.34f, h * 0.15f);
                // Body and head.
                g.FillEllipse(bodyBrush, w * 0.16f, h * 0.24f, w * 0.68f, h * 0.66f);
                g.FillEllipse(bodyBrush, w * 0.26f, h * 0.02f, w * 0.48f, h * 0.40f);
                // Flippers.
                g.FillEllipse(bodyBrush, w * 0.06f, h * 0.40f, w * 0.22f, h * 0.38f);
                g.FillEllipse(bodyBrush, w * 0.72f, h * 0.40f, w * 0.22f, h * 0.38f);
                // Belly.
                g.FillEllipse(bellyBrush, w * 0.27f, h * 0.36f, w * 0.46f, h * 0.50f);
                // Eyes.
                g.FillEllipse(bellyBrush, w * 0.36f, h * 0.11f, w * 0.12f, h * 0.14f);
                g.FillEllipse(bellyBrush, w * 0.52f, h * 0.11f, w * 0.12f, h * 0.14f);
                g.FillEllipse(bodyBrush, w * 0.405f, h * 0.15f, w * 0.055f, h * 0.07f);
                g.FillEllipse(bodyBrush, w * 0.54f, h * 0.15f, w * 0.055f, h * 0.07f);
                // Beak.
                g.FillPolygon(beakBrush, new[] {
                    new PointF(w * 0.37f, h * 0.27f), new PointF(w * 0.63f, h * 0.27f), new PointF(w * 0.50f, h * 0.37f) });
            }
            // Status badge with a ring in the surface colour.
            float d = Math.Min(w, h) * 0.34f, ring = Math.Max(1.5f, d * 0.16f), x = w - d - ring - 0.5f, y = h - d - ring - 0.5f;
            using (var ringBrush = new SolidBrush(surface)) g.FillEllipse(ringBrush, x - ring, y - ring, d + ring * 2 - 0.5f, d + ring * 2 - 0.5f);
            using (var dot = new SolidBrush(badge)) g.FillEllipse(dot, x, y, d, d);
        }
    }

    public class ThinProgress : Control
    {
        int value;
        bool indeterminate;
        float phase;
        readonly Timer timer = new Timer();

        public ThinProgress()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            timer.Interval = 16;
            timer.Tick += delegate { phase = (phase + 0.012f) % 1.4f; Invalidate(); };
        }

        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }

        public bool Indeterminate
        {
            get { return indeterminate; }
            set { indeterminate = value; phase = 0; if (value) timer.Start(); else timer.Stop(); Invalidate(); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceOf(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            float radius = Height / 2f;
            using (var path = Theme.RoundRect(track, radius))
            using (var brush = new SolidBrush(Theme.Separator))
                g.FillPath(brush, path);
            RectangleF bar;
            if (indeterminate)
            {
                float w = track.Width * 0.3f;
                bar = new RectangleF(track.X + (phase - 0.3f) * track.Width, track.Y, w, track.Height);
            }
            else
            {
                if (value == 0) return;
                bar = new RectangleF(track.X, track.Y, track.Width * value / 100f, track.Height);
            }
            using (var clip = Theme.RoundRect(track, radius))
            using (var path = Theme.RoundRect(bar, radius))
            using (var brush = new SolidBrush(Theme.Accent))
            {
                g.SetClip(clip);
                g.FillPath(brush, path);
                g.ResetClip();
            }
        }
    }

    // macOS-style pop-up button: a rounded field that opens a menu of choices.
    public class PopupSelect : Control
    {
        List<string> items = new List<string>();
        int selected = -1;
        bool hover;
        string placeholder = "";

        public event EventHandler SelectedIndexChanged;

        public PopupSelect()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        public string Placeholder { get { return placeholder; } set { placeholder = value ?? ""; Invalidate(); } }
        public int Count { get { return items.Count; } }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (value < -1 || value >= items.Count) throw new ArgumentOutOfRangeException("value");
                if (selected == value) return;
                selected = value;
                Invalidate();
                if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
            }
        }

        public void SetItems(string[] values)
        {
            items = new List<string>(values ?? new string[0]);
            selected = items.Count > 0 ? 0 : -1;
            Invalidate();
            if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { hover = false; Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) { Focus(); ShowMenu(); }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Down) { ShowMenu(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        void ShowMenu()
        {
            if (!Enabled || items.Count == 0) return;
            var menu = new ContextMenuStrip();
            menu.Renderer = new MenuRenderer();
            menu.Font = Font;
            menu.ShowImageMargin = true;
            menu.MinimumSize = new Size(Width, 0);
            float s = Theme.Scale(this);
            for (int i = 0; i < items.Count; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(items[i]);
                item.Checked = i == selected;
                item.Padding = new Padding(0, (int)(4 * s), 0, (int)(4 * s));
                item.Click += delegate { SelectedIndex = index; };
                menu.Items.Add(item);
            }
            menu.Closed += delegate { BeginInvoke(new MethodInvoker(menu.Dispose)); };
            menu.Show(this, new Point(0, Height + (int)(4 * s)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.SurfaceOf(this));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Theme.Scale(this);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Theme.RoundRect(rect, 9 * s))
            {
                using (var brush = new SolidBrush(Enabled && hover ? Theme.FillHover : Theme.Fill))
                    g.FillPath(brush, path);
                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Color.FromArgb(140, Theme.Accent), 2 * s))
                        g.DrawPath(pen, path);
            }
            bool empty = selected < 0;
            string text = empty ? placeholder : items[selected];
            Color color = !Enabled || empty ? Theme.TertiaryLabel : Theme.Label;
            int pad = (int)(12 * s), chevron = (int)(28 * s);
            var textRect = new Rectangle(pad, 0, Width - pad - chevron, Height);
            TextRenderer.DrawText(g, text, Font, textRect, color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

            // Up/down chevrons, as on a macOS pop-up button.
            float cx = Width - 16 * s, cy = Height / 2f, w = 3.5f * s, h = 2.5f * s, gap = 2f * s;
            using (var pen = new Pen(Enabled ? Theme.SecondaryLabel : Theme.TertiaryLabel, 1.5f * s))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(cx - w, cy - gap), new PointF(cx, cy - gap - h), new PointF(cx + w, cy - gap) });
                g.DrawLines(pen, new[] { new PointF(cx - w, cy + gap), new PointF(cx, cy + gap + h), new PointF(cx + w, cy + gap) });
            }
        }
    }

    public class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color MenuBorder { get { return Theme.Separator; } }
        public override Color MenuItemBorder { get { return Color.Transparent; } }
        public override Color MenuItemSelected { get { return Theme.Accent; } }
    }

    public class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = true; }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.RoundRect(new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2), 6))
            using (var brush = new SolidBrush(Theme.Accent))
                g.FillPath(brush, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Color.White : Theme.Label;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Height / 5f;
            using (var pen = new Pen(e.Item.Selected ? Color.White : Theme.Label, Math.Max(1.5f, u / 2.2f)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(cx - u * 1.3f, cy), new PointF(cx - u * 0.3f, cy + u), new PointF(cx + u * 1.4f, cy - u * 1.1f) });
            }
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
    }

    // An item in the browser. Path is relative to the volume root; Kind is file, folder, link, device or volume.
    public class FileEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; }
        public long Size { get; set; }
        public DateTime Modified { get; set; }
        public string TypeName { get; set; }
        public string Device { get; set; }
        public string Uuid { get; set; }
        public bool IsFolder { get { return Kind == "folder"; } }
        public bool IsLink { get { return Kind == "link"; } }
        public override string ToString() { return Name; }
    }

    // Windows shell icons and type names, looked up by name so the file need not exist locally.
    public static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHSTOCKICONINFO
        {
            public uint cbSize;
            public IntPtr hIcon;
            public int iSysImageIndex;
            public int iIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szPath;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("shell32.dll")]
        static extern int SHGetStockIconInfo(uint siid, uint flags, ref SHSTOCKICONINFO info);
        [DllImport("shell32.dll", EntryPoint = "#727")]
        static extern int SHGetImageList(int list, ref Guid riid, out IntPtr imageList);
        [DllImport("comctl32.dll")]
        static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        const uint SHGFI_TYPENAME = 0x400, SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10, FILE_ATTRIBUTE_NORMAL = 0x80;
        public const uint DriveRemovable = 7, DriveFixed = 8, Folder = 3;

        static SHFILEINFO Query(string name, bool folder, uint flags)
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(folder ? "folder" : name, folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                ref info, (uint)Marshal.SizeOf(info), flags | SHGFI_USEFILEATTRIBUTES);
            return info;
        }

        public static string TypeName(string name, bool folder) { return Query(name, folder, SHGFI_TYPENAME).szTypeName; }

        public static Bitmap ForFile(string name, bool folder, int size) { return Render(Query(name, folder, SHGFI_SYSICONINDEX).iIcon, size); }

        // The icon of an existing path (drives, special folders), as Explorer shows it.
        public static Bitmap ForPath(string path, int size)
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_SYSICONINDEX);
            return Render(info.iIcon, size);
        }

        public static string PathTypeName(string path)
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_TYPENAME);
            return info.szTypeName;
        }

        public static Bitmap Stock(uint siid, int size)
        {
            var info = new SHSTOCKICONINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            SHGetStockIconInfo(siid, SHGFI_SYSICONINDEX, ref info);
            return Render(info.iSysImageIndex, size);
        }

        // SHIL_SMALL (16), SHIL_LARGE (32), SHIL_EXTRALARGE (48), SHIL_JUMBO (256).
        static Bitmap FromList(int list, int index)
        {
            Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            IntPtr imageList;
            if (SHGetImageList(list, ref iid, out imageList) != 0 || imageList == IntPtr.Zero) return null;
            IntPtr handle = ImageList_GetIcon(imageList, index, 1);
            if (handle == IntPtr.Zero) return null;
            try { using (var icon = Icon.FromHandle(handle)) return icon.ToBitmap(); }
            finally { DestroyIcon(handle); }
        }

        // Jumbo entries for types without 256 px artwork hold a small icon in one corner.
        static bool FillsCanvas(Bitmap image)
        {
            int step = Math.Max(1, image.Width / 32);
            for (int y = image.Height / 2; y < image.Height; y += step)
                for (int x = image.Width / 2; x < image.Width; x += step)
                    if (image.GetPixel(x, y).A > 0) return true;
            return false;
        }

        static Bitmap Render(int index, int size)
        {
            Bitmap source = null;
            if (size > 48)
            {
                source = FromList(4, index);
                if (source != null && !FillsCanvas(source)) { source.Dispose(); source = null; }
            }
            if (source == null) source = FromList(size > 32 ? 2 : size > 16 ? 0 : 1, index);
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            if (source == null) return result;
            using (source)
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            return result;
        }

        // Overlays a small badge (the Linux penguin) on the lower-right of an icon.
        public static Bitmap WithBadge(Bitmap image, Image badge)
        {
            var result = new Bitmap(image);
            using (var g = Graphics.FromImage(result))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = (int)(image.Width * 0.5f);
                g.DrawImage(badge, new Rectangle(image.Width - d, image.Height - d, d, d));
            }
            return result;
        }
    }

    // Native list view with the Explorer theme, natural-order sorting and folders first.
    public class ExplorerListView : ListView
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        int sortColumn;
        bool ascending = true;

        public ExplorerListView()
        {
            DoubleBuffered = true;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            AllowDrop = true;
            FoldersFirst = true;
            ListViewItemSorter = new EntryComparer(this);
        }

        public bool FoldersFirst { get; set; }
        public int SortColumn { get { return sortColumn; } }
        public bool Ascending { get { return ascending; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetWindowTheme(Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            if (Theme.IsDark)
            {
                // Dark column header and scroll bars (Windows 10 1809+; ignored elsewhere).
                const int LVM_GETHEADER = 0x101F;
                SetWindowTheme(SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero), "DarkMode_ItemsView", null);
            }
            ForeColor = Theme.Label;
        }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct NMHDR { public IntPtr hwndFrom; public UIntPtr idFrom; public int code; }

        [StructLayout(LayoutKind.Sequential)]
        struct NMCUSTOMDRAW
        {
            public NMHDR hdr; // nested so x64 padding matches the native layout
            public int dwDrawStage; public IntPtr hdc;
            public int left, top, right, bottom; public IntPtr dwItemSpec;
        }

        // Raised for Ctrl + mouse wheel (Delta > 0 zooms in); the list does not scroll then.
        public event MouseEventHandler CtrlWheel;

        protected override void WndProc(ref Message m)
        {
            const int WM_MOUSEWHEEL = 0x020A, MK_CONTROL = 0x0008;
            // Dark mode: the themed column header keeps dark text, so recolour it via custom draw.
            const int WM_NOTIFY = 0x004E, NM_CUSTOMDRAW = -12, CDDS_PREPAINT = 1, CDDS_ITEMPREPAINT = 0x10001;
            if (m.Msg == WM_NOTIFY && Theme.IsDark)
            {
                var nm = (NMCUSTOMDRAW)Marshal.PtrToStructure(m.LParam, typeof(NMCUSTOMDRAW));
                if (nm.hdr.code == NM_CUSTOMDRAW)
                {
                    if (nm.dwDrawStage == CDDS_PREPAINT)
                    {
                        using (var g = Graphics.FromHdc(nm.hdc)) g.Clear(Theme.Surface); // area right of the last column
                        m.Result = (IntPtr)0x20; return; // CDRF_NOTIFYITEMDRAW
                    }
                    if (nm.dwDrawStage == CDDS_ITEMPREPAINT)
                    {
                        int i = nm.dwItemSpec.ToInt32();
                        var r = Rectangle.FromLTRB(nm.left, nm.top, nm.right, nm.bottom);
                        using (var g = Graphics.FromHdc(nm.hdc))
                        using (var pen = new Pen(Theme.Separator))
                        {
                            using (var fill = new SolidBrush(Theme.Surface)) g.FillRectangle(fill, r);
                            g.DrawLine(pen, r.Right - 1, r.Top + 4, r.Right - 1, r.Bottom - 4);
                            if (i >= 0 && i < Columns.Count)
                                TextRenderer.DrawText(g, Columns[i].Text, Font, Rectangle.FromLTRB(r.Left + 6, r.Top, r.Right - 4, r.Bottom),
                                    Theme.SecondaryLabel, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        }
                        m.Result = (IntPtr)4; return; // CDRF_SKIPDEFAULT
                    }
                }
            }
            if (m.Msg == WM_MOUSEWHEEL && (m.WParam.ToInt64() & MK_CONTROL) != 0 && CtrlWheel != null)
            {
                int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
                CtrlWheel(this, new MouseEventArgs(MouseButtons.None, 0, 0, 0, delta));
                return;
            }
            base.WndProc(ref m);
        }

        // Large-icon cell size; the default spacing does not grow with the image list.
        public void SetIconSpacing(int width, int height)
        {
            const int LVM_SETICONSPACING = 0x1035;
            if (IsHandleCreated) SendMessage(Handle, LVM_SETICONSPACING, IntPtr.Zero, (IntPtr)((height << 16) | (width & 0xFFFF)));
        }

        protected override void OnColumnClick(ColumnClickEventArgs e)
        {
            if (e.Column == sortColumn) ascending = !ascending;
            else { sortColumn = e.Column; ascending = true; }
            Sort();
            base.OnColumnClick(e);
        }
    }

    // Raises Changed when Windows reports devices arriving or leaving (WM_DEVICECHANGE).
    public class DeviceChangeWatcher : NativeWindow
    {
        public event EventHandler Changed;

        public DeviceChangeWatcher(Form form)
        {
            if (form.IsHandleCreated) AssignHandle(form.Handle);
            form.HandleCreated += delegate { AssignHandle(form.Handle); };
            form.HandleDestroyed += delegate { ReleaseHandle(); };
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DEVICECHANGE = 0x0219, DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
            if (m.Msg == WM_DEVICECHANGE)
            {
                int code = m.WParam.ToInt32();
                if ((code == DBT_DEVNODES_CHANGED || code == DBT_DEVICEARRIVAL || code == DBT_DEVICEREMOVECOMPLETE) && Changed != null)
                    Changed(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }
    }

    class EntryComparer : System.Collections.IComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        readonly ExplorerListView view;
        public EntryComparer(ExplorerListView view) { this.view = view; }

        public int Compare(object x, object y)
        {
            var a = ((ListViewItem)x).Tag as FileEntry;
            var b = ((ListViewItem)y).Tag as FileEntry;
            if (a == null || b == null) return 0;
            if (view.FoldersFirst && a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            int result;
            switch (view.SortColumn)
            {
                case 1: result = a.Modified.CompareTo(b.Modified); break;
                case 2: result = string.Compare(a.TypeName, b.TypeName, StringComparison.CurrentCultureIgnoreCase); break;
                case 3: result = a.Size.CompareTo(b.Size); break;
                default: result = StrCmpLogicalW(a.Name ?? "", b.Name ?? ""); break;
            }
            if (result == 0 && view.SortColumn != 0) result = StrCmpLogicalW(a.Name ?? "", b.Name ?? "");
            return view.Ascending ? result : -result;
        }
    }
}
