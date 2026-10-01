using System.Text.Json;
using SystemWatch.Contracts;

if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.Contracts.Smoke -- tests/fixtures/snapshot.example.json [sanitized-vm-snapshot.json]");
    return 2;
}

var original = await File.ReadAllTextAsync(args[0]);
SnapshotDocument Parse() => SnapshotCodec.Deserialize(original);
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var fixture = Parse();
    Check(SnapshotSemantics.Validate(fixture).Count == 0, "fixture semantic validation");
    var roundTrip = SnapshotCodec.Deserialize(SnapshotCodec.Serialize(fixture));
    Check(roundTrip.Devices.Count == fixture.Devices.Count &&
          roundTrip.Zfs.Pools[0].SizeBytes == fixture.Zfs.Pools[0].SizeBytes &&
          roundTrip.Temperatures.Count == fixture.Temperatures.Count,
          "typed JSON round trip");

    var unknownReference = Parse();
    unknownReference.Temperatures[0].SourceId = "missing:source";
    Check(SnapshotSemantics.Validate(unknownReference).Any(e => e.Contains("unresolved ID")),
          "missing source reference rejected");

    var staleGreen = Parse();
    staleGreen.Temperatures[0].Reading.Freshness = "stale";
    Check(SnapshotSemantics.Validate(staleGreen).Any(e => e.Contains("non-fresh reading")),
          "stale green reading rejected");

    var brokenCounter = Parse();
    brokenCounter.Zfs.Pools[0].SizeBytes = "01";
    Check(SnapshotSemantics.Validate(brokenCounter).Any(e => e.Contains("unsigned decimal")),
          "noncanonical byte counter rejected");

    var cycle = Parse();
    cycle.Zfs.Pools[0].Vdevs[0].ParentId = cycle.Zfs.Pools[0].Vdevs[1].Id;
    Check(SnapshotSemantics.Validate(cycle).Any(e => e.Contains("vdev cycle")),
          "vdev cycle rejected");

    var missingField = original.Replace("\"schema_version\": \"1.0.0-draft.2\",", "");
    try
    {
        SnapshotCodec.Deserialize(missingField);
        throw new Exception("missing required schema_version was accepted");
    }
    catch (JsonException)
    {
        Console.WriteLine("PASS missing required property rejected");
    }

    if (args.Length == 2)
    {
        var vm = SnapshotCodec.Deserialize(await File.ReadAllTextAsync(args[1]));
        var vmErrors = SnapshotSemantics.Validate(vm);
        Check(vmErrors.Count == 0,
              "sanitized VM semantic validation: " + string.Join("; ", vmErrors));
        var vmRoundTrip = SnapshotCodec.Deserialize(SnapshotCodec.Serialize(vm));
        Check(vmRoundTrip.Zfs.Pools.Count == vm.Zfs.Pools.Count &&
              vmRoundTrip.Devices.Count == vm.Devices.Count &&
              vmRoundTrip.Temperatures.Count == vm.Temperatures.Count,
              "sanitized VM typed JSON round trip");
    }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
