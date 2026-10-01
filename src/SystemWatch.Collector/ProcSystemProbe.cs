using System.Globalization;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Read-only Linux uptime/load probe, using uptime and load averages.</summary>
public static class ProcSystemProbe
{
    public static SystemMetrics ReadFrom(string procRoot = "/proc")
    {
        var uptime = File.ReadAllText(Path.Combine(procRoot, "uptime"));
        var load = File.ReadAllText(Path.Combine(procRoot, "loadavg"));
        return Parse(uptime, load);
    }

    public static SystemMetrics Parse(string uptimeText, string loadavgText)
    {
        var uptime = Fields(uptimeText, "/proc/uptime", 1);
        var load = Fields(loadavgText, "/proc/loadavg", 3);
        return new SystemMetrics
        {
            UptimeSeconds = Number(uptime[0], "uptime"),
            LoadAverages =
            [
                Number(load[0], "load average 1m"),
                Number(load[1], "load average 5m"),
                Number(load[2], "load average 15m"),
            ],
        };
    }

    private static string[] Fields(string text, string source, int minimum)
    {
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < minimum)
            throw new FormatException($"{source}: expected at least {minimum} fields");
        return parts;
    }

    private static double Number(string text, string field)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            !double.IsFinite(value) || value < 0)
            throw new FormatException($"{field}: expected finite nonnegative number");
        return value;
    }
}
