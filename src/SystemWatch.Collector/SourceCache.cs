using System.Diagnostics;
using System.Globalization;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public interface ISourceClock
{
    double MonotonicSeconds { get; }
    string UtcNow { get; }
}

public sealed class SystemSourceClock : ISourceClock
{
    public double MonotonicSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public string UtcNow => DateTimeOffset.UtcNow.ToString(
        "yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>One independently scheduled source with last-success retention.</summary>
public sealed class SourceCache<T>(string id, string kind, TimeSpan interval,
    Func<CancellationToken, Task<T>> collect, ISourceClock clock,
    Func<T, Source?>? embeddedSource = null) where T : class
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object stateGate = new();
    private double nextDue;
    private double? successfulAtMonotonic;
    private string? lastAttemptAt;
    private string? lastSuccessAt;
    private string? error;
    private double? durationMs;

    private T? payload;
    public T? Payload { get { lock (stateGate) return payload; } }

    public async Task<bool> UpdateIfDueAsync(CancellationToken cancellationToken = default)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        cancellationToken.ThrowIfCancellationRequested();
        await gate.WaitAsync(cancellationToken);
        try
        {
            var mono = clock.MonotonicSeconds;
            if (mono < nextDue) return false;
            cancellationToken.ThrowIfCancellationRequested();
            var wall = clock.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            T? candidate = null;
            string? failureMessage = null;
            try
            {
                candidate = await collect(cancellationToken);
                if (candidate is null) throw new InvalidDataException("collector returned no data");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                failureMessage = $"{failure.GetType().Name}: {failure.Message}";
            }
            finally
            {
                stopwatch.Stop();
            }
            // Publish the completed observation and its metadata together.
            lock (stateGate)
            {
                if (failureMessage is null)
                {
                    payload = candidate;
                    successfulAtMonotonic = mono;
                    lastSuccessAt = wall;
                }
                error = failureMessage;
                durationMs = stopwatch.Elapsed.TotalMilliseconds;
                lastAttemptAt = wall;
            }
            nextDue = mono + interval.TotalSeconds;
            return true;
        }
        finally { gate.Release(); }
    }

    public double? AgeSeconds { get { lock (stateGate) return CurrentAge(); } }

    private double? CurrentAge() => successfulAtMonotonic is double at
        ? Math.Max(0, clock.MonotonicSeconds - at) : null;

    public CachedSnapshotPart<T> Capture()
    {
        lock (stateGate) return new(payload, CurrentAge(), CurrentStatus());
    }

    public Source Status()
    {
        lock (stateGate) return CurrentStatus();
    }

    private Source CurrentStatus()
    {
        var reported = error is null && payload is not null ? embeddedSource?.Invoke(payload) : null;
        var state = error is not null ? "error" : payload is null ? "unavailable" :
            reported?.State ?? "ok";
        return new Source
        {
            Id = id, Kind = kind, RefreshSeconds = interval.TotalSeconds,
            State = state,
            LastAttemptAt = lastAttemptAt,
            LastSuccessAt = lastSuccessAt,
            DurationMs = durationMs,
            Error = error is not null ? new SourceError
            {
                Code = "collection_failed",
                Message = error[..Math.Min(240, error.Length)],
            } : reported?.Error,
        };
    }
}
