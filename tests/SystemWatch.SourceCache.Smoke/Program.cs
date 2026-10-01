using SystemWatch.Collector;
using SystemWatch.Contracts;

void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine($"PASS {description}");
}

try
{
    var clock = new FakeClock();
    var calls = 0;
    var smart = new SourceCache<Sample>("smart", "smart", TimeSpan.FromSeconds(10),
        _ =>
        {
            calls++;
            if (calls == 2) throw new IOException("temporary disk error");
            return Task.FromResult(new Sample("reading-" + calls));
        }, clock);
    Check(smart.Status().State == "unavailable" && smart.AgeSeconds is null &&
          smart.Status().LastAttemptAt is null, "new cache reports unavailable");

    Check(await smart.UpdateIfDueAsync() && calls == 1 && smart.Status().State == "ok" &&
          smart.Status().LastSuccessAt == clock.UtcNow && smart.AgeSeconds == 0,
          "first poll records success and source time");
    clock.Advance(9);
    Check(!await smart.UpdateIfDueAsync() && calls == 1 && smart.AgeSeconds == 9,
          "slow SMART source not repolled before its interval");

    clock.Advance(1);
    Check(await smart.UpdateIfDueAsync() && smart.Status().State == "error" &&
          smart.Status().Error?.Code == "collection_failed" && calls == 2 &&
          smart.Payload?.Value == "reading-1" && smart.AgeSeconds == 10 &&
          smart.Status().LastSuccessAt != smart.Status().LastAttemptAt,
          "failure retains last payload and reports separate attempt time");
    clock.Advance(2);
    Check(!await smart.UpdateIfDueAsync() && smart.Status().State == "error",
          "failed source respects retry interval");
    clock.Advance(8);
    Check(await smart.UpdateIfDueAsync() && smart.Status().State == "ok" &&
          smart.Payload?.Value == "reading-3" && smart.AgeSeconds == 0,
          "later success replaces payload and clears error");

    var fastCalls = 0;
    var fast = new SourceCache<Sample>("hwmon:nvme", "hwmon", TimeSpan.FromSeconds(1),
        _ => { fastCalls++; return Task.FromResult(new Sample("nvme")); }, clock);
    Check(await fast.UpdateIfDueAsync() && fastCalls == 1, "fast NVMe source polls independently");
    clock.Advance(1);
    Check(await fast.UpdateIfDueAsync() && fastCalls == 2 &&
          !(await smart.UpdateIfDueAsync()) && calls == 3,
          "fast NVMe update does not trigger SMART");

    var partial = new SourceCache<Sample>("hwmon:platform", "hwmon",
        TimeSpan.FromSeconds(1), _ => Task.FromResult(new Sample("partial")), clock,
        _ => new Source
        {
            Id = "hwmon:platform", Kind = "hwmon", RefreshSeconds = 1,
            State = "partial", LastAttemptAt = clock.UtcNow,
            LastSuccessAt = null, DurationMs = null,
            Error = new SourceError { Code = "channel_read_failed", Message = "1 channel unavailable" },
        });
    await partial.UpdateIfDueAsync();
    Check(partial.Status().State == "partial" &&
          partial.Status().Error?.Code == "channel_read_failed" &&
          partial.Status().LastSuccessAt == clock.UtcNow,
          "embedded partial source preserves successful cache observation");

    var noData = new SourceCache<Sample>("zfs", "zfs", TimeSpan.FromSeconds(1),
        _ => Task.FromResult<Sample>(null!), clock);
    await noData.UpdateIfDueAsync();
    Check(noData.Status().State == "error" && noData.Payload is null &&
          noData.Status().Error?.Code == "collection_failed",
          "collector returning no data fails closed");

    var canceled = new SourceCache<Sample>("ipmi", "ipmi", TimeSpan.FromSeconds(5),
        token => { token.ThrowIfCancellationRequested(); return Task.FromResult(new Sample("never")); },
        clock);
    using (var stop = new CancellationTokenSource())
    {
        stop.Cancel();
        try { await canceled.UpdateIfDueAsync(stop.Token); throw new Exception("cancellation swallowed"); }
        catch (OperationCanceledException)
        { Console.WriteLine("PASS cancellation does not record failed attempt"); }
    }
    Check(canceled.Status().State == "unavailable" && canceled.Status().LastAttemptAt is null,
          "canceled source remains unattempted");

    var completion = new TaskCompletionSource<Sample>(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentCalls = 0;
    var concurrent = new SourceCache<Sample>("smart", "smart", TimeSpan.FromSeconds(1),
        _ => ++concurrentCalls == 1 ? Task.FromResult(new Sample("before")) : completion.Task, clock);
    await concurrent.UpdateIfDueAsync();
    clock.Advance(1);
    var pending = concurrent.UpdateIfDueAsync();
    var during = concurrent.Capture();
    Check(during.Payload?.Value == "before" && during.Status.State == "ok" &&
        during.Status.LastSuccessAt != clock.UtcNow,
        "in-flight collection preserves the complete previous cache observation");
    completion.SetException(new IOException("delayed failure"));
    await pending;
    var failedCapture = concurrent.Capture();
    Check(failedCapture.Payload?.Value == "before" && failedCapture.Status.State == "error" &&
        failedCapture.Status.LastSuccessAt == during.Status.LastSuccessAt &&
        failedCapture.Status.LastAttemptAt == clock.UtcNow,
        "atomic capture retains payload and success time with completed failure metadata");

    var restarted = new SourceCache<Sample>("smart", "smart", TimeSpan.FromSeconds(10),
        _ => Task.FromResult(new Sample("fresh")), clock);
    Check(restarted.Payload is null && restarted.Status().State == "unavailable" &&
          await restarted.UpdateIfDueAsync() && restarted.AgeSeconds == 0,
          "restart does not inherit old cache payload or deadline");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed record Sample(string Value);

internal sealed class FakeClock : ISourceClock
{
    public double MonotonicSeconds { get; private set; }
    public string UtcNow => $"2026-09-27T12:00:{MonotonicSeconds:00}Z";
    public void Advance(double seconds) => MonotonicSeconds += seconds;
}
