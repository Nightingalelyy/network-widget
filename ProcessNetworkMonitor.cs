using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace NetSpeedWidget
{
    public sealed class ProcessTrafficRow
    {
        public int ProcessId { get; set; }
        public string Name { get; set; }
        public double DownloadBytesPerSecond { get; set; }
        public double UploadBytesPerSecond { get; set; }
    }

    public sealed class ProcessTrafficSample
    {
        public List<ProcessTrafficRow> Rows { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool NeedsElevation { get; set; }
        public bool IsRunning { get; set; }
        public bool IsApproximate { get; set; }
    }

    /// <summary>Memory-only TCP/UDP ETW monitoring. Start/Stop do not block the UI.</summary>
    public sealed class ProcessNetworkMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly string sessionName;
        private readonly Guid sessionGuid;
        private readonly string mutexName;
        private readonly ProcessTrafficAggregator aggregate = new ProcessTrafficAggregator();
        private readonly ProcessNameCache names = new ProcessNameCache();
        private Thread worker;
        private NativeRun run;
        private bool wanted;
        private bool disposed;
        private bool running;
        private bool needsElevation;
        private int requestVersion;
        private string status = "App tracking paused";
        private string error;

        public ProcessNetworkMonitor()
            : this("NetSpeedWidget.ProcessTraffic.Session" + Process.GetCurrentProcess().SessionId.ToString(CultureInfo.InvariantCulture))
        {
        }

        // Separate deterministic names let native smoke tests avoid the production session.
        internal ProcessNetworkMonitor(string ownedSessionName)
        {
            if (String.IsNullOrEmpty(ownedSessionName) || !ownedSessionName.StartsWith("NetSpeedWidget.ProcessTraffic.", StringComparison.Ordinal))
                throw new ArgumentException("A product-scoped ETW session name is required.", "ownedSessionName");
            sessionName = ownedSessionName;
            mutexName = "Local\\" + ownedSessionName + ".Owner";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes("NetSpeedWidget ETW owner v1:" + ownedSessionName));
                byte[] guid = new byte[16];
                Array.Copy(digest, guid, guid.Length);
                sessionGuid = new Guid(guid);
            }
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public void Start()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("ProcessNetworkMonitor");
                if (wanted) return;
                wanted = true;
                requestVersion++;
                if (worker != null) return;
                status = "Starting app tracking...";
                error = null;
                needsElevation = false;
                aggregate.Reset(Seconds());
                worker = new Thread(WorkerLoop) { IsBackground = true, Name = "NetSpeedWidget process traffic" };
                worker.Start();
            }
        }

        public void Stop()
        {
            NativeRun stop;
            lock (gate)
            {
                wanted = false;
                running = false;
                status = "App tracking paused";
                error = null;
                needsElevation = false;
                stop = run;
                if (stop != null) stop.Cancelled = true;
            }
            if (stop != null) ThreadPool.QueueUserWorkItem(delegate { stop.RequestStop(); });
        }

        public ProcessTrafficSample Sample()
        {
            ProcessTrafficSample sample;
            lock (gate)
            {
                sample = new ProcessTrafficSample
                {
                    Rows = new List<ProcessTrafficRow>(), Status = status, Error = error,
                    NeedsElevation = needsElevation, IsRunning = running
                };
            }
            if (!sample.IsRunning) return sample;
            bool approximate;
            sample.Rows = aggregate.Sample(Seconds(), names.Resolve, out approximate);
            sample.IsApproximate = approximate;
            if (approximate) sample.Status = "Some events were missed; rates are approximate";
            else sample.Status = sample.Rows.Count == 0 ? "Waiting for app network traffic..." : "Live TCP + UDP traffic";
            return sample;
        }

        private void WorkerLoop()
        {
            while (true)
            {
                NativeRun current;
                int currentVersion;
                lock (gate)
                {
                    if (!wanted || disposed) { worker = null; return; }
                    current = new NativeRun(sessionName, sessionGuid, mutexName, aggregate);
                    currentVersion = requestVersion;
                    run = current;
                    status = "Starting app tracking...";
                    error = null;
                    needsElevation = false;
                    aggregate.Reset(Seconds());
                }
                try
                {
                    current.Execute(delegate
                    {
                        lock (gate)
                        {
                            if (wanted && !disposed && !current.Cancelled)
                            {
                                running = true;
                                status = "Waiting for app network traffic...";
                            }
                        }
                    });
                }
                catch (Exception ex)
                {
                    lock (gate)
                    {
                        if (!current.Cancelled && wanted && !disposed)
                        {
                            Win32Exception native = ex as Win32Exception;
                            needsElevation = ex is UnauthorizedAccessException ||
                                (native != null && (native.NativeErrorCode == 5 || native.NativeErrorCode == 1314));
                            status = needsElevation ? "Run as administrator to see app traffic" : "App tracking unavailable";
                            error = ex.Message;
                            wanted = false;
                        }
                    }
                }
                finally
                {
                    current.Dispose();
                    lock (gate) { running = false; if (run == current) run = null; }
                }
                lock (gate)
                {
                    // A collapse/reopen during shutdown restarts only after the old owned session is gone.
                    if (!current.Cancelled && wanted && currentVersion == requestVersion)
                    {
                        wanted = false;
                        status = "App tracking stopped";
                    }
                    if (!wanted || disposed) { worker = null; return; }
                }
            }
        }

        public void Dispose()
        {
            Thread join;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                join = worker;
            }
            Stop();
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            // Normal teardown is immediate. Bound the exit wait even if Windows tracing is unresponsive.
            if (join != null && join != Thread.CurrentThread) join.Join(2000);
        }

        private void OnProcessExit(object sender, EventArgs args) { Dispose(); }
        internal static double Seconds() { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; }

        private sealed class NativeRun : IDisposable
        {
            private readonly object nativeGate = new object();
            private readonly string name;
            private readonly Guid guid;
            private readonly string ownerName;
            private readonly ProcessTrafficAggregator aggregate;
            private ulong controller;
            private ulong consumer = EtwNative.InvalidConsumerHandle;
            private Timer healthTimer;
            private EtwNative.EventRecordCallback eventCallback;
            private EtwNative.BufferCallback bufferCallback;
            private bool closed;
            internal volatile bool Cancelled;

            internal NativeRun(string name, Guid guid, string ownerName, ProcessTrafficAggregator aggregate)
            {
                this.name = name; this.guid = guid; this.ownerName = ownerName; this.aggregate = aggregate;
            }

            internal void Execute(Action started)
            {
                using (Mutex owner = new Mutex(false, ownerName))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = owner.WaitOne(0); }
                        catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new InvalidOperationException("Another widget is already monitoring app traffic in this Windows session.");
                        lock (nativeGate)
                        {
                            if (Cancelled) return;
                            RecoverOwnedOrphan();
                            using (EtwNative.PropertiesBuffer properties = new EtwNative.PropertiesBuffer(name, guid))
                            {
                                uint result = EtwNative.StartTrace(out controller, name, properties.Pointer);
                                if (result != 0) { controller = 0; throw new Win32Exception((int)result); }
                            }
                            eventCallback = OnEvent;
                            bufferCallback = delegate(IntPtr bufferInfo) { return Cancelled ? 0U : 1U; };
                            EtwNative.EventTraceLogfile logfile = new EtwNative.EventTraceLogfile();
                            logfile.LoggerName = name;
                            logfile.ProcessTraceMode = 0x00000100 | 0x10000000; // REAL_TIME | EVENT_RECORD, converted UTC timestamps
                            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(eventCallback);
                            logfile.BufferCallback = Marshal.GetFunctionPointerForDelegate(bufferCallback);
                            consumer = EtwNative.OpenTrace(ref logfile);
                            if (consumer == EtwNative.InvalidConsumerHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
                            healthTimer = new Timer(CheckHealth, null, 1000, 1000);
                        }
                        if (Cancelled) return;
                        started();
                        ulong handle;
                        lock (nativeGate) handle = consumer;
                        if (handle != EtwNative.InvalidConsumerHandle)
                        {
                            uint result = EtwNative.ProcessTrace(new ulong[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
                            if (!Cancelled && result != 0 && result != 1223) throw new Win32Exception((int)result);
                        }
                    }
                    finally
                    {
                        // Stop before releasing the ownership mutex, so an immediate reopen cannot race cleanup.
                        Dispose();
                        if (acquired) owner.ReleaseMutex();
                    }
                }
            }

            private void RecoverOwnedOrphan()
            {
                using (EtwNative.PropertiesBuffer properties = new EtwNative.PropertiesBuffer(name, guid))
                {
                    uint result = EtwNative.ControlTrace(0, name, properties.Pointer, 0); // QUERY
                    if (result == 4201) return; // No session with our name exists.
                    if (result != 0) throw new Win32Exception((int)result);
                    EtwNative.EventTraceProperties existing = properties.Read();
                    if (existing.Wnode.Guid != guid)
                        throw new InvalidOperationException("The app traffic session name is in use by another owner.");
                    result = EtwNative.ControlTrace(0, name, properties.Pointer, 1); // STOP only verified orphan
                    if (result != 0 && result != 4201) throw new Win32Exception((int)result);
                }
            }

            private void OnEvent(IntPtr recordPointer)
            {
                if (Cancelled) return;
                try
                {
                    EtwNative.EventRecord record = (EtwNative.EventRecord)Marshal.PtrToStructure(recordPointer, typeof(EtwNative.EventRecord));
                    ProcessNetworkEvent decoded;
                    bool malformed;
                    if (ProcessNetworkDecoder.TryDecode(record, out decoded, out malformed))
                        aggregate.Add(decoded.ProcessId, decoded.Bytes, decoded.IsReceive, decoded.UtcFileTime, Seconds());
                    else if (malformed) aggregate.MarkApproximate();
                }
                catch { aggregate.MarkApproximate(); } // Never unwind a managed exception through ETW.
            }

            private void CheckHealth(object ignored)
            {
                try
                {
                    lock (nativeGate)
                    {
                        if (closed || Cancelled || controller == 0) return;
                        using (EtwNative.PropertiesBuffer properties = new EtwNative.PropertiesBuffer(name, guid))
                        {
                            uint result = EtwNative.ControlTrace(controller, null, properties.Pointer, 0);
                            if (result != 0) { aggregate.MarkApproximate(); return; }
                            EtwNative.EventTraceProperties p = properties.Read();
                            // EVENT_TRACE_LOGFILE.EventsLost is unused. Query the controller's real loss counters.
                            if (p.EventsLost != 0 || p.LogBuffersLost != 0 || p.RealTimeBuffersLost != 0)
                                aggregate.MarkApproximate();
                        }
                    }
                }
                catch { aggregate.MarkApproximate(); }
            }

            internal void RequestStop()
            {
                lock (nativeGate)
                {
                    if (closed) return;
                    if (controller != 0)
                    {
                        using (EtwNative.PropertiesBuffer properties = new EtwNative.PropertiesBuffer(name, guid))
                            EtwNative.ControlTrace(controller, null, properties.Pointer, 1);
                        controller = 0;
                    }
                    if (consumer != EtwNative.InvalidConsumerHandle)
                    {
                        EtwNative.CloseTrace(consumer);
                        consumer = EtwNative.InvalidConsumerHandle;
                    }
                }
            }

            public void Dispose()
            {
                lock (nativeGate)
                {
                    if (closed) return;
                    if (healthTimer != null) { healthTimer.Dispose(); healthTimer = null; }
                    RequestStop();
                    closed = true;
                }
                // Delegates stay rooted in this run until ProcessTrace has returned.
                GC.KeepAlive(eventCallback);
                GC.KeepAlive(bufferCallback);
            }
        }
    }

    internal struct ProcessNetworkEvent
    {
        internal int ProcessId;
        internal uint Bytes;
        internal bool IsReceive;
        internal long UtcFileTime;
    }

    internal static class ProcessNetworkDecoder
    {
        internal static readonly Guid TcpProvider = new Guid("9a280ac0-c8e0-11d1-84e2-00c04fb998a2");
        internal static readonly Guid UdpProvider = new Guid("bf3a50c5-a9c9-4988-a005-2df0b7c80f80");

        internal static bool TryDecode(EtwNative.EventRecord record, out ProcessNetworkEvent result, out bool malformed)
        {
            result = new ProcessNetworkEvent();
            malformed = false;
            if (record.Header.ProviderId != TcpProvider && record.Header.ProviderId != UdpProvider) return false;
            byte opcode = record.Header.Descriptor.Opcode;
            if (opcode != 10 && opcode != 11 && opcode != 26 && opcode != 27) return false;
            // Documented v1/v2 payloads begin with uint32 PID, uint32 size for both protocols.
            // Header PID can belong to a system thread on receives, and must never be used here.
            byte version = record.Header.Descriptor.Version;
            if ((version != 1 && version != 2) || (version == 1 && opcode >= 26) ||
                record.UserData == IntPtr.Zero || record.UserDataLength < 8)
            {
                malformed = true;
                return false;
            }
            uint pid = unchecked((uint)Marshal.ReadInt32(record.UserData));
            uint bytes = unchecked((uint)Marshal.ReadInt32(record.UserData, 4));
            if (pid > Int32.MaxValue || bytes == 0) return false;
            result.ProcessId = (int)pid;
            result.Bytes = bytes;
            result.IsReceive = opcode == 11 || opcode == 27;
            result.UtcFileTime = record.Header.TimeStamp;
            return true;
        }
    }

    internal sealed class ProcessIdentity
    {
        internal string Name;
        internal long CreatedUtcFileTime;
    }

    internal sealed class ProcessTrafficAggregator
    {
        private readonly object gate = new object();
        private readonly Dictionary<int, Bucket> buckets = new Dictionary<int, Bucket>();
        private double previousSeconds;
        private bool approximate;

        internal void Reset(double seconds)
        {
            lock (gate) { buckets.Clear(); previousSeconds = seconds; approximate = false; }
        }

        internal void MarkApproximate() { lock (gate) approximate = true; }

        internal void Add(int pid, uint bytes, bool receive, long utcFileTime, double seconds)
        {
            lock (gate)
            {
                Bucket bucket;
                if (!buckets.TryGetValue(pid, out bucket))
                {
                    if (buckets.Count >= 4096) { approximate = true; return; }
                    bucket = new Bucket { ProcessId = pid };
                    buckets.Add(pid, bucket);
                }
                if (receive) bucket.Down += bytes; else bucket.Up += bytes;
                bucket.LastSeen = seconds;
                if (bucket.FirstEvent == 0 || utcFileTime < bucket.FirstEvent) bucket.FirstEvent = utcFileTime;
            }
        }

        internal List<ProcessTrafficRow> Sample(double seconds, Func<int, ProcessIdentity> resolve, out bool isApproximate)
        {
            List<Bucket> snapshot = new List<Bucket>();
            double elapsed;
            lock (gate)
            {
                elapsed = seconds - previousSeconds;
                if (elapsed <= 0) { isApproximate = approximate; return new List<ProcessTrafficRow>(); }
                if (elapsed > 10) { buckets.Clear(); previousSeconds = seconds; isApproximate = approximate; return new List<ProcessTrafficRow>(); }
                List<int> expired = new List<int>();
                foreach (Bucket bucket in buckets.Values)
                {
                    if (seconds - bucket.LastSeen > 5) { expired.Add(bucket.ProcessId); continue; }
                    snapshot.Add(new Bucket { ProcessId = bucket.ProcessId, Down = bucket.Down, Up = bucket.Up, FirstEvent = bucket.FirstEvent });
                    bucket.Down = 0; bucket.Up = 0; bucket.FirstEvent = 0;
                }
                foreach (int id in expired) buckets.Remove(id);
                previousSeconds = seconds;
                isApproximate = approximate;
            }
            List<ProcessTrafficRow> rows = new List<ProcessTrafficRow>();
            foreach (Bucket bucket in snapshot)
            {
                ProcessIdentity identity = resolve(bucket.ProcessId);
                // ETW delivery is buffered. If this PID was reused before a queued event was read,
                // do not label the old process's traffic with the new process's cached name.
                if (identity != null && identity.CreatedUtcFileTime > 0 && bucket.FirstEvent > 0 && bucket.FirstEvent < identity.CreatedUtcFileTime)
                {
                    MarkApproximate(); isApproximate = true; continue;
                }
                rows.Add(new ProcessTrafficRow
                {
                    ProcessId = bucket.ProcessId,
                    Name = identity == null || String.IsNullOrEmpty(identity.Name) ? "Process " + bucket.ProcessId.ToString(CultureInfo.CurrentCulture) : identity.Name,
                    DownloadBytesPerSecond = bucket.Down / elapsed,
                    UploadBytesPerSecond = bucket.Up / elapsed
                });
            }
            return rows;
        }

        private sealed class Bucket
        {
            internal int ProcessId;
            internal ulong Down;
            internal ulong Up;
            internal double LastSeen;
            internal long FirstEvent;
        }
    }

    internal sealed class ProcessNameCache
    {
        private readonly Dictionary<int, ProcessIdentity> cache = new Dictionary<int, ProcessIdentity>();

        internal ProcessIdentity Resolve(int pid)
        {
            if (pid == 0) return new ProcessIdentity { Name = "System / unassigned" };
            if (pid == 4) return new ProcessIdentity { Name = "System" };
            IntPtr process = OpenProcess(0x1000, false, pid); // QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) { cache.Remove(pid); return null; }
            try
            {
                long created, exited, kernel, user;
                if (!GetProcessTimes(process, out created, out exited, out kernel, out user)) { cache.Remove(pid); return null; }
                ProcessIdentity known;
                if (cache.TryGetValue(pid, out known) && known.CreatedUtcFileTime == created) return known;
                StringBuilder path = new StringBuilder(1024);
                int length = path.Capacity;
                string name = QueryFullProcessImageName(process, 0, path, ref length) ? Path.GetFileNameWithoutExtension(path.ToString()) : "Process " + pid.ToString(CultureInfo.CurrentCulture);
                ProcessIdentity identity = new ProcessIdentity { Name = name, CreatedUtcFileTime = created };
                if (cache.Count >= 4096) cache.Clear();
                cache[pid] = identity;
                return identity;
            }
            finally { CloseHandle(process); }
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }

    internal static class EtwNative
    {
        internal static readonly ulong InvalidConsumerHandle = IntPtr.Size == 8 ? UInt64.MaxValue : UInt32.MaxValue;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void EventRecordCallback(IntPtr record);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate uint BufferCallback(IntPtr logfile);

        [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)] internal static extern uint StartTrace(out ulong session, string name, IntPtr properties);
        [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)] internal static extern uint ControlTrace(ulong session, string name, IntPtr properties, uint code);
        [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ulong OpenTrace(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll")] internal static extern uint ProcessTrace([In] ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] internal static extern uint CloseTrace(ulong handle);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WnodeHeader
        {
            internal uint BufferSize, ProviderId;
            internal ulong HistoricalContext;
            internal long TimeStamp;
            internal Guid Guid;
            internal uint ClientContext, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventTraceProperties
        {
            internal WnodeHeader Wnode;
            internal uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            internal int AgeLimit;
            internal uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            internal IntPtr LoggerThreadId;
            internal uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventDescriptor
        {
            internal ushort Id;
            internal byte Version, Channel, Level, Opcode;
            internal ushort Task;
            internal ulong Keyword;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventHeader
        {
            internal ushort Size, HeaderType, Flags, EventProperty;
            internal uint ThreadId, ProcessId;
            internal long TimeStamp;
            internal Guid ProviderId;
            internal EventDescriptor Descriptor;
            internal ulong ProcessorTime;
            internal Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventRecord
        {
            internal EventHeader Header;
            internal uint BufferContext;
            internal ushort ExtendedDataCount, UserDataLength;
            internal IntPtr ExtendedData, UserData, UserContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventTraceHeader
        {
            internal ushort Size, FieldTypeFlags;
            internal uint Version, ThreadId, ProcessId;
            internal long TimeStamp;
            internal Guid Guid;
            internal ulong ProcessorTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventTrace
        {
            internal EventTraceHeader Header;
            internal uint InstanceId, ParentInstanceId;
            internal Guid ParentGuid;
            internal IntPtr MofData;
            internal uint MofLength, BufferContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TimeZoneInformation
        {
            internal int Bias;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal ushort[] StandardName;
            internal SystemTime StandardDate;
            internal int StandardBias;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal ushort[] DaylightName;
            internal SystemTime DaylightDate;
            internal int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SystemTime
        {
            internal ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct TraceLogfileHeader
        {
            internal uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            internal long EndTime;
            internal uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            internal uint StartBuffers, PointerSize, EventsLost, CpuSpeed;
            internal IntPtr LoggerName, LogFileName;
            internal TimeZoneInformation TimeZone;
            internal long BootTime, PerfFreq, StartTime;
            internal uint ReservedFlags, BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct EventTraceLogfile
        {
            [MarshalAs(UnmanagedType.LPWStr)] internal string LogFileName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string LoggerName;
            internal long CurrentTime;
            internal uint BuffersRead, ProcessTraceMode;
            internal EventTrace CurrentEvent;
            internal TraceLogfileHeader LogfileHeader;
            internal IntPtr BufferCallback;
            internal uint BufferSize, Filled, EventsLost;
            internal IntPtr EventRecordCallback;
            internal uint IsKernelTrace;
            internal IntPtr Context;
        }

        internal sealed class PropertiesBuffer : IDisposable
        {
            internal IntPtr Pointer;
            internal PropertiesBuffer(string name, Guid guid)
            {
                int structureSize = Marshal.SizeOf(typeof(EventTraceProperties));
                int size = structureSize + 2048; // Room for query-returned session name; no log file path.
                Pointer = Marshal.AllocHGlobal(size);
                Marshal.Copy(new byte[size], 0, Pointer, size);
                EventTraceProperties properties = new EventTraceProperties();
                properties.Wnode.BufferSize = (uint)size;
                properties.Wnode.Guid = guid;
                properties.Wnode.ClientContext = 1; // QPC capture clock; ProcessTrace converts to UTC.
                properties.Wnode.Flags = 0x00020000; // WNODE_FLAG_TRACED_GUID
                properties.BufferSize = 64;
                properties.MinimumBuffers = 4;
                properties.MaximumBuffers = 64;
                properties.LogFileMode = 0x00000100 | 0x02000000; // REAL_TIME | SYSTEM_LOGGER_MODE
                properties.FlushTimer = 1;
                properties.EnableFlags = 0x00010000 | 0x10000000; // NETWORK_TCPIP | NO_SYSCONFIG
                properties.LoggerNameOffset = (uint)structureSize;
                properties.LogFileNameOffset = 0;
                Marshal.StructureToPtr(properties, Pointer, false);
            }
            internal EventTraceProperties Read() { return (EventTraceProperties)Marshal.PtrToStructure(Pointer, typeof(EventTraceProperties)); }
            public void Dispose() { if (Pointer != IntPtr.Zero) { Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero; } }
        }
    }
}
