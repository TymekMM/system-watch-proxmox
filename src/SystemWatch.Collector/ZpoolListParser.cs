using System.Globalization;

namespace SystemWatch.Collector;

/// <summary>One row from zpool list -H -p -o name,size,alloc,free,cap,frag,health.</summary>
public sealed record ZpoolListEntry(
    string Name,
    string? SizeBytes,
    string? AllocatedBytes,
    string? FreeBytes,
    double? CapacityPercent,
    double? FragmentationPercent,
    string State);

public static class ZpoolListParser
{
    public static IReadOnlyDictionary<string, ZpoolListEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var pools = new Dictionary<string, ZpoolListEntry>(StringComparer.Ordinal);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.Split('\t');
            if (fields.Length != 7) throw new InvalidDataException("unexpected zpool list column count");
            var name = fields[0];
            if (pools.ContainsKey(name)) throw new InvalidDataException($"duplicate pool: {name}");
            pools.Add(name, new ZpoolListEntry(
                name,
                Unsigned(fields[1]),
                Unsigned(fields[2]),
                Unsigned(fields[3]),
                Percentage(fields[4]),
                Percentage(fields[5]),
                fields[6]));
        }
        return pools;
    }

    private static string? Unsigned(string value)
    {
        if (value == "-") return null;
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') ||
            value.Any(c => c is < '0' or > '9'))
            throw new InvalidDataException($"invalid unsigned integer: '{value}'");
        // Keep the canonical decimal text, including values above Int64.MaxValue.
        return value;
    }

    private static double? Percentage(string value)
    {
        if (value == "-") return null;
        var numeric = value.TrimEnd('%');
        if (!double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result) || result < 0 || result > 100)
            throw new InvalidDataException($"invalid percentage: '{value}'");
        return result;
    }
}
