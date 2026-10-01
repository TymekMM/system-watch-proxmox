using SystemWatch.Collector;

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var runner = new FixtureRunner();
    var probe = new SmartctlProbe(runner);
    var standby = await probe.ReadAsync("/dev/sda", isHdd: true);
    Check(standby == new SmartTemperature(null, "standby", true),
          "HDD standby exit retains JSON and is marked skipped");

    var active = await probe.ReadAsync("/dev/sdb", isHdd: true);
    Check(active == new SmartTemperature(34.5, null, false),
          "active HDD reads structured Celsius with standby guard");

    var nvme = await probe.ReadAsync("/dev/nvme0n1", isHdd: false);
    Check(nvme == new SmartTemperature(null, "smartctl_error", false),
          "SSD read omits HDD guard and preserves smartctl error JSON");
    Check(runner.Calls == 3, "all commands have fixed argv, 8-second timeout and nonzero opt-in");

    try
    {
        await probe.ReadAsync("sda", isHdd: true);
        throw new Exception("relative device path accepted");
    }
    catch (ArgumentException)
    { Console.WriteLine("PASS relative device path rejected before process start"); }

    var failed = new SmartctlProbe(new FailedRunner());
    try
    {
        await failed.ReadAsync("/dev/sdc", isHdd: true);
        throw new Exception("runner failure accepted");
    }
    catch (TimeoutException)
    { Console.WriteLine("PASS command timeout propagates for source error reporting"); }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FixtureRunner : ICommandRunner
{
    public int Calls { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Calls switch
        {
            0 => new[] { "-j", "-n", "standby,3", "-A", "/dev/sda" },
            1 => new[] { "-j", "-n", "standby,3", "-A", "/dev/sdb" },
            2 => new[] { "-j", "-A", "/dev/nvme0n1" },
            _ => throw new Exception("unexpected extra SMART call"),
        };
        if (executable != "smartctl" || !arguments.SequenceEqual(expected) ||
            timeout != TimeSpan.FromSeconds(8) || !acceptNonZeroExit)
            throw new Exception("SMART command policy changed");
        var result = Calls++ switch
        {
            0 => new CommandResult("""
                {"smartctl":{"exit_status":3},"power_mode":{"name":"STANDBY"}}
                """, "", 3),
            1 => new CommandResult("""
                {"smartctl":{"exit_status":0},"temperature":{"current":34.5}}
                """, "", 0),
            _ => new CommandResult("""
                {"smartctl":{"exit_status":8}}
                """, "", 8),
        };
        return Task.FromResult(result);
    }
}

internal sealed class FailedRunner : ICommandRunner
{
    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false) => throw new TimeoutException("SMART timeout");
}
