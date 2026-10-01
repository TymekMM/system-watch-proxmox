namespace SystemWatch.Collector;

public interface ISmartTemperatureReader
{
    Task<SmartTemperature> ReadAsync(string devicePath, bool isHdd,
        CancellationToken cancellationToken = default);
}

/// <summary>Guard rotational disks against spin-up while requesting SMART JSON.</summary>
public sealed class SmartctlProbe(ICommandRunner runner) : ISmartTemperatureReader
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(8);

    public async Task<SmartTemperature> ReadAsync(string devicePath, bool isHdd,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        if (!Path.IsPathFullyQualified(devicePath))
            throw new ArgumentException("SMART device path must be absolute", nameof(devicePath));
        string[] arguments = isHdd
            ? ["-j", "-n", "standby,3", "-A", devicePath]
            : ["-j", "-A", devicePath];
        // smartctl may return nonzero with valid JSON (notably standby status 3).
        var result = await runner.RunAsync("smartctl", arguments, Deadline,
            cancellationToken, acceptNonZeroExit: true);
        return SmartTemperatureParser.Parse(result.Stdout);
    }
}
