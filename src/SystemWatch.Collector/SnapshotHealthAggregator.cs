using SystemWatch.Contracts;

namespace SystemWatch.Collector;

/// <summary>Builds draft.2 summary health from already aged readings and current source states.</summary>
public static class SnapshotHealthAggregator
{
    public static HealthOverview Aggregate(
        IReadOnlyCollection<Temperature> temperatures,
        IReadOnlyCollection<Pool> pools,
        IReadOnlyCollection<Source> sources)
    {
        ArgumentNullException.ThrowIfNull(temperatures);
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(sources);

        var bySource = sources.ToDictionary(source => source.Id, StringComparer.Ordinal);
        bool SourceOk(string id) => bySource.TryGetValue(id, out var source) && source.State == "ok";

        var hottest = temperatures
            .Where(t => t.ValueCelsius.HasValue && t.Reading.Freshness == "fresh")
            .OrderByDescending(t => t.ValueCelsius!.Value)
            .ThenBy(t => t.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Id;

        var legacy = temperatures.Where(t => t.LegacySystemMember && t.ValueCelsius.HasValue).ToArray();
        var knownLegacy = legacy.Where(t => t.Health != "unknown").ToArray();
        var essentialOk = SourceOk("ipmi") && SourceOk("hba:perccli");
        var systemCoverage = knownLegacy.Length == 0 ? "unavailable" :
            legacy.Any(t => t.Health == "unknown") || !essentialOk ? "partial" : "complete";
        var system = new Summary
        {
            State = essentialOk ? Worst(knownLegacy.Select(t => t.Health)) : "unknown",
            Coverage = systemCoverage,
            ReasonCodes = systemCoverage == "complete" ? new() :
                new() { essentialOk ? "partial_readings" : "source_unavailable" },
        };

        var validPools = pools.Where(pool => pool.Health != "unknown").ToArray();
        var zfsOk = SourceOk("zfs") && pools.All(pool => pool.Reading.Freshness == "fresh");
        var zfsCoverage = validPools.Length == 0 ? "unavailable" : zfsOk ? "complete" : "partial";
        var zfs = new Summary
        {
            State = validPools.Length > 0 && zfsOk ? Worst(validPools.Select(pool => pool.Health)) : "unknown",
            Coverage = zfsCoverage,
            ReasonCodes = zfsCoverage == "complete" ? new() :
                new() { zfsOk && validPools.Length == 0 ? "no_pools" : "source_unavailable" },
        };

        return new HealthOverview
        {
            System = system, Zfs = zfs,
            SystemPolicy = "legacy-v3-temperature-scope",
            HottestTemperatureId = hottest,
        };
    }

    private static string Worst(IEnumerable<string> states)
    {
        var values = states.ToHashSet(StringComparer.Ordinal);
        if (values.Contains("critical")) return "critical";
        if (values.Contains("warning")) return "warning";
        if (values.Contains("ok")) return "ok";
        return "unknown";
    }
}
