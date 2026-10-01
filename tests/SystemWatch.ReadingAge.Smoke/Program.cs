using SystemWatch.Collector;
using SystemWatch.Contracts;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project tests/SystemWatch.ReadingAge.Smoke -- tests/fixtures/snapshot.example.json");
    return 2;
}

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var document = SnapshotCodec.Deserialize(File.ReadAllText(args[0]));
    var original = document.Temperatures.Single(t => t.Category == "disks" && t.Health == "ok");
    var clock = new FakeClock();
    var calls = 0;
    var cache = new SourceCache<string>("smart", "smart", TimeSpan.FromSeconds(10),
        _ =>
        {
            if (++calls > 1) throw new IOException("disk unavailable");
            return Task.FromResult("observation");
        }, clock);
    await cache.UpdateIfDueAsync();
    var first = ReadingAger.Temperature(original, cache.AgeSeconds,
        cache.Status(), clock.UtcNow);
    Check(first.Health == "ok" && first.Reading.AgeSeconds == 0 &&
          first.Reading.Freshness == "fresh", "new observation starts fresh");

    clock.Advance(10);
    await cache.UpdateIfDueAsync();
    var failed = ReadingAger.Temperature(original, cache.AgeSeconds,
        cache.Status(), clock.UtcNow);
    Check(failed.Health == "ok" && failed.Reading.Freshness == "fresh" &&
          failed.Reading.AgeSeconds == 10 && failed.Reading.LastAttemptStatus == "error" &&
          failed.Reading.Reason == "source_error" &&
          failed.Reading.ObservedAt == original.Reading.ObservedAt,
          "failed SMART poll retains recent observation and records error");

    clock.Advance(20);
    var boundary = ReadingAger.Temperature(original, cache.AgeSeconds,
        cache.Status(), clock.UtcNow);
    Check(boundary.Health == "ok" && boundary.Reading.Freshness == "fresh",
          "stale threshold is strict greater-than");
    clock.Advance(1);
    var stale = ReadingAger.Temperature(original, cache.AgeSeconds,
        cache.Status(), clock.UtcNow);
    Check(stale.Health == "unknown" && stale.Reading.Freshness == "stale" &&
          stale.Reading.AgeSeconds == 31 && original.Health == "ok" &&
          original.Reading.Freshness == "fresh",
          "old cached reading becomes unknown without mutating original");

    var fan = ReadingAger.Fan(document.Fans[0], cache.AgeSeconds, cache.Status(), clock.UtcNow);
    var pool = ReadingAger.Pool(document.Zfs.Pools[0], cache.AgeSeconds,
        cache.Status(), clock.UtcNow);
    Check(fan.Health == "unknown" && pool.Health == "unknown" &&
          fan.Reading.Freshness == "stale" && pool.Reading.Freshness == "stale",
          "fan and pool health degrade on stale observation");

    var completion = Status("ok", "2026-09-23T14:00:02Z", "2026-09-23T14:00:02Z");
    var gap = ReadingAger.Temperature(original, 29, completion, clock.UtcNow);
    Check(gap.Reading.AgeSeconds == 31 && gap.Reading.Freshness == "stale",
          "sampling duration is included without using wall time for aging");

    var noSuccess = ReadingAger.Temperature(original, null,
        Status("unavailable", null, null), clock.UtcNow);
    Check(noSuccess.Health == "unknown" && noSuccess.Reading.Freshness == "unavailable" &&
          noSuccess.Reading.AgeSeconds is null,
          "uninitialized cache cannot publish fresh green");

    var unavailable = new Reading
    {
        ObservedAt = null, AgeSeconds = null, StaleAfterSeconds = 30,
        Freshness = "unavailable", LastAttemptAt = "2026-09-23T14:00:00Z",
        LastAttemptStatus = "skipped", Reason = "standby",
    };
    var stillSkipped = ReadingAger.Age(unavailable, cache.AgeSeconds, cache.Status(), clock.UtcNow);
    Check(stillSkipped.Freshness == "unavailable" && stillSkipped.AgeSeconds is null &&
          stillSkipped.LastAttemptStatus == "skipped",
          "unobserved standby reading remains unavailable and skipped");

    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

static Source Status(string state, string? attempt, string? success) => new()
{
    Id = "smart", Kind = "smart", RefreshSeconds = 10, State = state,
    LastAttemptAt = attempt, LastSuccessAt = success,
    DurationMs = null, Error = null,
};

internal sealed class FakeClock : ISourceClock
{
    public double MonotonicSeconds { get; private set; }
    public string UtcNow => DateTimeOffset.Parse("2026-09-23T14:00:00Z")
        .AddSeconds(MonotonicSeconds).ToString("yyyy-MM-ddTHH:mm:ssZ");
    public void Advance(double seconds) => MonotonicSeconds += seconds;
}
