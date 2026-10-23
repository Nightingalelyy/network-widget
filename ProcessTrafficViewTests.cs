using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NetSpeedWidget
{
    /// <summary>Deterministic UI checks using synthetic samples. Never starts tracing or opens a window.</summary>
    internal static class ProcessTrafficViewTests
    {
        private static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                TestGroupingAndProcesses();
                TestNumericSorting();
                TestStableSelectionAndScrolling();
                TestPermissionAndWaitingStates();
                TestUnitsAndScaling();
                string output = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(output);
                RenderFixtures(output);
                Console.WriteLine("Process view tests passed: " + assertions + " assertions. Synthetic fixtures: " + output);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void TestGroupingAndProcesses()
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                ProcessTrafficSample sample = Fixture();
                view.UpdateSample(sample, false);
                DataGridView table = Field<DataGridView>(view, "grid");
                Equal(22, table.Rows.Count, "All 22 app groups remain available, without a top-ten cutoff.");
                DataGridViewRow browser = Find(table, "ExampleBrowser");
                Contains("2 processes", Text(browser, 0), "Grouped apps expose the process count.");
                Equal("96.0 KB/s", Text(browser, 1), "Grouped download is the sum of both PIDs.");
                Equal("3.00 KB/s", Text(browser, 2), "Grouped upload is the sum of both PIDs.");
                Contains("ExampleBackup", Text(table.Rows[0], 0), "Default sort uses combined upload and download.");

                Invoke(view, "SetGrouped", false);
                Equal(23, table.Rows.Count, "Process mode retains both browser PIDs.");
                DataGridViewRow first = Find(table, "4101");
                DataGridViewRow second = Find(table, "4102");
                Contains("ExampleBrowser", Text(first, 0), "The first PID shows its executable name.");
                Contains("ExampleBrowser", Text(second, 0), "The second PID shows its executable name.");
                Equal("64.0 KB/s", Text(first, 1), "Individual process rates remain independent.");
                Equal("32.0 KB/s", Text(second, 1), "The second PID shows its own rate.");
                Contains("4101", first.Cells[0].ToolTipText, "PID remains available through the row tooltip.");
            }
        }

        private static void TestNumericSorting()
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                view.UpdateSample(Sample(
                    Row(1, "ExampleA", 100, 9000),
                    Row(2, "ExampleB", 9000, 100),
                    Row(3, "ExampleC", 5000, 8000)), false);
                DataGridView table = Field<DataGridView>(view, "grid");
                Equal("ExampleC", Text(table.Rows[0], 0), "Combined traffic drives the initial order.");
                ClickColumn(view, 1);
                Equal("ExampleB", Text(table.Rows[0], 0), "Download sorting compares numeric rates, not formatted text.");
                Equal(SortOrder.Descending, table.Columns[1].HeaderCell.SortGlyphDirection, "Download direction is visible.");
                ClickColumn(view, 1);
                Equal("ExampleA", Text(table.Rows[0], 0), "A second click reverses download sorting.");
                ClickColumn(view, 2);
                Equal("ExampleA", Text(table.Rows[0], 0), "Upload sorting starts with the highest upload.");
                Equal("ExampleB", Text(table.Rows[2], 0), "Low uploads sort after high uploads across unit changes.");
            }
        }

        private static void TestStableSelectionAndScrolling()
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                List<ProcessTrafficRow> rows = new List<ProcessTrafficRow>();
                for (int i = 0; i < 40; i++) rows.Add(Row(6000 + i, "ExampleApp" + i.ToString("00"), 100000 - i * 1000, i));
                ProcessTrafficSample sample = Sample(rows.ToArray());
                view.UpdateSample(sample, false);
                CreateOffscreenControls(view);
                DataGridView table = Field<DataGridView>(view, "grid");
                table.ClearSelection();
                table.CurrentCell = table.Rows[18].Cells[0];
                table.Rows[18].Selected = true;
                table.FirstDisplayedScrollingRowIndex = 12;
                Equal(12, table.FirstDisplayedScrollingRowIndex, "The test can scroll the hidden native grid.");
                string selected = Text(table.CurrentRow, 0);
                string first = Text(table.Rows[table.FirstDisplayedScrollingRowIndex], 0);
                for (int i = 0; i < rows.Count; i++) rows[i].DownloadBytesPerSecond = 1000 + i * 1000;
                view.UpdateSample(sample, false);
                Equal(selected, Text(table.CurrentRow, 0), "A refresh preserves selected process identity while rows reorder.");
                Equal(first, Text(table.Rows[table.FirstDisplayedScrollingRowIndex], 0), "A scrolled viewport stays anchored to its first visible app.");
                table.FirstDisplayedScrollingRowIndex = 0;
                rows[0].DownloadBytesPerSecond = 900000;
                view.UpdateSample(sample, false);
                Equal(0, table.FirstDisplayedScrollingRowIndex, "A viewport at the top keeps showing the fastest apps.");
                Equal("ExampleApp00", Text(table.Rows[0], 0), "The fastest app moves to the top after a refresh.");
                sample.Rows.RemoveAll(delegate(ProcessTrafficRow row) { return row.Name == selected; });
                view.UpdateSample(sample, false);
                Equal(39, table.Rows.Count, "Removing a recently active row does not break the update.");
            }
        }

        private static void TestPermissionAndWaitingStates()
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                view.UpdateSample(new ProcessTrafficSample
                {
                    Rows = new List<ProcessTrafficRow>(), IsRunning = false, NeedsElevation = true,
                    Status = "App details are off", Error = "Synthetic permission denial"
                }, false);
                True(Field<Panel>(view, "emptyPanel").Visible, "Permission denial shows an explanation.");
                Contains("Permission", Field<Label>(view, "emptyTitle").Text, "The permission state is named clearly.");
                Equal("Enable app details", Field<Button>(view, "enableButton").Text, "Permission denial has a clear enable action.");
                True(Field<Button>(view, "enableButton").Visible, "The enable button is reachable after denial.");
                int requested = 0;
                view.EnableRequested += delegate { requested++; };
                typeof(Button).GetMethod("OnClick", Hidden).Invoke(Field<Button>(view, "enableButton"), new object[] { EventArgs.Empty });
                Equal(1, requested, "The action raises one enable event without starting a process.");
                view.UpdateSample(Sample(), false);
                Contains("Waiting", Field<Label>(view, "emptyTitle").Text, "A running empty collector shows a waiting state.");
                True(!Field<Button>(view, "enableButton").Visible, "A running collector does not prompt for permission again.");
                view.UpdateSample(new ProcessTrafficSample
                {
                    Rows = new List<ProcessTrafficRow>(), IsRunning = false,
                    Error = "Synthetic tracing failure", Status = "Collection stopped"
                }, false);
                Equal("Retry app details", Field<Button>(view, "enableButton").Text, "Other collection failures offer retry.");
                Contains("Synthetic tracing failure", Field<Label>(view, "emptyDescription").Text, "A non-permission error stays visible.");
            }
        }

        private static void TestUnitsAndScaling()
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                ProcessTrafficSample sample = Sample(Row(1, "ExampleTransfer", 125000, 250000));
                view.UpdateSample(sample, false);
                DataGridView table = Field<DataGridView>(view, "grid");
                Equal("125 KB/s", Text(table.Rows[0], 1), "Byte units use decimal kilo prefixes.");
                view.UpdateSample(sample, true);
                Equal("1.00 Mbit/s", Text(table.Rows[0], 1), "Bit units convert download by eight.");
                Equal("2.00 Mbit/s", Text(table.Rows[0], 2), "Bit units convert upload by eight.");
            }
            using (ProcessTrafficView view = new ProcessTrafficView(1.5f))
            {
                view.UpdateSample(Fixture(), false);
                Equal(new Size(720, 488), view.Size, "The panel scales both dimensions at 150 percent.");
                True(Field<DataGridView>(view, "grid").Columns[1].Width > 150, "High-DPI speed columns retain readable width.");
                using (Bitmap image = new Bitmap(view.Width, view.Height)) view.DrawToBitmap(image, view.ClientRectangle);
            }
        }

        private static void RenderFixtures(string output)
        {
            using (ProcessTrafficView view = new ProcessTrafficView(1f))
            {
                view.UpdateSample(Fixture(), false);
                SaveImage(view, Path.Combine(output, "process-view-fixture.png"));
                Invoke(view, "SetGrouped", false);
                SaveImage(view, Path.Combine(output, "process-view-processes-fixture.png"));
                view.UpdateSample(new ProcessTrafficSample
                {
                    Rows = new List<ProcessTrafficRow>(), IsRunning = false, NeedsElevation = true,
                    Status = "Synthetic fixture · administrator permission declined", Error = "Synthetic permission denial"
                }, false);
                SaveImage(view, Path.Combine(output, "process-view-denied-fixture.png"));
            }
        }

        private static void SaveImage(ProcessTrafficView view, string path)
        {
            CreateOffscreenControls(view);
            using (Bitmap image = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(image, view.ClientRectangle);
                image.Save(path, ImageFormat.Png);
            }
        }

        private static void CreateOffscreenControls(Control view)
        {
            // Creating HWNDs for rendering is sufficient; no Form.Show, message loop, or desktop input is used.
            view.CreateControl();
            foreach (Control child in view.Controls) child.CreateControl();
        }

        private static ProcessTrafficSample Fixture()
        {
            List<ProcessTrafficRow> rows = new List<ProcessTrafficRow>
            {
                Row(4101, @"C:\Examples\ExampleBrowser.exe", 64000, 1000),
                Row(4102, "ExampleBrowser", 32000, 2000),
                Row(5201, "ExampleBackup", 15000, 220000)
            };
            for (int i = 0; i < 20; i++) rows.Add(Row(6100 + i, "ExampleApp" + i.ToString("00"), 18000 - i * 700, 4000 - i * 150));
            ProcessTrafficSample sample = Sample(rows.ToArray());
            sample.Status = "Synthetic fixture · 23 recently active processes";
            return sample;
        }

        private static ProcessTrafficRow Row(int id, string name, double down, double up)
        {
            return new ProcessTrafficRow { ProcessId = id, Name = name, DownloadBytesPerSecond = down, UploadBytesPerSecond = up };
        }

        private static ProcessTrafficSample Sample(params ProcessTrafficRow[] rows)
        {
            return new ProcessTrafficSample { Rows = new List<ProcessTrafficRow>(rows), IsRunning = true, IsApproximate = true, Status = "Synthetic test data" };
        }

        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, Hidden).GetValue(instance);
        }

        private static void Invoke(object instance, string name, params object[] args)
        {
            instance.GetType().GetMethod(name, Hidden).Invoke(instance, args);
        }

        private static void ClickColumn(ProcessTrafficView view, int index)
        {
            Invoke(view, "OnColumnHeaderClick", null, new DataGridViewCellMouseEventArgs(index, -1, 1, 1, new MouseEventArgs(MouseButtons.Left, 1, 1, 1, 0)));
        }

        private static DataGridViewRow Find(DataGridView table, string fragment)
        {
            foreach (DataGridViewRow row in table.Rows)
                if (Text(row, 0).IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return row;
            throw new InvalidOperationException("Missing fixture row: " + fragment);
        }

        private static string Text(DataGridViewRow row, int column) { return row.Cells[column].Value as string; }

        private static void True(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Contains(string expected, string actual, string message)
        {
            True(actual != null && actual.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0, message + " Actual: " + actual);
        }

        private static void Equal<T>(T expected, T actual, string message)
        {
            True(EqualityComparer<T>.Default.Equals(expected, actual), message + " Expected: " + expected + "; actual: " + actual);
        }
    }
}
