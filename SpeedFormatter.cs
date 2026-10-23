using System;
using System.Globalization;

namespace NetSpeedWidget
{
    internal static class SpeedFormatter
    {
        public static void Format(double bytesPerSecond, bool bits, out string value, out string unit)
        {
            double speed = double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond)
                ? 0 : Math.Max(0, bytesPerSecond) * (bits ? 8 : 1);
            string[] units = bits ? new[] { "bit/s", "kbit/s", "Mbit/s", "Gbit/s", "Tbit/s" }
                                  : new[] { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
            int index = 0;
            while (speed >= 999.5 && index < units.Length - 1) { speed /= 1000; index++; }
            value = speed.ToString(speed < 10 && index > 0 ? "0.00" : speed < 100 && index > 0 ? "0.0" : "0", CultureInfo.CurrentCulture);
            unit = units[index];
        }

        public static string Full(double bytesPerSecond, bool bits)
        {
            string value, unit;
            Format(bytesPerSecond, bits, out value, out unit);
            return value + " " + unit;
        }
    }
}
