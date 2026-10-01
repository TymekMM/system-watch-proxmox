using SystemWatch.Collector;

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

var scratch = Path.Combine(Path.GetTempPath(), "system-watch-nvme-" + Guid.NewGuid().ToString("N"));
var classRoot = Path.Combine(scratch, "sys", "class", "hwmon");
Directory.CreateDirectory(classRoot);

void Add(int index, string name, string target, params (int Number, string? Label, string Value)[] channels)
{
    Directory.CreateDirectory(target);
    File.WriteAllText(Path.Combine(target, "name"), name);
    foreach (var (number, label, value) in channels)
    {
        if (label is not null) File.WriteAllText(Path.Combine(target, $"temp{number}_label"), label);
        File.WriteAllText(Path.Combine(target, $"temp{number}_input"), value);
    }
    Directory.CreateSymbolicLink(Path.Combine(classRoot, $"hwmon{index}"), target);
}

try
{
    Check(NvmeHwmonProbe.Collect(classRoot).Count == 0,
          "empty VM-style hwmon inventory has no NVMe controllers");

    Add(4, "nvme", Path.Combine(scratch, "sys/devices/pci0000:80/nvme/nvme4/hwmon4"),
        (1, "Composite", "32850"), (2, null, "35000"), (3, "Sensor 3", "37850"));
    Add(7, "k10temp", Path.Combine(scratch, "sys/devices/pci0000:80/cpu/hwmon7"),
        (1, "Tctl", "60000"));
    var result = NvmeHwmonProbe.Collect(classRoot);
    Check(result.Count == 1 && result.TryGetValue("4", out var found) && found.Count == 3 &&
          found.Select(x => x.Label).SequenceEqual(new[] { "Composite", "Sensor 2", "Sensor 3" }),
          "controller path maps ordered channels and excludes CPU hwmon");
    Check(result["4"][0].ValueCelsius == 32.85 &&
          result["4"][2].ValueCelsius == 37.85 &&
          result["4"][1].Number == "2",
          "millidegree precision and missing-label fallback");

    Check(NvmeHwmonProbe.ControllerForNamespace("/dev/nvme4n1") == "4" &&
          NvmeHwmonProbe.ControllerForNamespace("/dev/nvme4n2") == "4" &&
          NvmeHwmonProbe.ControllerForNamespace("/dev/sda") is null,
          "NVMe namespaces identify their controller");

    File.WriteAllText(Path.Combine(scratch, "sys/devices/pci0000:80/nvme/nvme4/hwmon4/temp3_input"),
        "not-a-temperature");
    Check(NvmeHwmonProbe.Collect(classRoot)["4"][2].ValueCelsius is null,
          "invalid channel is unavailable rather than zero");

    Add(8, "nvme", Path.Combine(scratch, "sys/devices/pci0000:80/unrelated/hwmon8"),
        (1, "unmapped", "26000"));
    Check(NvmeHwmonProbe.Collect(classRoot).Count == 1,
          "unrecognized sysfs path is not bound to NVMe device");

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally { Directory.Delete(scratch, recursive: true); }
