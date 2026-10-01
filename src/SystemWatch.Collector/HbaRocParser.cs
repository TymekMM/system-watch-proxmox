using System.Globalization;
using System.Text.RegularExpressions;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record HbaRocResult(Temperature Temperature, Source Source);

/// <summary>Parse optional controller-0 perccli ROC temperature.</summary>
public static partial class HbaRocParser
{
    [GeneratedRegex(@"^\s*ROC temperature\(Degree Celsius\)\s*=\s*(-?\d+(?:\.\d+)?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex RocLine();

    public static HbaRocResult Parse(string output, string observedAt)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedAt);
        var match = RocLine().Match(output);
        double? value = null;
        if (match.Success && double.TryParse(match.Groups[1].Value,
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed))
            value = parsed;
        var available = value is not null;
        var temperature = new Temperature
        {
            Id = "hba:controller0:roc", Label = "HBA ROC", Category = "hba",
            SourceId = "hba:perccli", DeviceId = null,
            ValueCelsius = value, WarningCelsius = 75, CriticalCelsius = 85,
            ThresholdOrigin = "baseline",
            Health = value is null ? "unknown" : value >= 85 ? "critical" :
                value >= 75 ? "warning" : "ok",
            LegacySystemMember = true,
            Reading = new Reading
            {
                ObservedAt = available ? observedAt : null,
                AgeSeconds = available ? 0 : null,
                StaleAfterSeconds = 15,
                Freshness = available ? "fresh" : "unavailable",
                LastAttemptAt = observedAt,
                LastAttemptStatus = available ? "ok" : "error",
                Reason = available ? null : "sensor_unavailable",
            },
            ReportedStatus = null, SourceThresholdsCelsius = null,
        };
        var source = new Source
        {
            Id = "hba:perccli", Kind = "perccli", RefreshSeconds = 2,
            State = available ? "ok" : "error",
            LastAttemptAt = observedAt,
            LastSuccessAt = available ? observedAt : null,
            DurationMs = null,
            Error = available ? null : new SourceError
            {
                Code = "roc_unavailable", Message = "ROC temperature unavailable",
            },
        };
        return new HbaRocResult(temperature, source);
    }
}
