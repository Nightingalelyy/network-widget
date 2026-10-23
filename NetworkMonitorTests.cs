using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetSpeedWidget
{
    internal static class NetworkMonitorTests
    {
        private static int passed;

        private static int Main()
        {
            try
            {
                Run("Native structure alignment", NativeLayout);
                Run("Actual elapsed time and unsigned 64-bit deltas", ElapsedAndLargeCounters);
                Run("Aggregate hardware once, including multiple adapters", HardwareAggregation);
                Run("Explicit adapter and virtual fallback", AdapterSelection);
                Run("Counter resets, explicit reset, and long gaps", ResetsAndGaps);
                Run("New, missing, disconnected, and reconnected adapters", AdapterLifecycle);
                Run("Changed adapter generation and network", AdapterGeneration);
                Run("Read errors clear baselines and recover", ReadFailure);
                Run("Zero and backwards elapsed time", InvalidTime);
                Run("Native Windows enumeration and live sample", NativeSmokeTest);
                Console.WriteLine("PASS: " + passed + " network monitor tests.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error);
                return 1;
            }
        }

        private static void Run(string name, Action action)
        {
            action();
            passed++;
            Console.WriteLine("PASS: " + name);
        }

        private static void NativeLayout()
        {
            Equal(1352, Marshal.SizeOf(typeof(NativeInterfaces.MibIfRow2)), "row size");
            Equal(8, Marshal.OffsetOf(typeof(NativeInterfaces.MibIfTableStart), "FirstRow").ToInt32(), "table alignment");
            Equal(1208, Marshal.OffsetOf(typeof(NativeInterfaces.MibIfRow2), "InOctets").ToInt32(), "download offset");
            Equal(1280, Marshal.OffsetOf(typeof(NativeInterfaces.MibIfRow2), "OutOctets").ToInt32(), "upload offset");
        }

        private static void ElapsedAndLargeCounters()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 9007199254740992UL, 9007199254740992UL);
            f.Adapters.Add(a);
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "first baseline");
            f.Seconds = 0.5;
            a.Received += 101;
            a.Sent += 33;
            NetworkSample sample = f.Monitor.Sample(null);
            Equal(202, sample.DownloadBytesPerSecond, "subsecond download");
            Equal(66, sample.UploadBytesPerSecond, "subsecond upload");
            Assert(sample.IsConnected && sample.ConnectionLabel == "A", "connection label");
        }

        private static void HardwareAggregation()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("Wi-Fi", true, 100, 10);
            AdapterSnapshot b = Adapter("Ethernet", true, 200, 20);
            AdapterSnapshot vpn = Adapter("VPN", false, 100000, 100000);
            f.Adapters.AddRange(new AdapterSnapshot[] { a, b, vpn });
            f.Monitor.Sample(null);
            f.Seconds = 2;
            a.Received += 1000; a.Sent += 200;
            b.Received += 400; b.Sent += 800;
            vpn.Received += 100000; vpn.Sent += 100000;
            NetworkSample sample = f.Monitor.Sample(null);
            Equal(700, sample.DownloadBytesPerSecond, "hardware aggregate");
            Equal(500, sample.UploadBytesPerSecond, "hardware upload aggregate");
            Assert(sample.ConnectionLabel == "2 adapters", "aggregate label");
        }

        private static void AdapterSelection()
        {
            Fixture f = new Fixture();
            AdapterSnapshot lan = Adapter("LAN", true, 10, 10);
            AdapterSnapshot vpn = Adapter("VPN", false, 10, 10);
            vpn.IsTunnel = true;
            f.Adapters.AddRange(new AdapterSnapshot[] { lan, vpn });
            f.Monitor.Sample("vpn");
            f.Seconds = 1;
            lan.Received += 1000; vpn.Received += 300;
            Equal(300, f.Monitor.Sample("VPN").DownloadBytesPerSecond, "explicit VPN");
            f.Adapters.Remove(lan);
            f.Seconds = 2; vpn.Received += 60;
            Equal(60, f.Monitor.Sample(null).DownloadBytesPerSecond, "tunnel-only fallback");
            AdapterSnapshot virtualLan = Adapter("Virtual LAN", false, 0, 0);
            f.Adapters.Add(virtualLan);
            f.Seconds = 3; vpn.Received += 900;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "new non-tunnel baseline");
            f.Seconds = 4; virtualLan.Received += 80; vpn.Received += 900;
            Equal(80, f.Monitor.Sample(null).DownloadBytesPerSecond, "virtual non-tunnel preferred");
            Assert(!f.Monitor.Sample("missing").IsConnected, "missing selection");
        }

        private static void ResetsAndGaps()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 1000, 1000);
            f.Adapters.Add(a);
            f.Monitor.Sample(null);
            f.Seconds = 1; a.Received = 100; a.Sent = 2000;
            NetworkSample reset = f.Monitor.Sample(null);
            Equal(0, reset.DownloadBytesPerSecond, "reset download");
            Equal(0, reset.UploadBytesPerSecond, "reset both directions together");
            f.Seconds = 2; a.Received += 42;
            Equal(42, f.Monitor.Sample(null).DownloadBytesPerSecond, "recover from reset");
            f.Seconds = 13; a.Received += 99999;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "long gap baseline");
            f.Seconds = 14; a.Received += 20;
            Equal(20, f.Monitor.Sample(null).DownloadBytesPerSecond, "recover from sleep");
            f.Monitor.Reset();
            f.Seconds = 15; a.Received += 50;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "explicit reset baseline");
        }

        private static void AdapterLifecycle()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 0, 0);
            f.Adapters.Add(a); f.Monitor.Sample(null);
            f.Seconds = 1; a.Received += 100;
            AdapterSnapshot b = Adapter("B", true, 9999999, 100000);
            f.Adapters.Add(b);
            Equal(100, f.Monitor.Sample(null).DownloadBytesPerSecond, "new adapter does not spike");
            f.Adapters.Remove(a);
            f.Seconds = 2; b.Received += 50;
            Equal(50, f.Monitor.Sample(null).DownloadBytesPerSecond, "removed adapter does not subtract");
            f.Adapters.Add(a);
            f.Seconds = 3; a.Received += 90000; b.IsConnected = false;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "reappeared adapter baseline");
            a.IsConnected = false; f.Seconds = 4;
            Assert(!f.Monitor.Sample(null).IsConnected, "all disconnected");
            a.IsConnected = true; f.Seconds = 5; a.Received += 60000;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "reconnection baseline");
            f.Seconds = 6; a.Received += 12;
            Equal(12, f.Monitor.Sample(null).DownloadBytesPerSecond, "reconnection resumes");
            AdapterSnapshot loop = Adapter("Loop", false, 0, 0);
            loop.IsEligible = false;
            f.Adapters.Add(loop);
            Assert(f.Monitor.GetAdapters().Count == 2, "list excludes loopback but retains disconnected adapters");
        }

        private static void AdapterGeneration()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 0, 0);
            f.Adapters.Add(a); f.Monitor.Sample(null);
            f.Seconds = 1; a.Luid = 2; a.Received = 90000;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "changed LUID baseline");
            f.Seconds = 2; a.NetworkGuid = Guid.NewGuid(); a.Received += 90000;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "changed network baseline");
        }

        private static void ReadFailure()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 0, 0);
            f.Adapters.Add(a); f.Monitor.Sample(null);
            f.FailRead = true; f.Seconds = 1;
            NetworkSample failed = f.Monitor.Sample(null);
            Assert(!failed.IsConnected && !String.IsNullOrEmpty(failed.Error), "reported read failure");
            f.FailRead = false; f.Seconds = 2; a.Received = 100000;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "read recovery baseline");
            f.Seconds = 3; a.Received += 5;
            Equal(5, f.Monitor.Sample(null).DownloadBytesPerSecond, "read recovery rate");
        }

        private static void InvalidTime()
        {
            Fixture f = new Fixture();
            AdapterSnapshot a = Adapter("A", true, 0, 0);
            f.Adapters.Add(a); f.Monitor.Sample(null);
            a.Received = 100;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "zero elapsed");
            f.Seconds = -1; a.Received = 200;
            Equal(0, f.Monitor.Sample(null).DownloadBytesPerSecond, "backwards elapsed");
            f.Seconds = 0; a.Received = 210;
            Equal(10, f.Monitor.Sample(null).DownloadBytesPerSecond, "clock recovery");
        }

        private static void NativeSmokeTest()
        {
            List<AdapterSnapshot> adapters = NativeInterfaces.Read();
            Assert(adapters.Count > 0, "Windows exposes at least loopback");
            foreach (AdapterSnapshot adapter in adapters)
                Assert(!String.IsNullOrEmpty(adapter.Id) && !String.IsNullOrEmpty(adapter.Name), "valid native adapter metadata");
            using (NetworkMonitor monitor = new NetworkMonitor())
            {
                monitor.GetAdapters();
                NetworkSample first = monitor.Sample(null);
                Assert(first.Error == null, "native initial sample: " + first.Error);
                Thread.Sleep(120);
                NetworkSample second = monitor.Sample(null);
                Assert(second.Error == null, "native second sample: " + second.Error);
                Assert(second.DownloadBytesPerSecond >= 0 && second.UploadBytesPerSecond >= 0, "non-negative native rates");
                Console.WriteLine("  Native sample: " + second.ConnectionLabel + ", down=" + second.DownloadBytesPerSecond.ToString("F1") + " B/s, up=" + second.UploadBytesPerSecond.ToString("F1") + " B/s");
            }
        }

        private static AdapterSnapshot Adapter(string id, bool hardware, ulong received, ulong sent)
        {
            return new AdapterSnapshot { Id = id, Name = id, IsHardware = hardware, IsConnected = true, IsEligible = true, Received = received, Sent = sent, Luid = 1 };
        }

        private static void Equal(double expected, double actual, string message)
        {
            if (Double.IsNaN(actual) || Math.Abs(expected - actual) > 0.000001)
                throw new Exception(message + ": expected " + expected + ", received " + actual);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        private sealed class Fixture
        {
            internal readonly List<AdapterSnapshot> Adapters = new List<AdapterSnapshot>();
            internal double Seconds;
            internal bool FailRead;
            internal readonly NetworkMonitor Monitor;

            internal Fixture()
            {
                Monitor = new NetworkMonitor(delegate
                {
                    if (FailRead) throw new Win32Exception(5);
                    return Adapters;
                }, delegate { return Seconds; });
            }
        }
    }
}
