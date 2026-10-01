using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.HealthAggregation.Smoke -- tests/fixtures/snapshot.example.json tests/fixtures/node-fixture-sanitized.json");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var exampleText = File.ReadAllText(args[0]);
    SnapshotDocument Example()
    {
        var d = SnapshotCodec.Deserialize(exampleText);
        d.Sources.Add(new Source
        {
            Id = "hba:perccli", Kind = "perccli", RefreshSeconds = 2, State = "ok",
            LastAttemptAt = null, LastSuccessAt = null, DurationMs = null, Error = null,
        });
        return d;
    }
    HealthOverview Aggregate(SnapshotDocument d) =>
        SnapshotHealthAggregator.Aggregate(d.Temperatures, d.Zfs.Pools, d.Sources);

    var baseline = Example();
    baseline.Health = Aggregate(baseline);
    Check(baseline.Health.System.State == "ok" && baseline.Health.System.Coverage == "complete" &&
          baseline.Health.Zfs.State == "ok" && baseline.Health.Zfs.Coverage == "complete" &&
          baseline.Health.HottestTemperatureId == "ipmi:CPU Temp" &&
          baseline.Health.SystemPolicy == "legacy-v3-temperature-scope",
          "example fixture matches reference healthy summary");
    Check(SnapshotSemantics.Validate(baseline).Count == 0,
          "aggregated example remains semantically valid");

    var missingHba = Example();
    missingHba.Sources.Single(s => s.Id == "hba:perccli").State = "error";
    var degraded = Aggregate(missingHba).System;
    Check(degraded.State == "unknown" && degraded.Coverage == "partial" &&
          degraded.ReasonCodes.SequenceEqual(new[] { "source_unavailable" }),
          "essential HBA error prevents green system summary");

    var missingIpmi = Example();
    missingIpmi.Sources.Single(s => s.Id == "ipmi").State = "partial";
    Check(Aggregate(missingIpmi).System.State == "unknown",
          "partial IPMI source prevents green system summary");

    var staleLegacy = Example();
    staleLegacy.Temperatures[0].Health = "unknown";
    staleLegacy.Temperatures[0].Reading.Freshness = "stale";
    var staleSystem = Aggregate(staleLegacy).System;
    Check(staleSystem.State == "unknown" && staleSystem.Coverage == "unavailable" &&
          staleSystem.ReasonCodes.SequenceEqual(new[] { "partial_readings" }) &&
          Aggregate(staleLegacy).HottestTemperatureId == "smart:example-a:temperature",
          "stale legacy reading loses health while fresh disk remains hottest");

    var warning = Example();
    warning.Temperatures[0].Health = "warning";
    Check(Aggregate(warning).System.State == "warning", "legacy warning propagates");
    warning.Temperatures[0].Health = "critical";
    Check(Aggregate(warning).System.State == "critical", "legacy critical propagates");

    var tie = Example();
    tie.Temperatures[1].ValueCelsius = tie.Temperatures[0].ValueCelsius;
    Check(Aggregate(tie).HottestTemperatureId == "ipmi:CPU Temp",
          "equal hottest values use ordinal ID tie break");

    var failedZfs = Example();
    failedZfs.Sources.Single(s => s.Id == "zfs").State = "error";
    var zfsError = Aggregate(failedZfs).Zfs;
    Check(zfsError.State == "unknown" && zfsError.Coverage == "partial" &&
          zfsError.ReasonCodes.SequenceEqual(new[] { "source_unavailable" }),
          "failed ZFS source does not publish a green summary");

    var stalePool = Example();
    stalePool.Zfs.Pools[0].Health = "unknown";
    stalePool.Zfs.Pools[0].Reading.Freshness = "stale";
    Check(Aggregate(stalePool).Zfs.Coverage == "unavailable" &&
          Aggregate(stalePool).Zfs.State == "unknown",
          "stale only pool loses ZFS coverage");

    var noPools = Example();
    noPools.Zfs.Pools.Clear();
    var empty = Aggregate(noPools).Zfs;
    Check(empty.Coverage == "unavailable" && empty.State == "unknown" &&
          empty.ReasonCodes.SequenceEqual(new[] { "no_pools" }),
          "healthy empty ZFS inventory reports no pools");

    var vm = SnapshotCodec.Deserialize(File.ReadAllText(args[1]));
    var vmHealth = Aggregate(vm);
    Check(vmHealth.Zfs.State == "unknown" && vmHealth.Zfs.Coverage == "partial" &&
          vmHealth.Zfs.ReasonCodes.SequenceEqual(new[] { "source_unavailable" }),
          "anonymized VM source IDs cannot claim live ZFS source health");

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
