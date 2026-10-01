using System.ComponentModel;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.PlatformProbe.Smoke -- tests/fixtures/platform");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    const string at = "2026-09-23T14:00:00Z";
    var ipmi = File.ReadAllText(Path.Combine(args[0], "ipmi-synthetic.txt"));
    var hba = File.ReadAllText(Path.Combine(args[0], "hba-present.txt"));
    var completeRunner = new FakeRunner(ipmi, hba);
    var complete = await new PlatformSensorProbe(completeRunner).CollectAsync(at);
    Check(completeRunner.Calls == 2 && complete.Temperatures.Count == 4 &&
          complete.Fans.Count == 2 && complete.Sources.All(s => s.State == "ok"),
          "both commands return independent healthy sources");

    var noHba = await new PlatformSensorProbe(new FakeRunner(ipmi, hba, hbaMissing: true))
        .CollectAsync(at);
    Check(noHba.Temperatures.Count == 3 && noHba.Fans.Count == 2 &&
          noHba.Sources[0].State == "ok" && noHba.Sources[1].State == "error" &&
          noHba.Sources[1].Error?.Code == "collection_failed",
          "missing perccli keeps IPMI readings and reports HBA error");

    var noIpmi = await new PlatformSensorProbe(new FakeRunner(ipmi, hba, ipmiMissing: true))
        .CollectAsync(at);
    Check(noIpmi.Temperatures.Count == 1 && noIpmi.Temperatures[0].Category == "hba" &&
          noIpmi.Sources[0].State == "error" && noIpmi.Sources[1].State == "ok",
          "missing ipmitool keeps HBA reading and reports IPMI error");

    var emptyRoc = await new PlatformSensorProbe(new FakeRunner(ipmi, "Controller OK\n"))
        .CollectAsync(at);
    Check(emptyRoc.Temperatures.Count == 3 && emptyRoc.Sources[1].State == "error",
          "unparseable vendor output does not produce green ROC");

    var badIpmi = await new PlatformSensorProbe(new FakeRunner("garbage", hba))
        .CollectAsync(at);
    Check(badIpmi.Temperatures.Count == 1 && badIpmi.Sources[0].State == "error" &&
          badIpmi.Sources[1].State == "ok",
          "malformed IPMI output cannot suppress good HBA");

    using var stop = new CancellationTokenSource();
    stop.Cancel();
    try
    {
        await new PlatformSensorProbe(new FakeRunner(ipmi, hba))
            .CollectAsync(at, stop.Token);
        throw new Exception("cancellation was swallowed");
    }
    catch (OperationCanceledException)
    { Console.WriteLine("PASS caller cancellation is not reported as source failure"); }

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed class FakeRunner(string ipmi, string hba,
    bool ipmiMissing = false, bool hbaMissing = false) : ICommandRunner
{
    public int Calls { get; private set; }

    public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken cancellationToken = default,
        bool acceptNonZeroExit = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout != TimeSpan.FromSeconds(15) || acceptNonZeroExit)
            throw new Exception("platform command policy changed");
        Calls++;
        if (executable == "ipmitool" && arguments.SequenceEqual(new[] { "sensor" }))
            return ipmiMissing ? throw new Win32Exception("ipmitool not installed") :
                Task.FromResult(new CommandResult(ipmi, "", 0));
        if (executable == "/usr/local/sbin/perccli" &&
            arguments.SequenceEqual(new[] { "/c0", "show", "all" }))
            return hbaMissing ? throw new Win32Exception("perccli not installed") :
                Task.FromResult(new CommandResult(hba, "", 0));
        throw new Exception("unexpected executable or argument list");
    }
}
