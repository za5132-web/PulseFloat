using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PulseFloat
{
    internal static class Program
    {
        private const string MutexName = "Local\\PulseFloat.SingleInstance";

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test")
            {
                MetricsSampler sampler = new MetricsSampler();
                Thread.Sleep(250);
                Metrics m = sampler.Sample();
                string path = args.Length > 1 ? args[1] : "PulseFloat-self-test.txt";
                System.IO.File.WriteAllText(path, string.Format(
                    "PASS\r\nCPU={0:0.0}\r\nMemory={1:0.0}\r\nDown={2}\r\nUp={3}\r\nProcesses={4}\r\n",
                    m.Cpu, m.Memory, m.DownPerSecond, m.UpPerSecond, m.Processes));
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created) return;
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MonitorForm());
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();
    }

    internal sealed class MonitorForm : Form
    {
        private const int HotkeyId = 0x5046;
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const int VkM = 0x4D;
        private const int WsExTransparent = 0x00000020;
        private const int GwlExStyle = -20;

        private readonly MetricsSampler sampler = new MetricsSampler();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer edgeTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer animationTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer memoryTrimTimer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly RegistryKey settings;
        private Metrics metrics;
        private bool clickThrough;
        private int interval;
        private byte alpha;
        private bool dragging;
        private Point dragOrigin;
        private DockEdge dockEdge;
        private bool collapsed;
        private int leaveTicks;
        private bool animating;
        private bool collapseTarget;
        private DateTime animationStarted;
        private Size animationFrom;
        private Size animationTo;
        private Size lastAnimationSize;
        private bool deepTrimPending = true;
        private const int AnimationDuration = 160;
        private const int ExpandedWidth = 240;
        private const int ExpandedHeight = 104;
        private const int CollapsedWidth = 48;
        private const int CollapsedHeight = 70;
        private enum DockEdge { None, Left, Right }

        private readonly Color primary = Color.FromArgb(255, 242, 248, 252);
        private readonly Color muted = Color.FromArgb(255, 142, 176, 194);
        private readonly Color accent = Color.FromArgb(255, 39, 166, 194);
        private readonly Color warning = Color.FromArgb(255, 230, 92, 76);
        private readonly Color track = Color.FromArgb(80, 42, 64, 80);
        private readonly Font brandFont = new Font("Segoe UI Semibold", 8.4f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font stateFont = new Font("Segoe UI", 6.6f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font labelFont = new Font("Segoe UI", 6.4f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font dataFont = new Font("Consolas", 8.3f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font iconFont = new Font("Segoe UI Symbol", 8f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font compactLabelFont = new Font("Segoe UI Semibold", 5.8f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font compactValueFont = new Font("Segoe UI Semibold", 9.2f, FontStyle.Bold, GraphicsUnit.Point);

        internal MonitorForm()
        {
            settings = Registry.CurrentUser.CreateSubKey("Software\\PulseFloat");
            interval = ReadInt("Interval", 1000, 500, 5000);
            alpha = (byte)ReadInt("Alpha", 238, 150, 255);
            clickThrough = ReadInt("ClickThrough", 0, 0, 1) == 1;

            Text = "PulseFloat";
            ClientSize = new Size(ExpandedWidth, ExpandedHeight);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            int x = ReadInt("X", Screen.PrimaryScreen.WorkingArea.Right - Width - 18, -32000, 32000);
            int y = ReadInt("Y", Screen.PrimaryScreen.WorkingArea.Top + 18, -32000, 32000);
            Location = KeepVisible(new Point(x, y));

            BuildMenu();
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            tray.Icon = Icon;
            tray.Text = "PulseFloat 性能浮窗";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ToggleVisible(); };

            timer.Interval = interval;
            timer.Tick += delegate { metrics = sampler.Sample(); RenderLayer(); };
            timer.Start();
            edgeTimer.Interval = 120;
            edgeTimer.Tick += delegate { UpdateEdgeState(); };
            edgeTimer.Start();
            animationTimer.Interval = 16;
            animationTimer.Tick += delegate { AnimateEdgeTransition(); };
            memoryTrimTimer.Interval = 8000;
            memoryTrimTimer.Tick += delegate { TrimMemory(); };
            memoryTrimTimer.Start();
            metrics = sampler.Sample();

            MouseDown += OnDragStart;
            MouseMove += OnDragMove;
            MouseUp += OnDragEnd;
            MouseDoubleClick += delegate { ToggleClickThrough(); };

            Shown += delegate
            {
                RegisterHotKey(Handle, HotkeyId, ModControl | ModAlt, VkM);
                ApplyClickThrough();
                RestoreDockState();
                RenderLayer();
            };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 | 0x00000080;
                return cp;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                edgeTimer.Dispose();
                animationTimer.Dispose();
                memoryTrimTimer.Dispose();
                brandFont.Dispose();
                stateFont.Dispose();
                labelFont.Dispose();
                dataFont.Dispose();
                iconFont.Dispose();
                compactLabelFont.Dispose();
                compactValueFont.Dispose();
                tray.Visible = false;
                tray.Dispose();
                menu.Dispose();
                settings.Dispose();
            }
            base.Dispose(disposing);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
            {
                ToggleClickThrough();
                return;
            }
            base.WndProc(ref m);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            UnregisterHotKey(Handle, HotkeyId);
            base.OnFormClosed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e) { }

        private void RenderLayer()
        {
            if (!IsHandleCreated || IsDisposed || Width < 1 || Height < 1) return;
            using (Bitmap bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                bool compactFrame = collapsed || (animating && Width <= 72);
                if (compactFrame) DrawCollapsed(g); else DrawExpanded(g);
                PushLayer(bitmap);
            }
        }

        private void DrawExpanded(Graphics g)
        {
            Rectangle panelBounds = new Rectangle(1, 1, ExpandedWidth - 2, ExpandedHeight - 2);
            using (GraphicsPath panelPath = Rounded(panelBounds, 11))
            using (LinearGradientBrush panelBrush = new LinearGradientBrush(
                panelBounds,
                Color.FromArgb(218, 27, 43, 56),
                Color.FromArgb(232, 15, 27, 38),
                90f))
            {
                g.FillPath(panelBrush, panelPath);
            }

            DrawMark(g, 5, 4, 18);
            using (Brush mainBrush = new SolidBrush(primary))
            using (Brush mutedBrush = new SolidBrush(muted))
            {
                DrawReadable(g, "PULSE", brandFont, mainBrush, 27, 3);
                string status = clickThrough ? "PASS  CTRL+ALT+M" : "LIVE  ·  " + (interval / 1000.0).ToString("0.#") + "s";
                SizeF ss = g.MeasureString(status, stateFont);
                DrawReadable(g, status, stateFont, mutedBrush, ExpandedWidth - ss.Width - 5, 6);
            }

            DrawGauge(g, 6, 29, "CPU", metrics.Cpu, FormatPercent(metrics.Cpu));
            DrawGauge(g, 6, 50, "RAM", metrics.Memory, FormatPercent(metrics.Memory));
            DrawNetwork(g, 6, 76, "↓", metrics.DownPerSecond);
            DrawNetwork(g, 91, 76, "↑", metrics.UpPerSecond);
            using (Brush mutedBrush = new SolidBrush(muted))
            using (Brush mainBrush = new SolidBrush(primary))
            {
                DrawReadable(g, "PROC", labelFont, mutedBrush, 184, 76);
                DrawReadable(g, metrics.Processes.ToString(), dataFont, mainBrush, 184, 88);
            }
        }

        private void DrawCollapsed(Graphics g)
        {
            using (GraphicsPath chip = Rounded(new Rectangle(1, 1, Width - 2, Height - 2), 9))
            using (Brush chipBrush = new SolidBrush(Color.FromArgb(248, 18, 32, 44)))
                g.FillPath(chipBrush, chip);
            using (Brush mutedBrush = new SolidBrush(muted))
            using (Brush mainBrush = new SolidBrush(primary))
            using (Brush accentBrush = new SolidBrush(accent))
            {
                int pad = 8;
                g.FillRectangle(accentBrush, dockEdge == DockEdge.Left ? 1 : Width - 3, 11, 2, Height - 22);
                DrawReadable(g, "CPU", compactLabelFont, mutedBrush, pad, 5);
                using (Brush cpuBrush = new SolidBrush(metrics.Cpu >= 85 ? warning : primary))
                    DrawReadable(g, FormatPercent(metrics.Cpu), compactValueFont, cpuBrush, pad, 15);
                DrawReadable(g, "RAM", compactLabelFont, mutedBrush, pad, 36);
                using (Brush ramBrush = new SolidBrush(metrics.Memory >= 85 ? warning : primary))
                    DrawReadable(g, FormatPercent(metrics.Memory), compactValueFont, ramBrush, pad, 46);
            }
        }

        private void DrawGauge(Graphics g, int x, int y, string label, double value, string text)
        {
            const int barWidth = 156;
            const int barHeight = 3;
            Color fill = value >= 85 ? warning : accent;
            using (Brush mutedBrush = new SolidBrush(muted))
            using (Brush valueBrush = new SolidBrush(value >= 85 ? warning : primary))
            using (Brush trackBrush = new SolidBrush(track))
            using (Brush fillBrush = new SolidBrush(fill))
            {
                DrawReadable(g, label, labelFont, mutedBrush, x, y);
                SizeF size = g.MeasureString(text, dataFont);
                DrawReadable(g, text, dataFont, valueBrush, x + 228 - size.Width, y - 3);
                RectangleF r = new RectangleF(x + 34, y + 7, barWidth, barHeight);
                g.FillRoundedRectangle(trackBrush, r, 2.5f);
                float w = (float)(r.Width * Math.Max(0, Math.Min(100, value)) / 100.0);
                if (w > 1) g.FillRoundedRectangle(fillBrush, new RectangleF(r.X, r.Y, w, r.Height), 2.5f);
            }
        }

        private void DrawNetwork(Graphics g, int x, int y, string direction, long bytes)
        {
            using (Brush accentBrush = new SolidBrush(accent))
            using (Brush mainBrush = new SolidBrush(primary))
            {
                DrawReadable(g, direction, iconFont, accentBrush, x, y - 2);
                DrawReadable(g, FormatRate(bytes), dataFont, mainBrush, x + 15, y);
            }
        }

        private void DrawMark(Graphics g, int x, int y, int size)
        {
            using (Brush disc = new SolidBrush(Color.FromArgb(235, 22, 42, 56)))
                g.FillEllipse(disc, x, y, size, size);
            PointF[] pulse = { new PointF(x + 3, y + 10), new PointF(x + 6, y + 10), new PointF(x + 8, y + 5), new PointF(x + 11, y + 14), new PointF(x + 13, y + 9), new PointF(x + 16, y + 9) };
            using (Pen pen = new Pen(accent, 1.7f)) { pen.StartCap = pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round; g.DrawLines(pen, pulse); }
        }

        private void DrawReadable(Graphics g, string text, Font font, Brush brush, float x, float y)
        {
            g.DrawString(text, font, brush, x, y);
        }

        private void BuildMenu()
        {
            menu.ShowImageMargin = false;
            menu.Items.Add("显示 / 隐藏", null, delegate { ToggleVisible(); });
            menu.Items.Add("点击穿透  (Ctrl+Alt+M)", null, delegate { ToggleClickThrough(); });

            ToolStripMenuItem refresh = new ToolStripMenuItem("刷新频率");
            AddInterval(refresh, "0.5 秒", 500);
            AddInterval(refresh, "1 秒", 1000);
            AddInterval(refresh, "2 秒", 2000);
            AddInterval(refresh, "5 秒", 5000);
            menu.Items.Add(refresh);

            ToolStripMenuItem opacity = new ToolStripMenuItem("透明度");
            AddOpacity(opacity, "100%", 255);
            AddOpacity(opacity, "93%", 238);
            AddOpacity(opacity, "82%", 209);
            AddOpacity(opacity, "70%", 179);
            menu.Items.Add(opacity);

            ToolStripMenuItem startup = new ToolStripMenuItem("开机启动");
            startup.Checked = IsStartupEnabled();
            startup.Click += delegate { SetStartup(!startup.Checked); startup.Checked = IsStartupEnabled(); };
            menu.Items.Add(startup);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Close(); });
            menu.Opening += delegate { UpdateChecks(); };
        }

        private void AddInterval(ToolStripMenuItem parent, string label, int value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Tag = value;
            item.Click += delegate { interval = value; timer.Interval = value; Write("Interval", value); UpdateChecks(); };
            parent.DropDownItems.Add(item);
        }

        private void AddOpacity(ToolStripMenuItem parent, string label, byte value)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(label);
            item.Tag = (int)value;
            item.Click += delegate { alpha = value; Write("Alpha", (int)alpha); RenderLayer(); UpdateChecks(); };
            parent.DropDownItems.Add(item);
        }

        private void UpdateChecks()
        {
            foreach (ToolStripItem top in menu.Items)
            {
                ToolStripMenuItem parent = top as ToolStripMenuItem;
                if (parent == null) continue;
                if (parent.Text == "点击穿透  (Ctrl+Alt+M)") parent.Checked = clickThrough;
                foreach (ToolStripItem child in parent.DropDownItems)
                {
                    ToolStripMenuItem item = child as ToolStripMenuItem;
                    if (item == null || item.Tag == null) continue;
                    int tag = (int)item.Tag;
                    item.Checked = parent.Text == "刷新频率" ? tag == interval : tag == alpha;
                }
            }
        }

        private void ToggleVisible()
        {
            if (Visible) Hide(); else { Show(); TopMost = true; }
        }

        private void ToggleClickThrough()
        {
            clickThrough = !clickThrough;
            Write("ClickThrough", clickThrough ? 1 : 0);
            ApplyClickThrough();
            RenderLayer();
            tray.ShowBalloonTip(900, "PulseFloat", clickThrough ? "已开启点击穿透，按 Ctrl+Alt+M 退出" : "已关闭点击穿透", ToolTipIcon.None);
        }

        private void ApplyClickThrough()
        {
            int style = GetWindowLong(Handle, GwlExStyle);
            if (clickThrough) style |= WsExTransparent; else style &= ~WsExTransparent;
            SetWindowLong(Handle, GwlExStyle, style);
        }

        private void OnDragStart(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (collapsed || animating) FinishExpandedImmediately();
                dragging = true; dragOrigin = e.Location; Capture = true;
                dockEdge = DockEdge.None;
            }
            if (e.Button == MouseButtons.Right) menu.Show(this, e.Location);
        }

        private void OnDragMove(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            Rectangle w = Screen.FromPoint(Cursor.Position).WorkingArea;
            int nextX = Left + e.X - dragOrigin.X;
            int nextY = Top + e.Y - dragOrigin.Y;
            const int grab = 26;
            nextX = Math.Max(w.Left - Width + grab, Math.Min(nextX, w.Right - grab));
            nextY = Math.Max(w.Top, Math.Min(nextY, w.Bottom - Height));
            Location = new Point(nextX, nextY);
        }

        private void OnDragEnd(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false; Capture = false;
            SnapToEdge();
            Write("X", Left); Write("Y", Top);
            Write("Dock", (int)dockEdge);
        }

        private void SnapToEdge()
        {
            Screen screen = Screen.FromRectangle(Bounds);
            Rectangle w = screen.WorkingArea;
            const int snapDistance = 32;
            if (Left <= w.Left + snapDistance)
            {
                dockEdge = DockEdge.Left;
                Location = new Point(w.Left, Math.Max(w.Top, Math.Min(Top, w.Bottom - Height)));
            }
            else if (Right >= w.Right - snapDistance)
            {
                dockEdge = DockEdge.Right;
                Location = new Point(w.Right - Width, Math.Max(w.Top, Math.Min(Top, w.Bottom - Height)));
            }
            else dockEdge = DockEdge.None;
        }

        private void UpdateEdgeState()
        {
            if (dockEdge == DockEdge.None || dragging || animating || clickThrough || !Visible) return;
            bool inside = Bounds.Contains(Cursor.Position);
            if (inside)
            {
                leaveTicks = 0;
                if (collapsed) ExpandFromEdge();
            }
            else if (!collapsed && ++leaveTicks >= 3) CollapseToEdge();
        }

        private void CollapseToEdge()
        {
            if (dockEdge == DockEdge.None || collapsed || animating) return;
            StartEdgeTransition(true);
        }

        private void ExpandFromEdge()
        {
            if (dockEdge == DockEdge.None || !collapsed || animating) return;
            StartEdgeTransition(false);
        }

        private void StartEdgeTransition(bool toCollapsed)
        {
            Rectangle w = Screen.FromRectangle(Bounds).WorkingArea;
            collapseTarget = toCollapsed;
            animationFrom = Size;
            animationTo = toCollapsed
                ? new Size(CollapsedWidth, CollapsedHeight)
                : new Size(ExpandedWidth, ExpandedHeight);
            lastAnimationSize = Size.Empty;
            Top = Math.Max(w.Top, Math.Min(Top, w.Bottom - animationTo.Height));
            collapsed = false;
            animating = true;
            leaveTicks = 0;
            animationStarted = DateTime.UtcNow;
            animationTimer.Start();
            AnimateEdgeTransition();
        }

        private void AnimateEdgeTransition()
        {
            if (!animating) return;
            double raw = (DateTime.UtcNow - animationStarted).TotalMilliseconds / AnimationDuration;
            double t = Math.Max(0, Math.Min(1, raw));
            Rectangle w = Screen.FromRectangle(Bounds).WorkingArea;

            if (t >= 1)
            {
                animationTimer.Stop();
                animating = false;
            collapsed = collapseTarget;
            Size = animationTo;
            Left = dockEdge == DockEdge.Left ? w.Left : w.Right - Width;
            RenderLayer();
            if (collapsed)
            {
                deepTrimPending = true;
                memoryTrimTimer.Interval = 1400;
                memoryTrimTimer.Start();
            }
            return;
            }

            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);
            int width = (int)Math.Round(animationFrom.Width + (animationTo.Width - animationFrom.Width) * eased);
            int height = (int)Math.Round(animationFrom.Height + (animationTo.Height - animationFrom.Height) * eased);
            Size nextSize = new Size(Math.Max(1, width), Math.Max(1, height));
            if (nextSize == lastAnimationSize) return;
            lastAnimationSize = nextSize;
            Size = nextSize;
            Left = dockEdge == DockEdge.Left ? w.Left : w.Right - Width;
            RenderLayer();
        }

        private void FinishExpandedImmediately()
        {
            animationTimer.Stop();
            animating = false;
            collapsed = false;
            Rectangle w = Screen.FromRectangle(Bounds).WorkingArea;
            Size = new Size(ExpandedWidth, ExpandedHeight);
            Left = dockEdge == DockEdge.Left ? w.Left : w.Right - Width;
            Top = Math.Max(w.Top, Math.Min(Top, w.Bottom - Height));
            RenderLayer();
        }

        private void TrimMemory()
        {
            if (animating || dragging) return;
            memoryTrimTimer.Stop();
            if (deepTrimPending)
            {
                GC.Collect(0, GCCollectionMode.Optimized);
                deepTrimPending = false;
            }
            EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            memoryTrimTimer.Interval = 8000;
        }

        private void RestoreDockState()
        {
            int saved = ReadInt("Dock", 0, 0, 2);
            dockEdge = (DockEdge)saved;
            if (dockEdge != DockEdge.None)
            {
                Rectangle w = Screen.FromPoint(Location).WorkingArea;
                Left = dockEdge == DockEdge.Left ? w.Left : w.Right - ExpandedWidth;
            }
            RenderLayer();
        }

        private Point KeepVisible(Point p)
        {
            foreach (Screen screen in Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(new Rectangle(p, Size))) return p;
            Rectangle w = Screen.PrimaryScreen.WorkingArea;
            return new Point(w.Right - Width - 18, w.Top + 18);
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
                return key != null && key.GetValue("PulseFloat") != null;
        }

        private void SetStartup(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run"))
            {
                if (enabled) key.SetValue("PulseFloat", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("PulseFloat", false);
            }
        }

        private int ReadInt(string name, int fallback, int min, int max)
        {
            object raw = settings.GetValue(name);
            int value;
            if (raw == null || !int.TryParse(raw.ToString(), out value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }

        private void Write(string name, int value) { settings.SetValue(name, value, RegistryValueKind.DWord); }

        private static string FormatPercent(double value) { return value.ToString("0") + "%"; }

        private static string FormatRate(long bytes)
        {
            if (bytes < 1024) return bytes + " B/s";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " K/s";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0") + " M/s";
            return (bytes / 1073741824.0).ToString("0.00") + " G/s";
        }

        private static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void PushLayer(Bitmap bitmap)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memoryDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            IntPtr oldBitmap = SelectObject(memoryDc, hBitmap);
            try
            {
                NativePoint source = new NativePoint(0, 0);
                NativePoint destination = new NativePoint(Left, Top);
                NativeSize size = new NativeSize(bitmap.Width, bitmap.Height);
                BlendFunction blend = new BlendFunction();
                blend.BlendOp = 0;
                blend.SourceConstantAlpha = alpha;
                blend.AlphaFormat = 1;
                UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memoryDc, oldBitmap);
                DeleteObject(hBitmap);
                DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            internal int X, Y;
            internal NativePoint(int x, int y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            internal int Width, Height;
            internal NativeSize(int width, int height) { Width = width; Height = height; }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, int virtualKey);
        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screenDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, int colorKey, ref BlendFunction blend, int flags);
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);
        [DllImport("psapi.dll")]
        private static extern bool EmptyWorkingSet(IntPtr process);
    }

    internal struct Metrics
    {
        internal double Cpu;
        internal double Memory;
        internal long DownPerSecond;
        internal long UpPerSecond;
        internal int Processes;
    }

    internal sealed class MetricsSampler
    {
        private ulong lastIdle, lastKernel, lastUser;
        private long lastDown, lastUp;
        private long lastTick;
        private readonly NetworkInterface[] adapters;

        internal MetricsSampler()
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
            GetCpuTimes(out lastIdle, out lastKernel, out lastUser);
            GetNetworkTotals(out lastDown, out lastUp);
            lastTick = Stopwatch.GetTimestamp();
        }

        internal Metrics Sample()
        {
            Metrics m = new Metrics();
            ulong idle, kernel, user;
            GetCpuTimes(out idle, out kernel, out user);
            ulong idleDelta = idle - lastIdle;
            ulong totalDelta = (kernel - lastKernel) + (user - lastUser);
            m.Cpu = totalDelta == 0 ? 0 : Math.Max(0, Math.Min(100, (totalDelta - idleDelta) * 100.0 / totalDelta));
            lastIdle = idle; lastKernel = kernel; lastUser = user;

            MemoryStatusEx status = new MemoryStatusEx();
            if (GlobalMemoryStatusEx(status)) m.Memory = status.MemoryLoad;

            long down, up;
            GetNetworkTotals(out down, out up);
            long now = Stopwatch.GetTimestamp();
            double seconds = (now - lastTick) / (double)Stopwatch.Frequency;
            if (seconds > 0)
            {
                m.DownPerSecond = down >= lastDown ? (long)((down - lastDown) / seconds) : 0;
                m.UpPerSecond = up >= lastUp ? (long)((up - lastUp) / seconds) : 0;
            }
            lastDown = down; lastUp = up; lastTick = now;
            m.Processes = GetProcessCount();
            return m;
        }

        private void GetNetworkTotals(out long received, out long sent)
        {
            received = 0; sent = 0;
            foreach (NetworkInterface adapter in adapters)
            {
                try
                {
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    IPv4InterfaceStatistics stats = adapter.GetIPv4Statistics();
                    received += stats.BytesReceived;
                    sent += stats.BytesSent;
                }
                catch (NetworkInformationException) { }
                catch (PlatformNotSupportedException) { }
            }
        }

        private static void GetCpuTimes(out ulong idle, out ulong kernel, out ulong user)
        {
            FileTime i, k, u;
            if (!GetSystemTimes(out i, out k, out u)) { idle = kernel = user = 0; return; }
            idle = i.Value; kernel = k.Value; user = u.Value;
        }

        private static int GetProcessCount()
        {
            uint[] ids = new uint[2048];
            uint needed;
            return EnumProcesses(ids, (uint)(ids.Length * sizeof(uint)), out needed) ? (int)(needed / sizeof(uint)) : 0;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime
        {
            internal uint Low;
            internal uint High;
            internal ulong Value { get { return ((ulong)High << 32) | Low; } }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private sealed class MemoryStatusEx
        {
            internal uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            internal uint MemoryLoad;
            internal ulong TotalPhysical;
            internal ulong AvailablePhysical;
            internal ulong TotalPageFile;
            internal ulong AvailablePageFile;
            internal ulong TotalVirtual;
            internal ulong AvailableVirtual;
            internal ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);
        [DllImport("psapi.dll")]
        private static extern bool EnumProcesses([Out] uint[] processIds, uint size, out uint needed);
    }

    internal static class GraphicsExtensions
    {
        internal static void FillRoundedRectangle(this Graphics g, Brush brush, RectangleF r, float radius)
        {
            float d = radius * 2;
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }
    }
}
