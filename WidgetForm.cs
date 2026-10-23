using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NetSpeedWidget
{
    internal sealed class WidgetForm : Form
    {
        private static readonly Color Background = Color.FromArgb(22, 27, 35);
        private static readonly Color Foreground = Color.FromArgb(237, 243, 248);
        private static readonly Color Muted = Color.FromArgb(144, 157, 176);
        private static readonly Color DownloadColor = Color.FromArgb(96, 226, 181);
        private static readonly Color UploadColor = Color.FromArgb(124, 177, 255);
        private readonly Settings settings;
        private readonly NetworkMonitor monitor = new NetworkMonitor();
        private readonly ProcessNetworkMonitor processMonitor = new ProcessNetworkMonitor();
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly ToolTip tips = new ToolTip();
        private readonly List<double> downloads = new List<double>();
        private readonly List<double> uploads = new List<double>();
        private readonly List<Font> fonts = new List<Font>();
        private readonly Label downloadValue;
        private readonly Label uploadValue;
        private readonly Label downloadUnit;
        private readonly Label uploadUnit;
        private readonly Label status;
        private readonly Label live;
        private readonly Label uploadHeading;
        private readonly Label historyLabel;
        private readonly Button menuButton;
        private readonly Button hideButton;
        private readonly Button expandButton;
        private readonly ProcessTrafficView processView;
        private readonly float scale;
        private NetworkSample sample;
        private bool wasHidden;
        private bool hiddenTipShown;
        private bool closing;
        private Icon appIcon;
        private readonly Stopwatch historyClock = Stopwatch.StartNew();
        private double lastSampleTime;
        private ProcessTrafficSample processSample;
        private System.Threading.EventWaitHandle handoffReady;
        private Process handoffChild;
        private Timer handoffTimer;
        private readonly Stopwatch handoffClock = new Stopwatch();

        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

        public WidgetForm(Settings preferences)
        {
            settings = preferences;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = Math.Max(1f, g.DpiX / 96f);
            AutoScaleMode = AutoScaleMode.None;
            Text = "NetSpeed Widget";
            AccessibleName = "NetSpeed Widget";
            AccessibleDescription = "Current download and upload speeds. Expand for app and process speeds. Right click for settings. Drag to move.";
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            ShowInTaskbar = settings.ShowInTaskbar;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(S(320), S(188));
            BackColor = Background;
            ForeColor = Foreground;
            DoubleBuffered = true;
            KeyPreview = true;
            TopMost = settings.AlwaysOnTop;
            appIcon = CreateIcon();
            Icon = appIcon;

            Label title = MakeLabel("NetSpeed", 36, 14, 100, 21, 10, FontStyle.Bold, Foreground);
            live = MakeLabel("LIVE", 177, 16, 39, 16, 7.5f, FontStyle.Bold, DownloadColor);
            expandButton = MakeButton("⌄", "Expand app speeds", 221, 7, ToggleExpanded);
            menuButton = MakeButton("⋯", "Options", 253, 7, ShowMenu);
            hideButton = MakeButton("−", "Hide to tray", 285, 7, HideWidget);
            MakeLabel("↓  DOWNLOAD", 20, 49, 132, 17, 8, FontStyle.Bold, DownloadColor);
            uploadHeading = MakeLabel("↑  UPLOAD", 179, 49, 122, 17, 8, FontStyle.Bold, UploadColor);
            downloadValue = MakeLabel("—", 17, 68, 139, 40, 26, FontStyle.Regular, Foreground);
            uploadValue = MakeLabel("—", 176, 68, 138, 40, 26, FontStyle.Regular, Foreground);
            downloadValue.AccessibleName = "Download speed";
            uploadValue.AccessibleName = "Upload speed";
            downloadUnit = MakeLabel("B/s", 21, 108, 130, 16, 8, FontStyle.Regular, Muted);
            uploadUnit = MakeLabel("B/s", 180, 108, 130, 16, 8, FontStyle.Regular, Muted);
            status = MakeLabel("Connecting…", 31, 164, 218, 17, 8, FontStyle.Regular, Muted);
            historyLabel = MakeLabel("60s", 267, 164, 32, 17, 8, FontStyle.Regular, Muted);
            historyLabel.TextAlign = ContentAlignment.TopRight;
            tips.SetToolTip(historyLabel, "Last 60 seconds · each graph scales independently");
            tips.SetToolTip(title, "Drag anywhere to move · right click for options");
            tips.SetToolTip(downloadValue, "Received data per second");
            tips.SetToolTip(uploadValue, "Sent data per second");
            tips.SetToolTip(hideButton, "Hide to tray · double-click the tray icon to restore");
            tips.SetToolTip(menuButton, "Options");
            processView = new ProcessTrafficView(scale);
            processView.EnableRequested += delegate { EnableProcessDetails(); };
            Controls.Add(processView);
            ApplyExpandedLayout();
            ContextMenuStrip = menu;
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { PopulateMenu(); e.Cancel = false; };
            menu.Font = GetFont(9, FontStyle.Regular);
            tray.Icon = appIcon;
            tray.Text = "NetSpeed Widget";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWidget(); };
            tray.Visible = true;
            timer.Tick += delegate { RefreshSample(); };
            MouseDown += BeginDrag;
            Shown += delegate { RestorePosition(); if (settings.Expanded) processMonitor.Start(); RefreshSample(); timer.Start(); };
            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized) processMonitor.Stop();
                else if (Visible && settings.Expanded) processMonitor.Start();
            };
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        private int S(float value) { return (int)Math.Round(value * scale); }
        private Font GetFont(float size, FontStyle style)
        {
            Font font = new Font("Segoe UI", size * (96f / 72f) * scale, style, GraphicsUnit.Pixel);
            fonts.Add(font);
            return font;
        }

        private Label MakeLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label { Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(height)),
                Font = GetFont(size, style), ForeColor = color, BackColor = Background, AutoEllipsis = true,
                UseMnemonic = false, TabStop = false, Margin = Padding.Empty, Padding = Padding.Empty };
            label.MouseDown += BeginDrag;
            Controls.Add(label);
            return label;
        }

        private Button MakeButton(string text, string accessible, int x, int y, Action action)
        {
            Button button = new Button { Text = text, AccessibleName = accessible,
                Bounds = new Rectangle(S(x), S(y), S(29), S(30)), Font = GetFont(14, FontStyle.Bold),
                ForeColor = Muted, BackColor = Background, FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand, TabStop = true, Padding = Padding.Empty };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 50, 63);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(53, 64, 79);
            button.Click += delegate { action(); };
            Controls.Add(button);
            return button;
        }

        private void ToggleExpanded()
        {
            settings.Expanded = !settings.Expanded;
            ApplyExpandedLayout();
            KeepOnScreen();
            if (settings.Expanded && Visible) processMonitor.Start();
            else processMonitor.Stop();
            RefreshSample();
            SaveSettings();
        }

        private void ApplyExpandedLayout()
        {
            int extra = settings.Expanded ? 200 : 0;
            SuspendLayout();
            ClientSize = new Size(S(320 + extra), S(settings.Expanded ? 546 : 188));
            live.Left = S(177 + extra);
            expandButton.Left = S(221 + extra);
            menuButton.Left = S(253 + extra);
            hideButton.Left = S(285 + extra);
            expandButton.Text = settings.Expanded ? "⌃" : "⌄";
            expandButton.AccessibleName = settings.Expanded ? "Collapse app speeds" : "Expand app speeds";
            tips.SetToolTip(expandButton, settings.Expanded ? "Collapse app details" : "Expand app and process speeds");
            uploadHeading.Left = S(179 + extra / 2);
            uploadValue.Left = S(176 + extra / 2);
            uploadUnit.Left = S(180 + extra / 2);
            downloadValue.Width = S(139 + extra / 2);
            uploadValue.Width = S(138 + extra / 2);
            status.Width = S(218 + extra);
            historyLabel.Left = S(267 + extra);
            processView.Bounds = new Rectangle(S(20), S(202), S(480), S(324));
            processView.Visible = settings.Expanded;
            ResumeLayout(false);
            UpdateWindowRegion();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen border = new Pen(Color.FromArgb(55, 65, 80), S(1)))
            using (GraphicsPath shape = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), S(14)))
                g.DrawPath(border, shape);
            using (Pen divider = new Pen(Color.FromArgb(45, 54, 67)))
            {
                int mid = settings.Expanded ? 261 : 161;
                int right = settings.Expanded ? 500 : 300;
                g.DrawLine(divider, S(mid), S(52), S(mid), S(149));
                g.DrawLine(divider, S(20), S(157), S(right), S(157));
                if (settings.Expanded) g.DrawLine(divider, S(20), S(191), S(right), S(191));
            }
            using (Pen mark = new Pen(DownloadColor, S(2)))
            {
                g.DrawLines(mark, new[] { new Point(S(18), S(29)), new Point(S(21), S(29)), new Point(S(24), S(19)), new Point(S(27), S(31)), new Point(S(30), S(24)) });
            }
            int extra = settings.Expanded ? 100 : 0;
            DrawGraph(g, downloads, new Rectangle(S(21), S(132), S(124 + extra), S(18)), DownloadColor);
            DrawGraph(g, uploads, new Rectangle(S(180 + extra), S(132), S(119 + extra), S(18)), UploadColor);
            Color dot = sample != null && sample.IsConnected && string.IsNullOrEmpty(sample.Error) ? DownloadColor : Color.FromArgb(235, 180, 104);
            using (Brush brush = new SolidBrush(dot)) g.FillEllipse(brush, S(20), S(169), S(5), S(5));
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Only the harmless "show widget" message may cross the elevation boundary.
            Program.ChangeWindowMessageFilterEx(Handle, (uint)Program.ShowMessage, 1, IntPtr.Zero);
            UpdateWindowRegion();
        }

        private void UpdateWindowRegion()
        {
            if (!IsHandleCreated || Width < 1 || Height < 1) return;
            using (GraphicsPath shape = RoundedRect(new Rectangle(0, 0, Width, Height), S(14)))
            {
                Region old = Region;
                Region = new Region(shape);
                if (old != null) old.Dispose();
            }
        }

        private void DrawGraph(Graphics g, List<double> history, Rectangle rect, Color color)
        {
            using (Pen baseline = new Pen(Color.FromArgb(44, 53, 65))) g.DrawLine(baseline, rect.Left, rect.Bottom, rect.Right, rect.Bottom);
            if (history.Count < 2) return;
            double max = 1024;
            foreach (double value in history) max = Math.Max(max, value);
            PointF[] points = new PointF[history.Count];
            for (int i = 0; i < history.Count; i++)
                points[i] = new PointF(rect.Left + (float)(60 - history.Count + i) / 59 * rect.Width, rect.Bottom - (float)(history[i] / max) * rect.Height);
            using (GraphicsPath fill = new GraphicsPath())
            using (Brush tint = new SolidBrush(Color.FromArgb(25, color)))
            using (Pen line = new Pen(color, Math.Max(1.3f, scale * 1.3f)))
            {
                fill.AddLines(points);
                fill.AddLine(points[points.Length - 1], new PointF(points[points.Length - 1].X, rect.Bottom));
                fill.AddLine(new PointF(points[points.Length - 1].X, rect.Bottom), new PointF(points[0].X, rect.Bottom));
                fill.CloseFigure();
                g.FillPath(tint, fill);
                g.DrawLines(line, points);
            }
        }

        private void RefreshSample()
        {
            double now = historyClock.Elapsed.TotalSeconds;
            if (now - lastSampleTime > 10) { downloads.Clear(); uploads.Clear(); }
            lastSampleTime = now;
            sample = monitor.Sample(settings.AdapterId);
            AddHistory(downloads, sample.DownloadBytesPerSecond);
            AddHistory(uploads, sample.UploadBytesPerSecond);
            if (settings.Expanded && Visible && WindowState != FormWindowState.Minimized)
            {
                processSample = processMonitor.Sample();
            }
            UpdateDisplay();
        }

        private static void AddHistory(List<double> values, double next)
        {
            if (values.Count == 60) values.RemoveAt(0);
            values.Add(Math.Max(0, next));
        }

        private void UpdateDisplay()
        {
            if (sample == null) return;
            bool unavailable = !string.IsNullOrEmpty(sample.Error);
            string value, unit;
            SpeedFormatter.Format(sample.DownloadBytesPerSecond, settings.UseBits, out value, out unit);
            downloadValue.Text = unavailable ? "—" : value;
            downloadUnit.Text = unit;
            downloadValue.AccessibleDescription = unavailable ? sample.Error : value + " " + unit;
            SpeedFormatter.Format(sample.UploadBytesPerSecond, settings.UseBits, out value, out unit);
            uploadValue.Text = unavailable ? "—" : value;
            uploadUnit.Text = unit;
            uploadValue.AccessibleDescription = unavailable ? sample.Error : value + " " + unit;
            status.Text = unavailable ? "Unable to read network" : sample.ConnectionLabel;
            live.Text = unavailable ? "ERROR" : sample.IsConnected ? "LIVE" : "IDLE";
            live.ForeColor = unavailable ? Color.FromArgb(235, 180, 104) : sample.IsConnected ? DownloadColor : Muted;
            tips.SetToolTip(status, unavailable ? sample.Error : sample.ConnectionLabel + "\nUpdates every second · includes local network traffic");
            string trayText = unavailable ? "NetSpeed · Unable to read network" : "↓ " + SpeedFormatter.Full(sample.DownloadBytesPerSecond, settings.UseBits) + "   ↑ " + SpeedFormatter.Full(sample.UploadBytesPerSecond, settings.UseBits);
            tray.Text = trayText.Length > 63 ? trayText.Substring(0, 63) : trayText;
            if (settings.Expanded && processSample != null) processView.UpdateSample(processSample, settings.UseBits);
            Invalidate();
        }

        private void PopulateMenu()
        {
            while (menu.Items.Count > 0) { ToolStripItem old = menu.Items[0]; menu.Items.RemoveAt(0); old.Dispose(); }
            menu.Items.Add("Show widget", null, delegate { ShowWidget(); });
            menu.Items.Add(settings.Expanded ? "Collapse app details" : "Expand app details", null, delegate { ToggleExpanded(); });
            ToolStripMenuItem top = new ToolStripMenuItem("Always on top") { Checked = settings.AlwaysOnTop };
            top.Click += delegate { settings.AlwaysOnTop = !settings.AlwaysOnTop; TopMost = settings.AlwaysOnTop; SaveSettings(); };
            menu.Items.Add(top);
            ToolStripMenuItem taskbar = new ToolStripMenuItem("Show in taskbar") { Checked = ShowInTaskbar };
            taskbar.Click += delegate { settings.ShowInTaskbar = !ShowInTaskbar; ShowInTaskbar = settings.ShowInTaskbar; SaveSettings(); };
            menu.Items.Add(taskbar);
            ToolStripMenuItem units = new ToolStripMenuItem("Speed units");
            ToolStripMenuItem bytes = new ToolStripMenuItem("Bytes / second (KB/s, MB/s)") { Checked = !settings.UseBits };
            ToolStripMenuItem bits = new ToolStripMenuItem("Bits / second (Mbit/s)") { Checked = settings.UseBits };
            bytes.Click += delegate { settings.UseBits = false; SaveSettings(); UpdateDisplay(); };
            bits.Click += delegate { settings.UseBits = true; SaveSettings(); UpdateDisplay(); };
            units.DropDownItems.Add(bytes);
            units.DropDownItems.Add(bits);
            menu.Items.Add(units);
            ToolStripMenuItem adapters = new ToolStripMenuItem("Network adapter");
            ToolStripMenuItem automatic = new ToolStripMenuItem("Automatic (physical connections)") { Checked = string.IsNullOrEmpty(settings.AdapterId) };
            automatic.Click += delegate { SelectAdapter(""); };
            adapters.DropDownItems.Add(automatic);
            adapters.DropDownItems.Add(new ToolStripSeparator());
            try
            {
                List<AdapterInfo> available = monitor.GetAdapters();
                foreach (AdapterInfo adapter in available)
                {
                    string id = adapter.Id;
                    ToolStripMenuItem item = new ToolStripMenuItem(adapter.Name.Replace("&", "&&")) { Checked = id == settings.AdapterId };
                    item.Click += delegate { SelectAdapter(id); };
                    adapters.DropDownItems.Add(item);
                }
                if (available.Count == 0) adapters.DropDownItems.Add(new ToolStripMenuItem("No adapters found") { Enabled = false });
            }
            catch (Exception) { adapters.DropDownItems.Add(new ToolStripMenuItem("Adapters unavailable") { Enabled = false }); }
            menu.Items.Add(adapters);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Reset position", null, delegate { PlaceDefault(); SaveSettings(); ShowWidget(); });
            menu.Items.Add("Hide to tray", null, delegate { HideWidget(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Close(); });
        }

        private void SelectAdapter(string id)
        {
            settings.AdapterId = id;
            monitor.Reset();
            downloads.Clear();
            uploads.Clear();
            SaveSettings();
            RefreshSample();
        }

        private void ShowMenu() { menu.Show(this, new Point(menuButton.Left, S(36))); }
        private void HideWidget()
        {
            SaveSettings();
            processMonitor.Stop();
            Hide();
            wasHidden = true;
            if (!hiddenTipShown)
            {
                hiddenTipShown = true;
                tray.ShowBalloonTip(3000, "NetSpeed is still running", "Double-click the tray icon to show your widget. Right-click it to exit.", ToolTipIcon.Info);
            }
        }

        private void ShowWidget()
        {
            WindowState = FormWindowState.Normal;
            Show();
            wasHidden = false;
            KeepOnScreen();
            if (settings.Expanded) { processMonitor.Start(); RefreshSample(); }
            Activate();
        }

        private void EnableProcessDetails()
        {
            if (handoffReady != null) return;
            if (processSample == null || !processSample.NeedsElevation || Program.IsAdministrator)
            {
                processMonitor.Stop();
                processMonitor.Start();
                RefreshSample();
                return;
            }
            SaveSettings();
            string nonce = Guid.NewGuid().ToString("N");
            handoffReady = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset,
                @"Local\NetSpeedWidget.Handoff." + nonce);
            try
            {
                handoffChild = Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = "--handoff " + nonce + " " + Process.GetCurrentProcess().Id + " --expanded" + (ShowInTaskbar ? " --taskbar" : ""),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = true,
                    Verb = "runas"
                });
                handoffClock.Restart();
                handoffTimer = new Timer { Interval = 100 };
                handoffTimer.Tick += delegate
                {
                    if (handoffReady.WaitOne(0)) { Close(); return; }
                    bool exited = handoffChild != null && handoffChild.HasExited;
                    if (exited || handoffClock.Elapsed.TotalSeconds > 20)
                    {
                        DisposeHandoff();
                        MessageBox.Show(this, "The administrator copy could not start. Your widget is still running; you can try Enable app details again.",
                            "NetSpeed Widget", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                };
                handoffTimer.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                DisposeHandoff();
                if (ex.NativeErrorCode != 1223)
                    MessageBox.Show(this, "Could not enable app details.\n\n" + ex.Message, "NetSpeed Widget", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                DisposeHandoff();
                MessageBox.Show(this, "Could not enable app details.\n\n" + ex.Message, "NetSpeed Widget", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void DisposeHandoff()
        {
            if (handoffTimer != null) { handoffTimer.Stop(); handoffTimer.Dispose(); handoffTimer = null; }
            if (handoffReady != null) { handoffReady.Dispose(); handoffReady = null; }
            if (handoffChild != null) { handoffChild.Dispose(); handoffChild = null; }
        }

        private void BeginDrag(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            // Let Windows own the move loop, including capture and mixed-display coordinates.
            ReleaseCapture();
            SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero); // WM_NCLBUTTONDOWN / HTCAPTION
            KeepOnScreen();
            SaveSettings();
        }

        private void RestorePosition()
        {
            if (settings.X.HasValue && settings.Y.HasValue) Location = new Point(settings.X.Value, settings.Y.Value);
            else PlaceDefault();
            KeepOnScreen();
        }

        private void PlaceDefault()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - S(24), area.Bottom - Height - S(24));
        }

        private void KeepOnScreen()
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }

        private void RememberPosition() { settings.X = Left; settings.Y = Top; }
        private void SaveSettings()
        {
            RememberPosition();
            if (!settings.Save() && !closing)
                tray.ShowBalloonTip(3000, "Settings could not be saved", "Keep NetSpeed in a folder you can write to, such as your desktop.", ToolTipIcon.Info);
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            if (!IsDisposed && IsHandleCreated)
            {
                try { BeginInvoke((Action)delegate { if (!IsDisposed) KeepOnScreen(); }); }
                catch (InvalidOperationException) { }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { HideWidget(); return true; }
            if (keyData == (Keys.Shift | Keys.F10) || keyData == Keys.Apps) { ShowMenu(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Program.ShowMessage) { ShowWidget(); return; }
            base.WndProc(ref m);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            processMonitor.Stop();
            if (!wasHidden) RememberPosition();
            settings.Save();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                timer.Stop(); timer.Dispose();
                tray.Visible = false; tray.Dispose();
                processMonitor.Dispose(); DisposeHandoff();
                monitor.Dispose(); menu.Dispose(); tips.Dispose();
                foreach (Font font in fonts) font.Dispose();
                if (appIcon != null) { appIcon.Dispose(); appIcon = null; }
            }
            base.Dispose(disposing);
        }

        private static Icon CreateIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush back = new SolidBrush(Background)) g.FillEllipse(back, 0, 0, 32, 32);
                using (Pen down = new Pen(DownloadColor, 2.5f))
                using (Pen up = new Pen(UploadColor, 2.5f))
                {
                    g.DrawLine(down, 10, 8, 10, 23);
                    g.DrawLines(down, new[] { new Point(5, 18), new Point(10, 23), new Point(15, 18) });
                    g.DrawLine(up, 22, 24, 22, 9);
                    g.DrawLines(up, new[] { new Point(17, 14), new Point(22, 9), new Point(27, 14) });
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }
}
