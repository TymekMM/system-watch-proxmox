using System.Text.Json;
using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.DiskTemperature.Smoke -- tests/fixtures/disk-temperatures");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
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
    var devices = JsonSerializer.Deserialize<List<Device>>(
        File.ReadAllText(Path.Combine(root, "devices.json")))!;
    using var channelDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "channels.json")));
    var channels = channelDoc.RootElement.EnumerateObject().ToDictionary(
        property => property.Name,
        property => (IReadOnlyList<NvmeHwmonChannel>)property.Value.EnumerateArray().Select(sensor =>
            new NvmeHwmonChannel(sensor[0].GetString()!, sensor[1].GetString()!,
                sensor[2].ValueKind == JsonValueKind.Null ? null : sensor[2].GetDouble())).ToList());
    using var smartDocs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "smart.json")));
    var fake = new FixtureSmartReader(smartDocs.RootElement);
    var collector = new DiskTemperatureCollector(fake, () => channels,
        () => "2026-09-27T12:00:01Z");
    var result = await collector.CollectAsync(devices, "2026-09-27T12:00:00Z");
    Check(fake.Calls == 4 && !fake.Paths.Contains("/dev/nvme5n1"),
          "NVMe hwmon device avoids duplicate SMART read");

    var output = JsonSerializer.SerializeToElement(new
    {
        devices = result.Devices, temperatures = result.Temperatures, sources = result.Sources,
    });
    using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "expected.json")));
    Compare(output, expected.RootElement, "temperature fixture");
    Console.WriteLine("PASS C# output equals reference disk adapter field by field");
    Check(result.Devices.Count == 5 && result.Temperatures.Count == 6 &&
          result.Sources.Count == 2 && result.Sources.All(source => source.State == "partial"),
          "standby, errors, NVMe channels and SMART fallback have honest coverage");
    Check(devices.All(device => device.TemperatureIds.Count == 0 && device.PowerState == "unknown"),
          "input disk inventory remains unchanged");

    var empty = await collector.CollectAsync([], "2026-09-27T12:00:00Z");
    Check(empty.Devices.Count == 0 && empty.Sources.Count == 0 && empty.Temperatures.Count == 0,
          "empty disk inventory creates no phantom source");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FixtureSmartReader(JsonElement documents) : ISmartTemperatureReader
{
    public int Calls { get; private set; }
    public List<string> Paths { get; } = new();

    public Task<SmartTemperature> ReadAsync(string devicePath, bool isHdd,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Paths.Add(devicePath);
        if (isHdd != (devicePath is "/dev/sda" or "/dev/sdb" or "/dev/sdc"))
            throw new Exception("wrong HDD standby policy");
        if (devicePath == "/dev/sdc") throw new IOException("synthetic read failure");
        return Task.FromResult(SmartTemperatureParser.Parse(
            documents.GetProperty(devicePath).GetRawText()));
    }
}
