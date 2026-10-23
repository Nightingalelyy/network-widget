using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("NetSpeed Widget")]
[assembly: AssemblyDescription("Live upload and download speeds for your Windows desktop")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace NetSpeedWidget
{
    internal static class Program
    {
        internal static readonly int ShowMessage = RegisterWindowMessage("NetSpeedWidget.Show.02B36649-7E47-46A4-9EE4-383A26716AAD");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string message);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);

        internal static bool IsAdministrator
        {
            get
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            if (!CompleteHandoff(args)) return;
            bool first;
            Mutex instance;
            try { instance = new Mutex(true, @"Local\NetSpeedWidget.02B36649-7E47-46A4-9EE4-383A26716AAD", out first); }
            catch (UnauthorizedAccessException)
            {
                // A normal launch may not be allowed to open an elevated instance's mutex.
                PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                return;
            }
            using (Mutex mutex = instance)
            {
                if (!first)
                {
                    PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                try
                {
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Settings settings = Settings.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.xml"));
                    if (Array.Exists(args, delegate(string argument) { return argument == "--expanded"; })) settings.Expanded = true;
                    WidgetForm widget = new WidgetForm(settings);
                    // Optional taskbar presence is useful for desktop accessibility and UI testing.
                    if (Array.Exists(args, delegate(string argument) { return argument == "--taskbar"; })) widget.ShowInTaskbar = true;
                    Application.Run(widget);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("NetSpeed could not start.\n\n" + ex.Message, "NetSpeed Widget", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }

        // The original UI remains alive if UAC is cancelled or this child never starts.
        // A child acknowledges startup, waits for the old process to release its mutex,
        // and then follows the same normal startup path.
        private static bool CompleteHandoff(string[] args)
        {
            int index = Array.IndexOf(args, "--handoff");
            if (index < 0) return true;
            int parentId;
            Guid nonce;
            if (index + 2 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out nonce) ||
                !int.TryParse(args[index + 2], out parentId) || parentId == Process.GetCurrentProcess().Id)
                return false;
            try
            {
                using (Process parent = Process.GetProcessById(parentId))
                {
                    if (!string.Equals(parent.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return false;
                    using (EventWaitHandle ready = EventWaitHandle.OpenExisting(@"Local\NetSpeedWidget.Handoff." + nonce.ToString("N")))
                        ready.Set();
                    return parent.WaitForExit(15000);
                }
            }
            catch (Exception) { return false; }
        }
    }
}
