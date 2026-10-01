using System.Globalization;
using System.Text.RegularExpressions;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record HwmonPlatformResult(List<Temperature> Temperatures, Source Source);

/// <summary>Read-only non-NVMe hwmon adapter, equivalent to hwmon_platform.py.</summary>
public static partial class HwmonPlatformProbe
{
    [GeneratedRegex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{4}:[0-9a-fA-F]{2}:[0-9a-fA-F]{2}\.[0-7]")]
    private static partial Regex PciAddress();

    [GeneratedRegex(@"^temp([0-9]+)_input$")]
    private static partial Regex TemperatureInput();

    [GeneratedRegex(@"/hwmon[0-9]+$")]
    private static partial Regex HwmonSuffix();

    public static HwmonPlatformResult Collect(string root = "/sys/class/hwmon", string? timestamp = null)
    {
        var at = timestamp ?? DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var temperatures = new List<Temperature>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var failures = 0;
        IEnumerable<string> entries = Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "hwmon*").OrderBy(x => x, StringComparer.Ordinal)
            : Array.Empty<string>();

        foreach (var hwmon in entries)
        {
            string name;
            try { name = File.ReadAllText(Path.Combine(hwmon, "name")).Trim(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            if (name == "nvme") continue;

            var target = new DirectoryInfo(hwmon).ResolveLinkTarget(returnFinalTarget: true);
            var resolved = target?.FullName ?? Path.GetFullPath(hwmon);
            var (category, warning, critical) = Category(name);
            foreach (var input in Directory.EnumerateFileSystemEntries(hwmon, "temp*_input")
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                var match = TemperatureInput().Match(Path.GetFileName(input));
                if (!match.Success) continue;
                var number = match.Groups[1].Value;
                string label;
                try { label = File.ReadAllText(Path.Combine(hwmon, $"temp{number}_label")).Trim(); }
                catch (IOException) { label = $"Temp{number}"; }
                catch (UnauthorizedAccessException) { label = $"Temp{number}"; }

                double? celsius = null;
                try
                {
                    var raw = File.ReadAllText(input).Trim();
                    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milli))
                        celsius = milli / 1000.0;
                }
                catch (IOException) { /* per-channel failure */ }
                catch (UnauthorizedAccessException) { /* per-channel failure */ }
                if (celsius is null) failures++;

                var id = SensorId(resolved, name, number);
                if (!seen.Add(id)) throw new InvalidDataException($"duplicate physical hwmon sensor identity: {id}");
                var health = celsius is null || warning is null ? "unknown" :
                    celsius >= critical ? "critical" : celsius >= warning ? "warning" : "ok";
                temperatures.Add(new Temperature
                {
                    Id = id,
                    Label = label,
                    Category = category,
                    SourceId = "hwmon:platform",
                    DeviceId = null,
                    ValueCelsius = celsius,
                    WarningCelsius = warning,
                    CriticalCelsius = critical,
                    ThresholdOrigin = warning is null ? "none" : "baseline",
                    Health = health,
                    LegacySystemMember = false,
                    Reading = new Reading
                    {
                        ObservedAt = celsius is null ? null : at,
                        AgeSeconds = celsius is null ? null : 0,
                        StaleAfterSeconds = 3,
                        Freshness = celsius is null ? "unavailable" : "fresh",
                        LastAttemptAt = at,
                        LastAttemptStatus = celsius is null ? "error" : "ok",
                        Reason = celsius is null ? "hwmon_unavailable" : null,
                    },
                    ReportedStatus = null,
                    SourceThresholdsCelsius = null,
                });
            }
        }

        var source = new Source
        {
            Id = "hwmon:platform",
            Kind = "hwmon",
            RefreshSeconds = 1,
            State = failures == 0 ? "ok" : "partial",
            LastAttemptAt = at,
            LastSuccessAt = failures == 0 ? at : null,
            DurationMs = null,
            Error = failures == 0 ? null : new SourceError
            {
                Code = "channel_read_failed",
                Message = $"{failures} hwmon channels unavailable",
            },
        };
        return new HwmonPlatformResult(temperatures, source);
    }

    private static (string Category, double? Warning, double? Critical) Category(string name) => name switch
    {
        "k10temp" => ("cpu", 75, 90),
        "i350bb" => ("network", 70, 85),
        _ when name.StartsWith("nic", StringComparison.Ordinal) => ("network", 70, 85),
        _ => ("other", null, null),
    };

    private static string SensorId(string resolvedPath, string name, string number)
    {
        var addresses = PciAddress().Matches(resolvedPath);
        var stem = addresses.Count > 0
            ? addresses[addresses.Count - 1].Value.ToLowerInvariant()
            : HwmonSuffix().Replace(resolvedPath, "");
        return $"hwmon:{stem}:{name}:temp{number}";
    }
}
