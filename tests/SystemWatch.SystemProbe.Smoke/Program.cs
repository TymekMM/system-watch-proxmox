using System.Globalization;
using SystemWatch.Collector;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.SystemProbe.Smoke -- tests/fixtures/proc");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

void Reject(string uptime, string load, string description)
{
    try
    {
        ProcSystemProbe.Parse(uptime, load);
        throw new Exception($"Accepted malformed input: {description}");
    }
    catch (FormatException)
    {
        Console.WriteLine($"PASS {description}");
    }
}

try
{
    var uptime = await File.ReadAllTextAsync(Path.Combine(args[0], "uptime"));
    var load = await File.ReadAllTextAsync(Path.Combine(args[0], "loadavg"));
    var metrics = ProcSystemProbe.Parse(uptime, load);
    Check(metrics.UptimeSeconds == 136567.75, "captured VM uptime");
    Check(metrics.LoadAverages.SequenceEqual(new double?[] { 0.04, 0.03, 0.04 }),
          "captured VM 1/5/15-minute loads");

    var fileMetrics = ProcSystemProbe.ReadFrom(args[0]);
    Check(fileMetrics.UptimeSeconds == metrics.UptimeSeconds &&
          fileMetrics.LoadAverages.SequenceEqual(metrics.LoadAverages),
          "read-only file probe matches parser");

    Reject("", load, "missing uptime rejected");
    Reject("NaN 22", load, "non-finite uptime rejected");
    Reject("-1 22", load, "negative uptime rejected");
    Reject(uptime, "0.04 0.03", "missing load interval rejected");
    Reject(uptime, "0.04 broken 0.04 2/509 891102", "invalid load rejected");

    var originalCulture = CultureInfo.CurrentCulture;
    try
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        Check(ProcSystemProbe.Parse(uptime, load).UptimeSeconds == 136567.75,
              "proc decimal parsing is culture-independent");
    }
    finally
    {
        CultureInfo.CurrentCulture = originalCulture;
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
