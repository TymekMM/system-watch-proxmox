using SystemWatch.Collector;

const string at = "2026-09-23T14:00:00Z";
var scratch = Path.Combine(Path.GetTempPath(), "system-watch-hwmon-" + Guid.NewGuid().ToString("N"));
var classRoot = Path.Combine(scratch, "class", "hwmon");
Directory.CreateDirectory(classRoot);

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

string Add(string address, string name, int index, params (int Number, string? Label, string Value)[] channels)
{
    var target = Path.Combine(scratch, "devices", address, "hwmon", $"hwmon{index}");
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "name"), name);
    foreach (var (number, label, value) in channels)
    {
        if (label is not null) File.WriteAllText(Path.Combine(target, $"temp{number}_label"), label);
        File.WriteAllText(Path.Combine(target, $"temp{number}_input"), value);
    }
    Directory.CreateSymbolicLink(Path.Combine(classRoot, $"hwmon{index}"), target);
    return target;
}

try
{
    var empty = HwmonPlatformProbe.Collect(classRoot, at);
    Check(empty.Temperatures.Count == 0 && empty.Source.State == "ok" &&
          empty.Source.LastSuccessAt == at,
          "VM-style empty hwmon inventory is healthy");

    var cpu = Add("0000:00:18.3", "k10temp", 1,
                  (1, "Tctl", "39375"), (5, "Tccd3", "40000"));
    Add("0000:c8:00.0", "nic2", 5,
        (1, "PHY Temperature", "53000"), (2, "MAC Temperature", "50214"));
    Add("0000:81:00.0", "nvme", 8, (1, "Composite", "32850"));
    var result = HwmonPlatformProbe.Collect(classRoot, at);
    Check(result.Temperatures.Count == 4 &&
          result.Temperatures.Select(t => t.Label).SequenceEqual(
              new[] { "Tctl", "Tccd3", "PHY Temperature", "MAC Temperature" }),
          "CPU/NIC channels ordered and NVMe excluded");
    Check(result.Temperatures[3].ValueCelsius == 50.214 &&
          result.Temperatures[2].WarningCelsius == 70 &&
          result.Temperatures[2].Id == "hwmon:0000:c8:00.0:nic2:temp1",
          "millidegrees, thresholds and stable PCI identity");
    Check(result.Source.State == "ok" && result.Temperatures.All(t => t.Reading.Freshness == "fresh"),
          "fresh readings and healthy source");

    Add("0000:42:00.0", "i350bb", 9, (1, "loc1", "na"), (2, null, "71000"));
    var partial = HwmonPlatformProbe.Collect(classRoot, at);
    var failed = partial.Temperatures.Single(t => t.Label == "loc1");
    Check(failed.ValueCelsius is null && failed.Health == "unknown" &&
          failed.Reading.Freshness == "unavailable" && partial.Source.State == "partial" &&
          partial.Source.Error?.Code == "channel_read_failed" && partial.Source.LastSuccessAt is null,
          "failed channel yields unknown reading and partial source");
    Check(partial.Temperatures.Single(t => t.Label == "Temp2").Health == "warning",
          "missing label falls back and network threshold applies");

    Directory.CreateSymbolicLink(Path.Combine(classRoot, "hwmon10"), cpu);
    try
    {
        HwmonPlatformProbe.Collect(classRoot, at);
        throw new Exception("duplicate physical sensor was accepted");
    }
    catch (InvalidDataException)
    {
        Console.WriteLine("PASS duplicate physical sensor identity rejected");
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    Directory.Delete(scratch, recursive: true);
}
