using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NetSpeedWidget
{
    public sealed class AdapterInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public bool IsHardware { get; set; }
    }

    public sealed class NetworkSample
    {
        public double DownloadBytesPerSecond { get; set; }
        public double UploadBytesPerSecond { get; set; }
        public string ConnectionLabel { get; set; }
        public bool IsConnected { get; set; }
        public string Error { get; set; }
    }

    /// <summary>Measures interface traffic, including IPv4 and IPv6, without sending traffic.</summary>
    public sealed class NetworkMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly Func<List<AdapterSnapshot>> readAdapters;
        private readonly Func<double> readSeconds;
        private readonly Dictionary<string, Baseline> baselines = new Dictionary<string, Baseline>(StringComparer.OrdinalIgnoreCase);
        private bool disposed;

        public NetworkMonitor()
            : this(NativeInterfaces.Read, delegate { return (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency; })
        {
        }

        // An injected clock and source keep counter/reconnect tests independent of the host network.
        internal NetworkMonitor(Func<List<AdapterSnapshot>> source, Func<double> clock)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (clock == null) throw new ArgumentNullException("clock");
            readAdapters = source;
            readSeconds = clock;
        }

        public List<AdapterInfo> GetAdapters()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                List<AdapterInfo> result = new List<AdapterInfo>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (AdapterSnapshot adapter in readAdapters())
                {
                    if (!adapter.IsEligible || !seen.Add(adapter.Id)) continue;
                    result.Add(new AdapterInfo { Id = adapter.Id, Name = adapter.Name, IsHardware = adapter.IsHardware });
                }
                result.Sort(delegate(AdapterInfo a, AdapterInfo b)
                {
                    int hardware = b.IsHardware.CompareTo(a.IsHardware);
                    return hardware != 0 ? hardware : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
                });
                return result;
            }
        }

        public NetworkSample Sample(string adapterId)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                try
                {
                    return CalculateSample(readAdapters(), readSeconds(), adapterId);
                }
                catch (Exception error)
                {
                    // After a failed read the next successful read must establish a fresh baseline.
                    // Do not show an average spanning an outage as current network speed.
                    baselines.Clear();
                    return new NetworkSample { ConnectionLabel = "Network unavailable", Error = error.Message };
                }
            }
        }

        private NetworkSample CalculateSample(List<AdapterSnapshot> adapters, double seconds, string adapterId)
        {
            List<AdapterSnapshot> available = new List<AdapterSnapshot>();
            Dictionary<string, Rate> rates = new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdapterSnapshot adapter in adapters)
            {
                if (!adapter.IsEligible || !adapter.IsConnected || !active.Add(adapter.Id)) continue;
                available.Add(adapter);
                Baseline previous;
                Rate rate = new Rate();
                if (baselines.TryGetValue(adapter.Id, out previous))
                {
                    double elapsed = seconds - previous.Seconds;
                    // A driver reset, reconnect, clock anomaly, or resume after sleep is a new baseline.
                    if (elapsed > 0 && elapsed <= 10 && previous.Luid == adapter.Luid &&
                        previous.NetworkGuid == adapter.NetworkGuid &&
                        adapter.Received >= previous.Received && adapter.Sent >= previous.Sent)
                    {
                        // Subtract as unsigned integers first, preserving small deltas above 2^53.
                        rate.Down = (adapter.Received - previous.Received) / elapsed;
                        rate.Up = (adapter.Sent - previous.Sent) / elapsed;
                    }
                }
                baselines[adapter.Id] = new Baseline
                {
                    Seconds = seconds,
                    Received = adapter.Received,
                    Sent = adapter.Sent,
                    Luid = adapter.Luid,
                    NetworkGuid = adapter.NetworkGuid
                };
                rates[adapter.Id] = rate;
            }

            // Removing disconnected/missing adapters prevents a reconnect from counting old traffic.
            List<string> stale = new List<string>();
            foreach (string id in baselines.Keys)
                if (!active.Contains(id)) stale.Add(id);
            foreach (string id in stale) baselines.Remove(id);

            List<AdapterSnapshot> selected = new List<AdapterSnapshot>();
            if (!String.IsNullOrEmpty(adapterId))
            {
                foreach (AdapterSnapshot adapter in available)
                    if (String.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase)) selected.Add(adapter);
            }
            else
            {
                // A VPN's packets also cross the physical adapter. Count the physical layer once.
                foreach (AdapterSnapshot adapter in available)
                    if (adapter.IsHardware) selected.Add(adapter);
                if (selected.Count == 0)
                {
                    // Guests and some virtual switches have no hardware interface. Prefer their
                    // ordinary network layer over PPP/tunnels, then allow tunnel-only connections.
                    foreach (AdapterSnapshot adapter in available)
                        if (!adapter.IsTunnel) selected.Add(adapter);
                    if (selected.Count == 0) selected.AddRange(available);
                }
            }

            NetworkSample sample = new NetworkSample { ConnectionLabel = "Disconnected", IsConnected = selected.Count > 0 };
            if (selected.Count == 1) sample.ConnectionLabel = selected[0].Name;
            else if (selected.Count > 1) sample.ConnectionLabel = selected.Count.ToString(CultureInfo.CurrentCulture) + " adapters";
            else if (!String.IsNullOrEmpty(adapterId))
            {
                sample.ConnectionLabel = "Adapter unavailable";
                foreach (AdapterSnapshot adapter in adapters)
                    if (String.Equals(adapter.Id, adapterId, StringComparison.OrdinalIgnoreCase))
                        sample.ConnectionLabel = adapter.Name + " (disconnected)";
            }
            foreach (AdapterSnapshot adapter in selected)
            {
                Rate rate = rates[adapter.Id];
                sample.DownloadBytesPerSecond += rate.Down;
                sample.UploadBytesPerSecond += rate.Up;
            }
            return sample;
        }

        public void Reset()
        {
            lock (gate)
            {
                ThrowIfDisposed();
                baselines.Clear();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                baselines.Clear();
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException("NetworkMonitor");
        }

        private sealed class Baseline
        {
            internal double Seconds;
            internal ulong Received;
            internal ulong Sent;
            internal ulong Luid;
            internal Guid NetworkGuid;
        }

        private struct Rate
        {
            internal double Down;
            internal double Up;
        }
    }

    internal sealed class AdapterSnapshot
    {
        internal string Id;
        internal string Name;
        internal bool IsHardware;
        internal bool IsConnected;
        internal bool IsEligible;
        internal bool IsTunnel;
        internal ulong Luid;
        internal Guid NetworkGuid;
        internal ulong Received;
        internal ulong Sent;
    }

    internal static class NativeInterfaces
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern uint GetIfTable2(out IntPtr table);

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern void FreeMibTable(IntPtr table);

        // NET_LUID and ULONG64 require eight-byte alignment, including the first table row.
        // https://learn.microsoft.com/windows/win32/api/netioapi/ns-netioapi-mib_if_row2
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        internal struct MibIfRow2
        {
            internal ulong InterfaceLuid;
            internal uint InterfaceIndex;
            internal Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string Alias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] internal string Description;
            internal uint PhysicalAddressLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PhysicalAddress;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] internal byte[] PermanentPhysicalAddress;
            internal uint Mtu;
            internal uint Type;
            internal uint TunnelType;
            internal uint MediaType;
            internal uint PhysicalMediumType;
            internal uint AccessType;
            internal uint DirectionType;
            internal byte InterfaceAndOperStatusFlags;
            internal uint OperStatus;
            internal uint AdminStatus;
            internal uint MediaConnectState;
            internal Guid NetworkGuid;
            internal uint ConnectionType;
            internal ulong TransmitLinkSpeed;
            internal ulong ReceiveLinkSpeed;
            internal ulong InOctets;
            internal ulong InUcastPkts;
            internal ulong InNUcastPkts;
            internal ulong InDiscards;
            internal ulong InErrors;
            internal ulong InUnknownProtos;
            internal ulong InUcastOctets;
            internal ulong InMulticastOctets;
            internal ulong InBroadcastOctets;
            internal ulong OutOctets;
            internal ulong OutUcastPkts;
            internal ulong OutNUcastPkts;
            internal ulong OutDiscards;
            internal ulong OutErrors;
            internal ulong OutUcastOctets;
            internal ulong OutMulticastOctets;
            internal ulong OutBroadcastOctets;
            internal ulong OutQLen;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 8)]
        internal struct MibIfTableStart
        {
            internal uint NumEntries;
            internal MibIfRow2 FirstRow;
        }

        internal static List<AdapterSnapshot> Read()
        {
            IntPtr table = IntPtr.Zero;
            try
            {
                uint error = GetIfTable2(out table);
                if (error != 0) throw new Win32Exception(unchecked((int)error));
                if (table == IntPtr.Zero) throw new InvalidOperationException("Windows returned an empty network table.");

                int rowSize = Marshal.SizeOf(typeof(MibIfRow2));
                int rowOffset = Marshal.OffsetOf(typeof(MibIfTableStart), "FirstRow").ToInt32();
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > 100000) throw new InvalidOperationException("Windows returned an invalid network table.");
                List<AdapterSnapshot> adapters = new List<AdapterSnapshot>(count);
                for (int i = 0; i < count; i++)
                {
                    MibIfRow2 row = (MibIfRow2)Marshal.PtrToStructure(IntPtr.Add(table, checked(rowOffset + i * rowSize)), typeof(MibIfRow2));
                    byte flags = row.InterfaceAndOperStatusFlags;
                    string id = row.InterfaceGuid != Guid.Empty ? row.InterfaceGuid.ToString("D") : "luid:" + row.InterfaceLuid.ToString(CultureInfo.InvariantCulture);
                    string name = String.IsNullOrWhiteSpace(row.Alias) ? row.Description : row.Alias;
                    adapters.Add(new AdapterSnapshot
                    {
                        Id = id,
                        Name = String.IsNullOrWhiteSpace(name) ? "Network adapter " + row.InterfaceIndex.ToString(CultureInfo.CurrentCulture) : name,
                        IsHardware = (flags & 0x01) != 0,
                        IsConnected = row.OperStatus == 1 && row.MediaConnectState != 2 && (flags & 0x10) == 0,
                        IsEligible = row.Type != 24 && row.AccessType != 1 && (flags & 0x82) == 0,
                        IsTunnel = row.Type == 131 || row.Type == 23,
                        Luid = row.InterfaceLuid,
                        NetworkGuid = row.NetworkGuid,
                        Received = row.InOctets,
                        Sent = row.OutOctets
                    });
                }
                return adapters;
            }
            finally
            {
                if (table != IntPtr.Zero) FreeMibTable(table);
            }
        }
    }
}
