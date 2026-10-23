using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace NetSpeedWidget
{
    /// <summary>The expandable, read-only view of process-attributed network events.</summary>
    internal sealed class ProcessTrafficView : UserControl
    {
        private static readonly Color Background = Color.FromArgb(22, 27, 35);
        private static readonly Color Foreground = Color.FromArgb(237, 243, 248);
        private static readonly Color Muted = Color.FromArgb(144, 157, 176);
        private static readonly Color DownloadColor = Color.FromArgb(96, 226, 181);
        private static readonly Color UploadColor = Color.FromArgb(124, 177, 255);
        private static readonly Color Surface = Color.FromArgb(29, 35, 45);
        private readonly float scale;
        private readonly List<Font> fonts = new List<Font>();
        private readonly ToolTip tips = new ToolTip();
        private readonly Button appsButton;
        private readonly Button processesButton;
        private readonly TrafficGrid grid;
        private readonly Panel emptyPanel;
        private readonly Label emptyTitle;
        private readonly Label emptyDescription;
        private readonly Button enableButton;
        private readonly Label statusLabel;
        private readonly Label scopeLabel;
        private readonly Label noteLabel;
        private ProcessTrafficSample currentSample;
        private bool useBits;
        private bool grouped = true;
        private SortField sortField = SortField.Total;
        private bool descending = true;

        public event EventHandler EnableRequested;

        public ProcessTrafficView(float displayScale)
        {
            scale = Math.Max(1f, displayScale);
            AutoScaleMode = AutoScaleMode.None;
            Size = new Size(S(480), S(325));
            BackColor = Background;
            ForeColor = Foreground;
            DoubleBuffered = true;
            AccessibleName = "Network activity by app";
            AccessibleDescription = "Live upload and download for all adapters. Switch between apps and individual processes.";

            Label heading = MakeLabel("App activity", 0, 5, 200, 23, 11, FontStyle.Bold, Foreground);
            tips.SetToolTip(heading, "Starts sorted by combined download and upload. Click a column header to change the order.");
            appsButton = MakeButton("Apps", 307, 0, 70, 29);
            processesButton = MakeButton("Processes", 381, 0, 99, 29);
            appsButton.AccessibleDescription = "Combine processes with the same executable name.";
            processesButton.AccessibleDescription = "Show each individual process and its process ID.";
            appsButton.Click += delegate { SetGrouped(true); };
            processesButton.Click += delegate { SetGrouped(false); };

            grid = new TrafficGrid
            {
                Location = new Point(0, S(39)),
                Size = new Size(S(480), S(205)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackgroundColor = Background,
                BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(41, 49, 62),
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AllowUserToResizeColumns = false,
                AllowUserToOrderColumns = false,
                AutoGenerateColumns = false,
                RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = S(29),
                RowTemplate = { Height = S(28), MinimumHeight = S(28) },
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
                ScrollBars = ScrollBars.Vertical,
                EnableHeadersVisualStyles = false,
                AccessibleName = "Live app network speeds",
                AccessibleDescription = "Use arrow keys to select rows. Column headers sort app, download, and upload values."
            };
            Font tableFont = MakeFont(9, FontStyle.Regular);
            grid.Font = tableFont;
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = tableFont,
                BackColor = Background,
                ForeColor = Foreground,
                SelectionBackColor = Color.FromArgb(44, 58, 75),
                SelectionForeColor = Foreground,
                Padding = new Padding(S(7), 0, S(7), 0),
                WrapMode = DataGridViewTriState.False
            };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                Font = MakeFont(8.5f, FontStyle.Bold),
                BackColor = Surface,
                ForeColor = Muted,
                SelectionBackColor = Surface,
                SelectionForeColor = Muted,
                Padding = new Padding(S(7), 0, S(7), 0)
            };
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Process", HeaderText = "App", FillWeight = 46,
                MinimumWidth = S(120), SortMode = DataGridViewColumnSortMode.Programmatic
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Download", HeaderText = "Download", FillWeight = 27,
                MinimumWidth = S(105), SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = DownloadColor },
                HeaderCell = { Style = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } }
            });
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "Upload", HeaderText = "Upload", FillWeight = 27,
                MinimumWidth = S(105), SortMode = DataGridViewColumnSortMode.Programmatic,
                DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, ForeColor = UploadColor },
                HeaderCell = { Style = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight } }
            });
            grid.Columns[0].ToolTipText = "Sort by executable name";
            grid.Columns[1].ToolTipText = "Sort by download speed. Click again to reverse.";
            grid.Columns[2].ToolTipText = "Sort by upload speed. Click again to reverse.";
            grid.ColumnHeaderMouseClick += OnColumnHeaderClick;
            Controls.Add(grid);

            // Use separate states instead of overlaying the native grid, so the action is never obscured.
            emptyPanel = new Panel { BackColor = Background, TabStop = false };
            Controls.Add(emptyPanel);
            emptyTitle = MakeEmptyLabel("See network activity by app", 13, 23, FontStyle.Bold, Foreground);
            emptyDescription = MakeEmptyLabel("Enable app details to start measuring traffic.", 43, 54, FontStyle.Regular, Muted);
            enableButton = new Button
            {
                Text = "Enable app details", Font = MakeFont(9, FontStyle.Bold),
                Size = new Size(S(164), S(31)), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(41, 57, 64), ForeColor = DownloadColor,
                Cursor = Cursors.Hand, AccessibleName = "Enable app details", TabStop = true
            };
            enableButton.FlatAppearance.BorderColor = Color.FromArgb(75, 119, 109);
            enableButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 72, 76);
            enableButton.Click += delegate
            {
                EventHandler handler = EnableRequested;
                if (handler != null) handler(this, EventArgs.Empty);
            };
            emptyPanel.Controls.Add(enableButton);
            emptyPanel.BringToFront();

            statusLabel = MakeLabel("App details are off", 0, 251, 480, 18, 8, FontStyle.Regular, Muted);
            scopeLabel = MakeLabel("All adapters · TCP + UDP", 0, 276, 480, 17, 8, FontStyle.Bold, Muted);
            noteLabel = MakeLabel("App rates can differ from totals due to VPNs and network overhead.", 0, 297, 480, 18, 8, FontStyle.Regular, Muted);
            statusLabel.Anchor = scopeLabel.Anchor = noteLabel.Anchor = AnchorStyles.Left | AnchorStyles.Bottom | AnchorStyles.Right;
            appsButton.Anchor = processesButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            tips.SetToolTip(scopeLabel, "App details cover every adapter. The main widget's adapter selection does not filter this table.");
            tips.SetToolTip(noteLabel, "These are process-attributed TCP and UDP events, not raw disk or process I/O. VPNs, shared services, and network overhead can make app rates differ from interface totals.");
            Resize += delegate { LayoutEmptyState(); };
            LayoutEmptyState();
            UpdateModeButtons();
            UpdateSample(null, false);
        }

        public void UpdateSample(ProcessTrafficSample sample, bool showBits)
        {
            currentSample = sample;
            useBits = showBits;
            bool running = sample != null && sample.IsRunning;
            List<DisplayRow> rows = running ? BuildRows(sample.Rows) : new List<DisplayRow>();
            bool noRows = rows.Count == 0;
            grid.Visible = !noRows;
            emptyPanel.Visible = noRows;
            grid.TabStop = !noRows;
            UpdateRows(rows);
            bool needsElevation = sample != null && sample.NeedsElevation;
            string error = sample == null ? "" : sample.Error ?? "";
            string status = sample == null ? "" : sample.Status ?? "";
            statusLabel.ForeColor = error.Length > 0 ? Color.FromArgb(235, 180, 104) : Muted;
            if (status.Length == 0)
                status = running ? (rows.Count == 0 ? "Listening for app traffic…" : "Live app traffic") : "App details are off";
            statusLabel.Text = status;
            tips.SetToolTip(statusLabel, error.Length > 0 ? status + "\n" + error : status + "\n" + SortDescription());
            noteLabel.Text = sample != null && sample.IsApproximate
                ? "Estimated app rates · VPNs and overhead can differ from totals."
                : "App rates can differ from totals due to VPNs and network overhead.";

            if (noRows)
            {
                enableButton.Visible = !running;
                if (running)
                {
                    emptyTitle.Text = "Waiting for app traffic";
                    emptyDescription.Text = error.Length > 0 ? error : "Open a website or use an app that sends data.\nActive and recently active apps will appear here.";
                }
                else if (error.Length > 0)
                {
                    emptyTitle.Text = needsElevation ? "Permission is needed for app details" : "App details are unavailable";
                    emptyDescription.Text = needsElevation
                        ? "Allow the Windows administrator prompt to see per-app traffic.\nThe main speed display continues working."
                        : error;
                }
                else
                {
                    emptyTitle.Text = "See network activity by app";
                    emptyDescription.Text = "Enable app details to measure live upload and download.\nWindows may ask for administrator permission.";
                }
                enableButton.Text = error.Length > 0 && !needsElevation ? "Retry app details" : "Enable app details";
                enableButton.AccessibleName = enableButton.Text;
                tips.SetToolTip(emptyDescription, error.Length > 0 ? error : emptyDescription.Text);
            }
        }

        private void SetGrouped(bool value)
        {
            if (grouped == value) return;
            grouped = value;
            grid.Columns[0].HeaderText = grouped ? "App" : "Process · PID";
            grid.AccessibleName = grouped ? "Live app network speeds" : "Live process network speeds";
            UpdateModeButtons();
            UpdateSample(currentSample, useBits);
            if (grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = 0;
        }

        private void UpdateModeButtons()
        {
            StyleMode(appsButton, grouped);
            StyleMode(processesButton, !grouped);
        }

        private static void StyleMode(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(46, 57, 71) : Background;
            button.ForeColor = selected ? Foreground : Muted;
            button.FlatAppearance.BorderColor = selected ? Color.FromArgb(73, 91, 110) : Color.FromArgb(45, 54, 67);
            button.AccessibleName = button.Text + (selected ? ", selected" : "");
        }

        private void OnColumnHeaderClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.ColumnIndex < 0) return;
            SortField field = e.ColumnIndex == 0 ? SortField.Name : e.ColumnIndex == 1 ? SortField.Download : SortField.Upload;
            descending = field == sortField ? !descending : field != SortField.Name;
            sortField = field;
            UpdateSample(currentSample, useBits);
            if (grid.Rows.Count > 0) grid.FirstDisplayedScrollingRowIndex = 0;
        }

        private string SortDescription()
        {
            return "Sorted by " + (sortField == SortField.Total ? "combined download and upload" : sortField.ToString().ToLowerInvariant()) + (descending ? ", descending." : ", ascending.");
        }

        private List<DisplayRow> BuildRows(List<ProcessTrafficRow> source)
        {
            Dictionary<string, DisplayRow> indexed = new Dictionary<string, DisplayRow>(StringComparer.OrdinalIgnoreCase);
            if (source != null)
            {
                foreach (ProcessTrafficRow row in source)
                {
                    if (row == null) continue;
                    string name = ExecutableName(row.Name, row.ProcessId);
                    string key = grouped ? "app:" + name : "pid:" + row.ProcessId.ToString(CultureInfo.InvariantCulture);
                    DisplayRow display;
                    if (!indexed.TryGetValue(key, out display))
                    {
                        display = new DisplayRow { Key = key, Name = name, ProcessId = row.ProcessId };
                        indexed.Add(key, display);
                    }
                    display.ProcessIds.Add(row.ProcessId);
                    display.Download += ValidRate(row.DownloadBytesPerSecond);
                    display.Upload += ValidRate(row.UploadBytesPerSecond);
                }
            }
            return new List<DisplayRow>(indexed.Values);
        }

        private void UpdateRows(List<DisplayRow> rows)
        {
            string selectedKey = RowKey(grid.CurrentRow);
            int firstIndex = grid.FirstDisplayedScrollingRowIndex;
            string firstKey = firstIndex >= 0 && firstIndex < grid.Rows.Count ? RowKey(grid.Rows[firstIndex]) : null;
            Dictionary<string, DisplayRow> incoming = new Dictionary<string, DisplayRow>(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayRow row in rows) incoming[row.Key] = row;
            grid.SuspendLayout();
            try
            {
                for (int i = grid.Rows.Count - 1; i >= 0; i--)
                {
                    string key = RowKey(grid.Rows[i]);
                    DisplayRow value;
                    if (key == null || !incoming.TryGetValue(key, out value)) grid.Rows.RemoveAt(i);
                    else
                    {
                        UpdateRow(grid.Rows[i], value);
                        incoming.Remove(key);
                    }
                }
                foreach (DisplayRow value in incoming.Values)
                {
                    int index = grid.Rows.Add();
                    UpdateRow(grid.Rows[index], value);
                }
                if (grid.Rows.Count > 1) grid.Sort(new TrafficComparer(sortField, descending));
                foreach (DataGridViewColumn column in grid.Columns) column.HeaderCell.SortGlyphDirection = SortOrder.None;
                int sortColumn = sortField == SortField.Name ? 0 : sortField == SortField.Download ? 1 : sortField == SortField.Upload ? 2 : -1;
                if (sortColumn >= 0) grid.Columns[sortColumn].HeaderCell.SortGlyphDirection = descending ? SortOrder.Descending : SortOrder.Ascending;
                if (selectedKey != null)
                {
                    DataGridViewRow selected = FindRow(selectedKey);
                    if (selected != null)
                    {
                        grid.ClearSelection();
                        selected.Selected = true;
                        grid.CurrentCell = selected.Cells[0];
                    }
                }
                if (grid.Rows.Count > 0)
                {
                    // Stay at the top to follow fastest apps; a user-scrolled viewport follows its anchor row.
                    DataGridViewRow first = firstIndex > 0 && firstKey != null ? FindRow(firstKey) : null;
                    int restore = first == null ? Math.Max(0, Math.Min(firstIndex, grid.Rows.Count - 1)) : first.Index;
                    grid.FirstDisplayedScrollingRowIndex = restore;
                }
            }
            finally { grid.ResumeLayout(); }
        }

        private void UpdateRow(DataGridViewRow target, DisplayRow value)
        {
            target.Tag = value;
            string identity = grouped
                ? value.Name + (value.ProcessIds.Count > 1 ? " · " + value.ProcessIds.Count.ToString(CultureInfo.CurrentCulture) + " processes" : "")
                : value.Name + " · " + value.ProcessId.ToString(CultureInfo.CurrentCulture);
            SetCell(target.Cells[0], identity);
            SetCell(target.Cells[1], SpeedFormatter.Full(value.Download, useBits));
            SetCell(target.Cells[2], SpeedFormatter.Full(value.Upload, useBits));
            List<int> ids = new List<int>(value.ProcessIds);
            ids.Sort();
            List<string> idText = new List<string>();
            foreach (int id in ids) idText.Add(id.ToString(CultureInfo.CurrentCulture));
            target.Cells[0].ToolTipText = value.Name + "\nProcess " + (ids.Count == 1 ? "ID: " : "IDs: ") + string.Join(", ", idText.ToArray());
        }

        private static void SetCell(DataGridViewCell cell, string text)
        {
            if (!String.Equals(cell.Value as string, text, StringComparison.Ordinal)) cell.Value = text;
        }

        private DataGridViewRow FindRow(string key)
        {
            foreach (DataGridViewRow row in grid.Rows)
                if (String.Equals(RowKey(row), key, StringComparison.OrdinalIgnoreCase)) return row;
            return null;
        }

        private static string RowKey(DataGridViewRow row)
        {
            DisplayRow value = row == null ? null : row.Tag as DisplayRow;
            return value == null ? null : value.Key;
        }

        private static string ExecutableName(string name, int processId)
        {
            if (String.IsNullOrWhiteSpace(name)) return "Process " + processId.ToString(CultureInfo.CurrentCulture);
            name = name.Trim();
            int separator = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
            if (separator >= 0 && separator < name.Length - 1) name = name.Substring(separator + 1);
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name.Length == 0 ? "Process " + processId.ToString(CultureInfo.CurrentCulture) : name;
        }

        private static double ValidRate(double value)
        {
            return Double.IsNaN(value) || Double.IsInfinity(value) || value < 0 ? 0 : value;
        }

        private void LayoutEmptyState()
        {
            if (grid == null || emptyPanel == null) return;
            emptyPanel.Bounds = grid.Bounds;
            if (emptyTitle != null) emptyTitle.Width = Math.Max(0, emptyPanel.Width - S(24));
            if (emptyDescription != null) emptyDescription.Width = Math.Max(0, emptyPanel.Width - S(24));
            if (enableButton != null) enableButton.Location = new Point(Math.Max(0, (emptyPanel.Width - enableButton.Width) / 2), S(109));
        }

        private Label MakeLabel(string text, int x, int y, int width, int height, float size, FontStyle style, Color color)
        {
            Label label = new Label
            {
                Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(height)),
                Font = MakeFont(size, style), ForeColor = color, BackColor = Background,
                AutoEllipsis = true, UseMnemonic = false, TabStop = false, Margin = Padding.Empty
            };
            Controls.Add(label);
            return label;
        }

        private Label MakeEmptyLabel(string text, int y, int height, FontStyle style, Color color)
        {
            Label label = new Label
            {
                Text = text, Location = new Point(S(12), S(y)), Size = new Size(S(456), S(height)),
                Font = MakeFont(9, style), ForeColor = color, BackColor = Background,
                AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.TopCenter,
                TabStop = false, Margin = Padding.Empty
            };
            emptyPanel.Controls.Add(label);
            return label;
        }

        private Button MakeButton(string text, int x, int y, int width, int height)
        {
            Button button = new Button
            {
                Text = text, Bounds = new Rectangle(S(x), S(y), S(width), S(height)),
                Font = MakeFont(8.5f, FontStyle.Bold), FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand, ForeColor = Muted, BackColor = Background,
                TabStop = true, UseMnemonic = false
            };
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(42, 50, 63);
            Controls.Add(button);
            return button;
        }

        private int S(float value) { return (int)Math.Round(value * scale); }

        private Font MakeFont(float size, FontStyle style)
        {
            Font font = new Font("Segoe UI", size * (96f / 72f) * scale, style, GraphicsUnit.Pixel);
            fonts.Add(font);
            return font;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose();
            base.Dispose(disposing);
            if (disposing)
            {
                foreach (Font font in fonts) font.Dispose();
                fonts.Clear();
            }
        }

        private sealed class TrafficGrid : DataGridView
        {
            internal TrafficGrid() { DoubleBuffered = true; }
        }

        private sealed class DisplayRow
        {
            internal string Key;
            internal string Name;
            internal int ProcessId;
            internal readonly HashSet<int> ProcessIds = new HashSet<int>();
            internal double Download;
            internal double Upload;
        }

        private enum SortField { Total, Name, Download, Upload }

        private sealed class TrafficComparer : IComparer
        {
            private readonly SortField field;
            private readonly bool descending;
            internal TrafficComparer(SortField sort, bool reverse) { field = sort; descending = reverse; }
            public int Compare(object x, object y)
            {
                DisplayRow left = ((DataGridViewRow)x).Tag as DisplayRow;
                DisplayRow right = ((DataGridViewRow)y).Tag as DisplayRow;
                if (left == null || right == null) return 0;
                int result;
                if (field == SortField.Name) result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                else
                {
                    double a = field == SortField.Download ? left.Download : field == SortField.Upload ? left.Upload : left.Download + left.Upload;
                    double b = field == SortField.Download ? right.Download : field == SortField.Upload ? right.Upload : right.Download + right.Upload;
                    result = a.CompareTo(b);
                }
                if (result != 0) return descending ? -result : result;
                result = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
                return result != 0 ? result : StringComparer.OrdinalIgnoreCase.Compare(left.Key, right.Key);
            }
        }
    }
}
