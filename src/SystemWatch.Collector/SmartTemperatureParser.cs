using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SystemWatch.Collector;

public sealed record SmartTemperature(double? ValueCelsius, string? Reason, bool Skipped);

/// <summary>Parse smartctl JSON without interpreting packed ATA raw.value.</summary>
public static partial class SmartTemperatureParser
{
    [GeneratedRegex(@"^(-?[0-9]+(?:\.[0-9]+)?)\b")]
    private static partial Regex AtaTemperaturePrefix();

    public static SmartTemperature Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = Get(Get(root, "temperature"), "current");
        var power = Get(Get(root, "power_mode"), "name");
        var mode = power.ValueKind == JsonValueKind.String ? power.GetString()!.ToUpperInvariant() : "";
        var exit = Get(Get(root, "smartctl"), "exit_status");
        int? code = exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out var parsed)
            ? parsed : null;

        if (current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined &&
            (mode.Contains("STANDBY", StringComparison.Ordinal) ||
             mode.Contains("SLEEP", StringComparison.Ordinal)) && code == 3)
            return new SmartTemperature(null, "standby", true);
        if (code != 0) return new SmartTemperature(null, "smartctl_error", false);

        if (current.ValueKind == JsonValueKind.Number &&
            current.TryGetDouble(out var celsius) && double.IsFinite(celsius))
            return new SmartTemperature(celsius, null, false);

        var table = Get(Get(root, "ata_smart_attributes"), "table");
        if (table.ValueKind == JsonValueKind.Array)
        {
            foreach (var attribute in table.EnumerateArray())
            {
                var id = Get(attribute, "id");
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var number) ||
                    number is not (194 or 190)) continue;
                var raw = Get(Get(attribute, "raw"), "string");
                if (raw.ValueKind != JsonValueKind.String) continue;
                var match = AtaTemperaturePrefix().Match(raw.GetString()!);
                if (match.Success && double.TryParse(match.Groups[1].Value,
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                    return new SmartTemperature(value, null, false);
            }
        }
        return new SmartTemperature(null, "temperature_unavailable", false);
    }

    private static JsonElement Get(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value)
            ? value : default;
}
