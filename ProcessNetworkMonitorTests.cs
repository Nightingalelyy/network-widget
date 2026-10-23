using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace NetSpeedWidget
{
    internal static class ProcessNetworkMonitorTests
    {
        private static int passed;
        private static int Main()
        {
            try
            {
                Run("ETW native layouts", NativeLayouts);
                Run("TCP/UDP IPv4/IPv6 payload attribution and direction", DecodeTraffic);
                Run("Retransmits, unrelated events, and malformed data are excluded", DecodeFiltering);
                Run("Monotonic rate intervals and unsigned byte totals", RateIntervals);
                Run("Recent rows expire, idle rows read zero, and sleep clears counters", RecentAndSleep);
                Run("PID reuse cannot mislabel buffered traffic", ProcessReuse);
                Run("Lost events and bounded process storage report approximate state", LossAndBounds);
                Run("Native process name cache verifies process creation time", NativeProcessName);
                Run("An active owner blocks another monitor without touching its session", ActiveOwner);
                Run("Native start/stop, elevation status, and rapid reopen", NativeSmoke);
                Console.WriteLine("PASS: " + passed + " process traffic tests.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error);
                return 1;
            }
        }

        private static void Run(string name, Action test)
        {
            test(); passed++; Console.WriteLine("PASS: " + name);
        }

        private static void NativeLayouts()
        {
            Equal(48, Marshal.SizeOf(typeof(EtwNative.WnodeHeader)), "WNODE_HEADER");
            Equal(80, Marshal.SizeOf(typeof(EtwNative.EventHeader)), "EVENT_HEADER");
            Equal(16, Marshal.SizeOf(typeof(EtwNative.EventDescriptor)), "EVENT_DESCRIPTOR");
            Equal(172, Marshal.SizeOf(typeof(EtwNative.TimeZoneInformation)), "TIME_ZONE_INFORMATION");
            Equal(IntPtr.Size == 8 ? 120 : 116, Marshal.SizeOf(typeof(EtwNative.EventTraceProperties)), "EVENT_TRACE_PROPERTIES");
            Equal(IntPtr.Size == 8 ? 112 : 104, Marshal.SizeOf(typeof(EtwNative.EventRecord)), "EVENT_RECORD");
            Equal(IntPtr.Size == 8 ? 448 : 416, Marshal.SizeOf(typeof(EtwNative.EventTraceLogfile)), "EVENT_TRACE_LOGFILE");
            Equal(IntPtr.Size == 8 ? 424 : 400, Marshal.OffsetOf(typeof(EtwNative.EventTraceLogfile), "EventRecordCallback").ToInt32(), "callback offset");
            Equal(IntPtr.Size == 8 ? 96 : 92, Marshal.OffsetOf(typeof(EtwNative.EventRecord), "UserData").ToInt32(), "payload pointer offset");
            // OpenTrace marshals this nested structure in both directions. Exercise the null
            // fixed arrays used by a newly initialized logfile even without elevated tracing.
            IntPtr logfileMemory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EtwNative.EventTraceLogfile)));
            bool marshalled = false;
            try
            {
                EtwNative.EventTraceLogfile logfile = new EtwNative.EventTraceLogfile();
                logfile.LoggerName = "NetSpeedWidget.ProcessTraffic.MarshalTest";
                Marshal.StructureToPtr(logfile, logfileMemory, false);
                marshalled = true;
                EtwNative.EventTraceLogfile roundTrip = (EtwNative.EventTraceLogfile)Marshal.PtrToStructure(logfileMemory, typeof(EtwNative.EventTraceLogfile));
                Assert(roundTrip.LoggerName == logfile.LoggerName && roundTrip.LogfileHeader.TimeZone.StandardName.Length == 32, "nested logfile roundtrip");
            }
            finally
            {
                if (marshalled) Marshal.DestroyStructure(logfileMemory, typeof(EtwNative.EventTraceLogfile));
                Marshal.FreeHGlobal(logfileMemory);
            }
            using (EtwNative.PropertiesBuffer buffer = new EtwNative.PropertiesBuffer("NetSpeedWidget.ProcessTraffic.Test", Guid.NewGuid()))
            {
                EtwNative.EventTraceProperties p = buffer.Read();
                Assert(p.LogFileNameOffset == 0, "no trace file");
                Assert(p.LogFileMode == (0x100 | 0x02000000), "real-time custom system logger");
                Assert(p.EnableFlags == (0x10000 | 0x10000000), "only network events, no disk IO");
                Assert(p.Wnode.Guid != new Guid("9e814aad-3204-11d2-9a82-006008a86939"), "not global kernel logger GUID");
            }
        }

        private static void DecodeTraffic()
        {
            foreach (Guid provider in new Guid[] { ProcessNetworkDecoder.TcpProvider, ProcessNetworkDecoder.UdpProvider })
            foreach (byte opcode in new byte[] { 10, 11, 26, 27 })
            {
                using (Payload payload = new Payload(4321, 0xF0000000U))
                {
                    EtwNative.EventRecord record = Record(provider, opcode, 2, payload.Pointer, 64);
                    record.Header.ProcessId = 4; // Deliberately different from the real payload PID.
                    ProcessNetworkEvent decoded;
                    bool malformed;
                    Assert(ProcessNetworkDecoder.TryDecode(record, out decoded, out malformed), "decode accepted");
                    Assert(!malformed && decoded.ProcessId == 4321, "payload PID wins over header PID");
                    Equal(0xF0000000U, decoded.Bytes, "unsigned payload size");
                    Assert(decoded.IsReceive == (opcode == 11 || opcode == 27), "direction");
                    Equal(123456789, decoded.UtcFileTime, "event timestamp");
                }
            }
            using (Payload payload = new Payload(22, 100))
            {
                ProcessNetworkEvent decoded; bool malformed;
                Assert(ProcessNetworkDecoder.TryDecode(Record(ProcessNetworkDecoder.TcpProvider, 10, 1, payload.Pointer, 20), out decoded, out malformed), "documented v1 IPv4 layout");
            }
        }

        private static void DecodeFiltering()
        {
            using (Payload payload = new Payload(1, 100))
            {
                foreach (byte opcode in new byte[] { 12, 13, 14, 15, 16, 17, 18, 28, 29, 30, 31, 32, 34 })
                {
                    ProcessNetworkEvent decoded; bool malformed;
                    Assert(!ProcessNetworkDecoder.TryDecode(Record(ProcessNetworkDecoder.TcpProvider, opcode, 2, payload.Pointer, 64), out decoded, out malformed), "non-data opcode " + opcode);
                    Assert(!malformed, "ignored non-data is not a loss");
                }
                ProcessNetworkEvent item; bool bad;
                Assert(!ProcessNetworkDecoder.TryDecode(Record(Guid.NewGuid(), 10, 2, payload.Pointer, 64), out item, out bad) && !bad, "unrelated provider");
                Assert(!ProcessNetworkDecoder.TryDecode(Record(ProcessNetworkDecoder.UdpProvider, 10, 2, payload.Pointer, 7), out item, out bad) && bad, "truncated payload");
                Assert(!ProcessNetworkDecoder.TryDecode(Record(ProcessNetworkDecoder.UdpProvider, 10, 2, IntPtr.Zero, 64), out item, out bad) && bad, "null payload");
                Assert(!ProcessNetworkDecoder.TryDecode(Record(ProcessNetworkDecoder.UdpProvider, 10, 99, payload.Pointer, 64), out item, out bad) && bad, "unknown schema");
            }
        }

        private static void RateIntervals()
        {
            ProcessTrafficAggregator a = new ProcessTrafficAggregator(); a.Reset(10);
            a.Add(5, 1000, true, 200, 10.1); a.Add(5, 500, false, 201, 10.2);
            a.Add(6, UInt32.MaxValue, false, 202, 10.2); a.Add(6, UInt32.MaxValue, false, 203, 10.3);
            bool approximate;
            List<ProcessTrafficRow> rows = a.Sample(10.5, Resolve, out approximate);
            Equal(2000, Find(rows, 5).DownloadBytesPerSecond, "subsecond download");
            Equal(1000, Find(rows, 5).UploadBytesPerSecond, "subsecond upload");
            Equal(UInt32.MaxValue * 4.0, Find(rows, 6).UploadBytesPerSecond, "larger than 32-bit total");
            Assert(!approximate, "normal sample exact");
            a.Add(5, 60, true, 300, 10.6);
            Assert(a.Sample(10.5, Resolve, out approximate).Count == 0, "no division by zero");
            Equal(30, Find(a.Sample(12.5, Resolve, out approximate), 5).DownloadBytesPerSecond, "nonuniform elapsed time");
        }

        private static void RecentAndSleep()
        {
            ProcessTrafficAggregator a = new ProcessTrafficAggregator(); a.Reset(0);
            a.Add(9, 100, true, 200, 0.5);
            bool approx;
            Equal(100, Find(a.Sample(1, Resolve, out approx), 9).DownloadBytesPerSecond, "first interval");
            Equal(0, Find(a.Sample(2, Resolve, out approx), 9).DownloadBytesPerSecond, "idle zero");
            Assert(a.Sample(6, Resolve, out approx).Count == 0, "recent expiration");
            a.Add(9, 100000, true, 900, 7);
            Assert(a.Sample(20, Resolve, out approx).Count == 0, "sleep gap discards stale totals");
            a.Add(9, 12, false, 1000, 20.5);
            Equal(12, Find(a.Sample(21, Resolve, out approx), 9).UploadBytesPerSecond, "resume fresh interval");
        }

        private static void ProcessReuse()
        {
            ProcessTrafficAggregator a = new ProcessTrafficAggregator(); a.Reset(0);
            a.Add(20, 100, true, 100, 0.5);
            bool approximate;
            List<ProcessTrafficRow> rows = a.Sample(1, delegate(int pid) { return new ProcessIdentity { Name = "new-process", CreatedUtcFileTime = 200 }; }, out approximate);
            Assert(rows.Count == 0 && approximate, "old buffered event never attributed to reused PID");
            a.Add(20, 50, true, 250, 1.5);
            rows = a.Sample(2, delegate(int pid) { return new ProcessIdentity { Name = "new-process", CreatedUtcFileTime = 200 }; }, out approximate);
            Assert(rows.Count == 1 && rows[0].Name == "new-process", "new PID incarnation recognized");
            Equal(50, rows[0].DownloadBytesPerSecond, "new incarnation only");
            a.Add(21, 10, false, 260, 2.1);
            rows = a.Sample(3, delegate(int pid) { return null; }, out approximate);
            Assert(Find(rows, 21).Name == "Process 21", "exited or inaccessible process remains explicit");
        }

        private static void LossAndBounds()
        {
            ProcessTrafficAggregator a = new ProcessTrafficAggregator(); a.Reset(0); a.MarkApproximate();
            bool approximate;
            a.Sample(1, Resolve, out approximate); Assert(approximate, "event loss is visible");
            a.Reset(1); a.Sample(2, Resolve, out approximate); Assert(!approximate, "restart resets loss state");
            for (int i = 1; i <= 4097; i++) a.Add(i, 1, true, 100, 2.2);
            List<ProcessTrafficRow> rows = a.Sample(3, Resolve, out approximate);
            Assert(rows.Count == 4096 && approximate, "bounded storage exposes dropped rows");
        }

        private static void NativeProcessName()
        {
            int pid = Process.GetCurrentProcess().Id;
            ProcessNameCache cache = new ProcessNameCache();
            ProcessIdentity a = cache.Resolve(pid);
            ProcessIdentity b = cache.Resolve(pid);
            Assert(a != null && !String.IsNullOrEmpty(a.Name) && a.CreatedUtcFileTime > 0, "actual process name and creation time");
            Assert(Object.ReferenceEquals(a, b), "same creation token reuses cached name");
        }

        private static void ActiveOwner()
        {
            string name = "NetSpeedWidget.ProcessTraffic.Tests.Owner" + Process.GetCurrentProcess().Id;
            using (Mutex owner = new Mutex(true, "Local\\" + name + ".Owner"))
            using (ProcessNetworkMonitor monitor = new ProcessNetworkMonitor(name))
            {
                monitor.Start();
                ProcessTrafficSample sample = AwaitState(monitor);
                Assert(!sample.IsRunning && !sample.NeedsElevation && sample.Error != null && sample.Error.Contains("Another widget"), "active owner preserved");
                owner.ReleaseMutex();
            }
        }

        private static void NativeSmoke()
        {
            string name = "NetSpeedWidget.ProcessTraffic.Tests.Smoke" + Process.GetCurrentProcess().Id;
            Stopwatch latency = Stopwatch.StartNew();
            using (ProcessNetworkMonitor monitor = new ProcessNetworkMonitor(name))
            {
                for (int i = 0; i < 12; i++) { monitor.Start(); monitor.Stop(); }
                monitor.Start();
                Assert(latency.ElapsedMilliseconds < 1000, "Start/Stop calls stay nonblocking");
                ProcessTrafficSample sample = AwaitState(monitor);
                Assert(sample.IsRunning || sample.NeedsElevation, "native startup must succeed or clearly request elevation: " + sample.Error);
                Console.WriteLine("  Native state: " + sample.Status + (sample.Error == null ? "" : " (" + sample.Error + ")"));
                if (sample.IsRunning)
                {
                    Thread.Sleep(1200);
                    sample = monitor.Sample();
                    Assert(sample.IsRunning, "native consumer stays active");
                    Console.WriteLine("  Active/recent processes: " + sample.Rows.Count);
                }
                monitor.Stop();
                Assert(!monitor.Sample().IsRunning, "stop is visible immediately");
            }
            using (EtwNative.PropertiesBuffer properties = new EtwNative.PropertiesBuffer(name, Guid.Empty))
            {
                uint result = EtwNative.ControlTrace(0, name, properties.Pointer, 0);
                Assert(result == 4201 || result == 5, "no owned session remains after disposal; query=" + result);
            }
        }

        private static ProcessTrafficSample AwaitState(ProcessNetworkMonitor monitor)
        {
            Stopwatch timeout = Stopwatch.StartNew();
            ProcessTrafficSample sample;
            do
            {
                sample = monitor.Sample();
                if (sample.IsRunning || sample.Error != null) return sample;
                Thread.Sleep(50);
            } while (timeout.ElapsedMilliseconds < 8000);
            throw new Exception("Native monitor did not report startup status: " + sample.Status);
        }

        private static ProcessIdentity Resolve(int pid) { return new ProcessIdentity { Name = "App " + pid }; }
        private static ProcessTrafficRow Find(List<ProcessTrafficRow> rows, int pid)
        {
            foreach (ProcessTrafficRow row in rows) if (row.ProcessId == pid) return row;
            throw new Exception("Missing process row " + pid);
        }

        private static EtwNative.EventRecord Record(Guid provider, byte opcode, byte version, IntPtr payload, ushort length)
        {
            EtwNative.EventRecord record = new EtwNative.EventRecord();
            record.Header.ProviderId = provider;
            record.Header.Descriptor.Opcode = opcode;
            record.Header.Descriptor.Version = version;
            record.Header.TimeStamp = 123456789;
            record.UserData = payload; record.UserDataLength = length;
            return record;
        }

        private static void Equal(double expected, double actual, string message)
        {
            if (Double.IsNaN(actual) || Math.Abs(expected - actual) > 0.000001) throw new Exception(message + ": expected " + expected + ", actual " + actual);
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

        private sealed class Payload : IDisposable
        {
            internal IntPtr Pointer;
            internal Payload(uint pid, uint bytes)
            {
                Pointer = Marshal.AllocHGlobal(64);
                Marshal.Copy(new byte[64], 0, Pointer, 64);
                Marshal.WriteInt32(Pointer, unchecked((int)pid));
                Marshal.WriteInt32(Pointer, 4, unchecked((int)bytes));
            }
            public void Dispose() { Marshal.FreeHGlobal(Pointer); }
        }
    }
}
