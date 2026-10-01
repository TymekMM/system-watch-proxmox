using SystemWatch.Collector;
using SystemWatch.Worker;

CliOptions options;
try { options = CliOptions.Parse(args); }
catch (ArgumentException error)
{
    Console.Error.WriteLine(error.Message);
    return 2;
}

try
{
    var runner = new ProcessCommandRunner();
    var clock = new SystemSourceClock();
    var probes = new LinuxSnapshotProbes(runner, new SmartctlProbe(runner)).Create();
    var worker = new SnapshotWorker(Environment.MachineName, clock, probes,
        new AtomicSnapshotPublisher(options.Output));
    if (!options.Watch)
    {
        await worker.TickAsync();
        return 0;
    }
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Cancel();
    };
    try { await worker.RunAsync(stop.Token); }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
