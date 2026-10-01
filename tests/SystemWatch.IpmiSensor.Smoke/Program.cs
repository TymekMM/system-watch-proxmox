using System.Globalization;
using System.Text.Json;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.IpmiSensor.Smoke -- tests/fixtures/platform");
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
        throw new Exception($"{path}: kind differs");
    switch (expected.ValueKind)
    {
        case JsonValueKind.Object:
            var properties = expected.EnumerateObject().ToList();
            if (actual.EnumerateObject().Count() != properties.Count)
                throw new Exception($"{path}: object field count differs");
            foreach (var property in properties)
            {
                if (!actual.TryGetProperty(property.Name, out var child))
                    throw new Exception($"{path}: missing {property.Name}");
                Compare(child, property.Value, path + "." + property.Name);
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
    var sample = File.ReadAllText(Path.Combine(args[0], "ipmi-synthetic.txt"));
    var result = IpmiSensorParser.Parse(sample, "2026-09-23T14:00:00Z");
    var actual = JsonSerializer.SerializeToElement(new
    {
        temperatures = result.Temperatures, fans = result.Fans,
    });
    using var expected = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(args[0], "ipmi-expected.json")));
    Compare(actual, expected.RootElement, "ipmi fixture");
    Console.WriteLine("PASS IPMI temperatures and fans equal reference output field by field");

    var board = result.Temperatures.Single(t => t.Label == "MB Temp");
    Check(board.Health == "ok" && board.WarningCelsius == 55 &&
          board.SourceThresholdsCelsius?.UpperNoncritical == 50,
          "baseline and BMC thresholds stay distinct");
    Check(result.Temperatures.Single(t => t.Label == "DDR4_D Temp").Health == "unknown" &&
          result.Fans.Single(f => f.Label == "FRNT_FAN2").Rpm is null,
          "unavailable DIMM and fan are not green or zero");

    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        Check(IpmiSensorParser.Parse(sample, "2026-09-23T14:00:00Z")
                  .Temperatures[0].ValueCelsius == 52,
              "IPMI decimal parsing ignores process culture");
    }
    finally { CultureInfo.CurrentCulture = previous; }

    try
    {
        IpmiSensorParser.Parse("garbage", "2026-09-23T14:00:00Z");
        throw new Exception("unparseable IPMI accepted");
    }
    catch (InvalidDataException)
    { Console.WriteLine("PASS unparseable IPMI rejected"); }

    var unknown = IpmiSensorParser.Parse(
        "Aux Temp | 40 | degrees C | ok | na | na | na | na | na | na",
        "2026-09-23T14:00:00Z").Temperatures.Single();
    Check(unknown.Health == "unknown" && unknown.WarningCelsius is null &&
          unknown.Category == "other", "unclassified sensor does not gain baseline threshold");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
