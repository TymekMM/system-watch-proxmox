using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record DiskTemperatureResult(List<Device> Devices,
    List<Temperature> Temperatures, List<Source> Sources);

/// <summary>Join SMART and NVMe hwmon readings to physical disk inventory.</summary>
public sealed class DiskTemperatureCollector(
    ISmartTemperatureReader smart,
    Func<IReadOnlyDictionary<string, IReadOnlyList<NvmeHwmonChannel>>> nvmeChannels,
    Func<string>? now = null)
{
    private readonly Func<string> clock = now ?? (() => DateTimeOffset.UtcNow.ToString(
        "yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));

    public async Task<DiskTemperatureResult> CollectAsync(IReadOnlyList<Device> inventory,
        string? timestamp = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var at = timestamp ?? clock();
        var devices = inventory.Select(Clone).ToList();
        var temperatures = new List<Temperature>();
        var failed = 0;
        var smartAttempts = 0;
        var hwmonUsed = false;
        var hwmonFailed = false;
        var channels = nvmeChannels();

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(device.Id) || string.IsNullOrEmpty(device.Path))
                throw new InvalidDataException("disk inventory entry requires id and path");
            device.TemperatureIds.Clear();
            var controller = NvmeHwmonProbe.ControllerForNamespace(device.Path);
            if (device.Transport == "nvme" && controller is not null &&
                channels.TryGetValue(controller, out var sensors) && sensors.Count > 0)
            {
                hwmonUsed = true;
                foreach (var sensor in sensors)
                {
                    var value = sensor.ValueCelsius;
                    var record = MakeTemperature(device, "hwmon:temp" + sensor.Number,
                        sensor.Label, "nvme", "hwmon:nvme", value, at,
                        value is null ? "hwmon_unavailable" : null, skipped: false);
                    temperatures.Add(record);
                    device.TemperatureIds.Add(record.Id);
                    if (value is null) hwmonFailed = true;
                }
                continue;
            }

            smartAttempts++;
            SmartTemperature reading;
            try { reading = await smart.ReadAsync(device.Path, device.Media == "hdd", cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or TimeoutException or JsonException or
                InvalidDataException or Win32Exception or UnauthorizedAccessException)
            {
                reading = new SmartTemperature(null, "smartctl_failed", false);
            }
            if (reading.ValueCelsius is null && !reading.Skipped) failed++;
            if (reading.Skipped) device.PowerState = "standby";
            else if (reading.ValueCelsius is not null) device.PowerState = "active";
            var category = device.Transport == "nvme" ? "nvme" : "disks";
            var temperature = MakeTemperature(device, "smart:temperature", "Temperature",
                category, "smart", reading.ValueCelsius, at, reading.Reason, reading.Skipped);
            temperatures.Add(temperature);
            device.TemperatureIds.Add(temperature.Id);
        }

        var sources = new List<Source>();
        if (smartAttempts > 0)
            sources.Add(MakeSource("smart", "smart", 10, failed > 0, at,
                "device_read_failed", $"{failed} disk temperature reads failed"));
        if (hwmonUsed)
            sources.Add(MakeSource("hwmon:nvme", "hwmon", 1, hwmonFailed, at,
                "channel_read_failed", "At least one NVMe channel was unavailable"));
        return new DiskTemperatureResult(devices, temperatures, sources);
    }

    private Temperature MakeTemperature(Device device, string sensorId, string label,
        string category, string sourceId, double? value, string at, string? reason, bool skipped)
    {
        var warning = category == "nvme" ? 65 : 45;
        var critical = category == "nvme" ? 75 : 55;
        return new Temperature
        {
            Id = device.Id + ":" + sensorId,
            Label = label, Category = category, SourceId = sourceId, DeviceId = device.Id,
            ValueCelsius = value, WarningCelsius = warning, CriticalCelsius = critical,
            ThresholdOrigin = "baseline",
            Health = value is null ? "unknown" : value >= critical ? "critical" :
                value >= warning ? "warning" : "ok",
            LegacySystemMember = false,
            Reading = new Reading
            {
                ObservedAt = value is null ? null : at,
                AgeSeconds = value is null ? null : 0,
                StaleAfterSeconds = 30,
                Freshness = value is null ? "unavailable" : "fresh",
                LastAttemptAt = value is null ? clock() : at,
                LastAttemptStatus = skipped ? "skipped" : value is null ? "error" : "ok",
                Reason = reason,
            },
            ReportedStatus = null,
            SourceThresholdsCelsius = null,
        };
    }

    private static Source MakeSource(string id, string kind, double seconds, bool partial,
        string at, string code, string message) => new()
    {
        Id = id, Kind = kind, RefreshSeconds = seconds,
        State = partial ? "partial" : "ok",
        LastAttemptAt = at, LastSuccessAt = partial ? null : at,
        DurationMs = null,
        Error = partial ? new SourceError { Code = code, Message = message } : null,
    };

    private static Device Clone(Device device) => new()
    {
        Id = device.Id, IdentityStability = device.IdentityStability,
        Path = device.Path, Aliases = new List<string>(device.Aliases),
        Model = device.Model, Serial = device.Serial,
        Transport = device.Transport, Media = device.Media,
        PowerState = device.PowerState, ZfsMembership = device.ZfsMembership,
        PoolIds = new List<string>(device.PoolIds),
        TemperatureIds = new List<string>(device.TemperatureIds), Usage = device.Usage,
    };
}
