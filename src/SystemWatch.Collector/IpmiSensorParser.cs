using System.Globalization;
using System.Text.RegularExpressions;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record IpmiSensorResult(List<Temperature> Temperatures, List<Fan> Fans);

/// <summary>Parse read-only ipmitool sensor output into contract records.</summary>
public static partial class IpmiSensorParser
{
    [GeneratedRegex(@"^DDR4_([A-Z]) Temp$")]
    private static partial Regex DimmName();

    public static IpmiSensorResult Parse(string output, string observedAt)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        var temperatures = new List<Temperature>();
        var fans = new List<Fan>();
        foreach (var line in output.Split('\n'))
        {
            var fields = line.Split('|').Select(x => x.Trim()).ToArray();
            if (fields.Length < 10) continue;
            var name = fields[0];
            var value = Number(fields[1]);
            var unit = fields[2];
            var status = fields[3];
            var limits = Limits(fields);
            if (unit.Equals("degrees c", StringComparison.OrdinalIgnoreCase))
            {
                var (category, warning, critical, legacy) = Category(name);
                temperatures.Add(new Temperature
                {
                    Id = "ipmi:" + name, Label = name, Category = category,
                    SourceId = "ipmi", DeviceId = null,
                    ValueCelsius = value, WarningCelsius = warning,
                    CriticalCelsius = critical,
                    ThresholdOrigin = warning is null ? "none" : "baseline",
                    Health = Severity(value, warning, critical),
                    LegacySystemMember = legacy,
                    Reading = Reading(observedAt, value is not null),
                    ReportedStatus = status,
                    SourceThresholdsCelsius = limits,
                });
            }
            else if (unit.Equals("RPM", StringComparison.OrdinalIgnoreCase) &&
                     name.Contains("FAN", StringComparison.OrdinalIgnoreCase))
            {
                var lower = status.ToLowerInvariant();
                var health = lower == "ok" && value is not null ? "ok" :
                    lower is "cr" or "critical" or "nr" or "non-recoverable" ? "critical" :
                    lower is "nc" or "warning" ? "warning" : "unknown";
                fans.Add(new Fan
                {
                    Id = "ipmi:" + name, Label = name, SourceId = "ipmi",
                    Rpm = value, ReportedStatus = status, Health = health,
                    Reading = Reading(observedAt, value is not null),
                    SourceThresholdsRpm = limits,
                });
            }
        }
        if (temperatures.Count == 0 && fans.Count == 0)
            throw new InvalidDataException("no IPMI temperature or fan records parsed");
        return new IpmiSensorResult(temperatures, fans);
    }

    private static (string Category, double? Warning, double? Critical, bool Legacy) Category(string name)
    {
        if (name == "CPU Temp") return ("platform", 75, 90, true);
        if (name is "MB Temp" or "Card Side Temp") return ("platform", 55, 70, true);
        if (DimmName().IsMatch(name)) return ("memory", 70, 84, true);
        return ("other", null, null, false);
    }

    private static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            && double.IsFinite(number) ? number : null;

    private static SourceThresholds Limits(string[] fields) => new()
    {
        LowerNonrecoverable = Number(fields[4]), LowerCritical = Number(fields[5]),
        LowerNoncritical = Number(fields[6]), UpperNoncritical = Number(fields[7]),
        UpperCritical = Number(fields[8]), UpperNonrecoverable = Number(fields[9]),
    };

    private static Reading Reading(string at, bool available) => new()
    {
        ObservedAt = available ? at : null,
        AgeSeconds = available ? 0 : null,
        StaleAfterSeconds = 15,
        Freshness = available ? "fresh" : "unavailable",
        LastAttemptAt = at,
        LastAttemptStatus = available ? "ok" : "error",
        Reason = available ? null : "sensor_unavailable",
    };

    private static string Severity(double? value, double? warning, double? critical) =>
        value is null || warning is null || critical is null ? "unknown" :
        value >= critical ? "critical" : value >= warning ? "warning" : "ok";
}
