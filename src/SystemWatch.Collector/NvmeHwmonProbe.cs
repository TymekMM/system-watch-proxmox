using System.Globalization;
using System.Text.RegularExpressions;

namespace SystemWatch.Collector;

public sealed record NvmeHwmonChannel(string Number, string Label, double? ValueCelsius);

/// <summary>Read-only NVMe hwmon channels keyed by kernel controller number.</summary>
public static partial class NvmeHwmonProbe
{
    [GeneratedRegex(@"/nvme/nvme([0-9]+)/hwmon[0-9]+$")]
    private static partial Regex ControllerPath();

    [GeneratedRegex(@"^/dev/nvme([0-9]+)n[0-9]+$")]
    private static partial Regex NamespacePath();

    [GeneratedRegex(@"^temp([0-9]+)_input$")]
    private static partial Regex TemperatureInput();

    public static string? ControllerForNamespace(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var match = NamespacePath().Match(path);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>> Collect(
        string root = "/sys/class/hwmon")
    {
        var output = new Dictionary<string, IReadOnlyList<NvmeHwmonChannel>>(StringComparer.Ordinal);
        if (!Directory.Exists(root)) return output;
        foreach (var directory in Directory.EnumerateFileSystemEntries(root, "hwmon*"))
        {
            try
            {
                if (File.ReadAllText(Path.Combine(directory, "name")).Trim() != "nvme") continue;
                var target = new DirectoryInfo(directory).ResolveLinkTarget(returnFinalTarget: true);
                var resolved = target?.FullName ?? Path.GetFullPath(directory);
                var match = ControllerPath().Match(resolved);
                if (!match.Success) continue;

                var channels = new List<NvmeHwmonChannel>();
                foreach (var input in Directory.EnumerateFileSystemEntries(directory, "temp*_input")
                             .OrderBy(x => x, StringComparer.Ordinal))
                {
                    var channel = TemperatureInput().Match(Path.GetFileName(input));
                    if (!channel.Success) continue;
                    var number = channel.Groups[1].Value;
                    var labelFile = Path.Combine(directory, $"temp{number}_label");
                    var label = File.Exists(labelFile)
                        ? File.ReadAllText(labelFile).Trim() : $"Sensor {number}";
                    var raw = File.ReadAllText(input).Trim();
                    var celsius = long.TryParse(raw, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var milli)
                        ? milli / 1000.0 : (double?)null;
                    channels.Add(new NvmeHwmonChannel(number, label, celsius));
                }
                output[match.Groups[1].Value] = channels;
            }
            catch (IOException) { /* disappearing sysfs entry: omit its channels */ }
            catch (UnauthorizedAccessException) { /* unreadable entry: omit its channels */ }
        }
        return output;
    }
}
