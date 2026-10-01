using System.Globalization;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Project cached observations into a snapshot without mutating the cache.</summary>
public static class ReadingAger
{
    public static Reading Age(Reading original, double? cacheAgeSeconds, Source status,
        string wallTimestamp)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrWhiteSpace(wallTimestamp);
        var reading = new Reading
        {
            ObservedAt = original.ObservedAt,
            AgeSeconds = original.AgeSeconds,
            StaleAfterSeconds = original.StaleAfterSeconds,
            Freshness = original.Freshness,
            LastAttemptAt = original.LastAttemptAt,
            LastAttemptStatus = original.LastAttemptStatus,
            Reason = original.Reason,
        };
        if (reading.ObservedAt is null || cacheAgeSeconds is null)
        {
            reading.Freshness = "unavailable";
            reading.AgeSeconds = null;
            return reading;
        }

        var age = Math.Max(0, cacheAgeSeconds.Value);
        if (status.LastSuccessAt is not null &&
            DateTimeOffset.TryParse(reading.ObservedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var observed) &&
            DateTimeOffset.TryParse(status.LastSuccessAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var succeeded))
            age += Math.Max(0, (succeeded - observed).TotalSeconds);

        reading.AgeSeconds = Math.Round(age, 3, MidpointRounding.ToEven);
        reading.Freshness = age > reading.StaleAfterSeconds ? "stale" : "fresh";
        if (status.State == "error")
        {
            reading.LastAttemptAt = status.LastAttemptAt ?? wallTimestamp;
            reading.LastAttemptStatus = "error";
            reading.Reason = "source_error";
        }
        return reading;
    }

    public static Temperature Temperature(Temperature original, double? cacheAgeSeconds,
        Source status, string wallTimestamp)
    {
        var reading = Age(original.Reading, cacheAgeSeconds, status, wallTimestamp);
        return new Temperature
        {
            Id = original.Id, Label = original.Label, Category = original.Category,
            SourceId = original.SourceId, DeviceId = original.DeviceId,
            ValueCelsius = original.ValueCelsius,
            WarningCelsius = original.WarningCelsius,
            CriticalCelsius = original.CriticalCelsius,
            ThresholdOrigin = original.ThresholdOrigin,
            Health = reading.Freshness == "fresh" ? original.Health : "unknown",
            LegacySystemMember = original.LegacySystemMember,
            Reading = reading,
            ReportedStatus = original.ReportedStatus,
            SourceThresholdsCelsius = original.SourceThresholdsCelsius,
        };
    }

    public static Fan Fan(Fan original, double? cacheAgeSeconds,
        Source status, string wallTimestamp)
    {
        var reading = Age(original.Reading, cacheAgeSeconds, status, wallTimestamp);
        return new Fan
        {
            Id = original.Id, Label = original.Label, SourceId = original.SourceId,
            Rpm = original.Rpm, ReportedStatus = original.ReportedStatus,
            Health = reading.Freshness == "fresh" ? original.Health : "unknown",
            Reading = reading, SourceThresholdsRpm = original.SourceThresholdsRpm,
        };
    }

    public static Pool Pool(Pool original, double? cacheAgeSeconds,
        Source status, string wallTimestamp)
    {
        var reading = Age(original.Reading, cacheAgeSeconds, status, wallTimestamp);
        return new Pool
        {
            Id = original.Id, Name = original.Name, State = original.State,
            Health = reading.Freshness == "fresh" ? original.Health : "unknown",
            Reading = reading,
            AllocatedBytes = original.AllocatedBytes, SizeBytes = original.SizeBytes,
            FreeBytes = original.FreeBytes,
            CapacityPercent = original.CapacityPercent,
            FragmentationPercent = original.FragmentationPercent,
            Vdevs = new List<Vdev>(original.Vdevs), Scan = original.Scan,
            PermanentErrors = original.PermanentErrors,
        };
    }
}
