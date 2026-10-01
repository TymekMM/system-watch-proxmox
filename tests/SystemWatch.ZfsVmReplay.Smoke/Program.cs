using System.Text.Json;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.ZfsVmReplay.Smoke -- tests/fixtures/node-fixture-zfs");
    return 2;
}

void Compare(JsonElement actual, JsonElement expected, string path)
{
    if (actual.ValueKind != expected.ValueKind)
        throw new Exception($"{path}: kind {actual.ValueKind} != {expected.ValueKind}");
    switch (expected.ValueKind)
    {
        case JsonValueKind.Object:
            var properties = expected.EnumerateObject().ToList();
            if (actual.EnumerateObject().Count() != properties.Count)
                throw new Exception($"{path}: object field count differs");
            foreach (var property in properties)
            {
                if (!actual.TryGetProperty(property.Name, out var value))
                    throw new Exception($"{path}: missing {property.Name}");
                Compare(value, property.Value, path + "." + property.Name);
            }
            break;
        case JsonValueKind.Array:
            if (actual.GetArrayLength() != expected.GetArrayLength())
                throw new Exception($"{path}: array length differs");
            for (var index = 0; index < expected.GetArrayLength(); index++)
                Compare(actual[index], expected[index], $"{path}[{index}]");
            break;
        case JsonValueKind.Number:
            if (actual.GetDouble() != expected.GetDouble())
                throw new Exception($"{path}: numeric value differs");
            break;
        default:
            if (actual.ToString() != expected.ToString())
                throw new Exception($"{path}: value differs");
            break;
    }
}

try
{
    var root = args[0];
    var list = ZpoolListParser.Parse(File.ReadAllText(Path.Combine(root, "zpool-list.tsv")));
    var pools = ZpoolStatusParser.Parse(File.ReadAllText(Path.Combine(root, "zpool-status.json")), list);
    var disks = LsblkInventoryParser.Parse(File.ReadAllText(Path.Combine(root, "lsblk.json")));
    var paths = JsonSerializer.Deserialize<Dictionary<string, string>>(
        File.ReadAllText(Path.Combine(root, "realpaths.json")))!;
    var result = ZfsInventoryAssembler.Assemble(pools, disks, path => paths[path],
        "2026-09-27T12:00:00Z");
    Console.WriteLine("PASS sanitized VM inputs parsed and joined");

    var output = JsonSerializer.SerializeToElement(new { devices = result.Devices, zfs = result.Zfs });
    using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "expected.json")));
    Compare(output, expected.RootElement, "zfs fixture");
    Console.WriteLine("PASS C# inventory equals reference adapter output field by field");

    if (result.Zfs.PoolCount != 2 || result.Devices.Count != 3 ||
        result.Devices.Count(d => d.ZfsMembership == "member") != 2 ||
        result.Devices.Count(d => d.Usage == "system") != 1)
        throw new Exception("captured VM coverage changed");
    Console.WriteLine("PASS two pools, two members and one system disk");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
