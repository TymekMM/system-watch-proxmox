using System.Globalization;
using System.Text.Json;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.HbaRoc.Smoke -- tests/fixtures/platform");
    return 2;
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
    foreach (var name in new[] { "present", "missing" })
    {
        var output = File.ReadAllText(Path.Combine(args[0], $"hba-{name}.txt"));
        var result = HbaRocParser.Parse(output, "2026-09-23T14:00:00Z");
        var actual = JsonSerializer.SerializeToElement(new
        {
            temperature = result.Temperature, source = result.Source,
        });
        using var expected = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(args[0], $"hba-{name}-expected.json")));
        Compare(actual, expected.RootElement, name);
        Console.WriteLine($"PASS {name} HBA output equals reference field by field");
    }

    var previous = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        var warm = HbaRocParser.Parse("roc TEMPERATURE(degree celsius) = 80.25",
            "2026-09-23T14:00:00Z");
        if (warm.Temperature.Health != "warning" || warm.Temperature.ValueCelsius != 80.25)
            throw new Exception("HBA fractional Celsius or threshold failed");
        Console.WriteLine("PASS case-insensitive ROC with culture-invariant warning threshold");
    }
    finally { CultureInfo.CurrentCulture = previous; }

    var missing = HbaRocParser.Parse("ROC temperature(Degree Celsius) = NaN",
        "2026-09-23T14:00:00Z");
    if (missing.Temperature.Health != "unknown" || missing.Source.State != "error")
        throw new Exception("malformed ROC was green");
    Console.WriteLine("PASS invalid ROC cannot report healthy source");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
