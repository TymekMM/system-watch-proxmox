using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using SystemWatch.Contracts;

namespace SystemWatch.Collector;

public sealed record PlatformSensorResult(List<Temperature> Temperatures,
    List<Fan> Fans, List<Source> Sources);

/// <summary>Collect IPMI and optional HBA independently; callers manage cache age.</summary>
public sealed class PlatformSensorProbe(ICommandRunner runner,
    string perccliPath = "/usr/local/sbin/perccli")
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    public async Task<PlatformSensorResult> CollectAsync(string? timestamp = null,
        CancellationToken cancellationToken = default)
    {
        var at = timestamp ?? DateTimeOffset.UtcNow.ToString(
            "yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
        var temperatures = new List<Temperature>();
        var fans = new List<Fan>();
        var sources = new List<Source>();

        try
        {
            var response = await runner.RunAsync("ipmitool", ["sensor"], Deadline,
                cancellationToken);
            var parsed = IpmiSensorParser.Parse(response.Stdout, at);
            temperatures.AddRange(parsed.Temperatures);
            fans.AddRange(parsed.Fans);
            sources.Add(MakeSource("ipmi", "ipmi", 5, at));
        }
        catch (Exception error) when (IsCollectionFailure(error, cancellationToken))
        {
            sources.Add(MakeSource("ipmi", "ipmi", 5, at, error));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await runner.RunAsync(perccliPath, ["/c0", "show", "all"],
                Deadline, cancellationToken);
            var parsed = HbaRocParser.Parse(response.Stdout, at);
            if (parsed.Temperature.ValueCelsius is null)
                throw new InvalidDataException("HBA ROC temperature missing");
            temperatures.Add(parsed.Temperature);
            sources.Add(MakeSource("hba:perccli", "perccli", 2, at));
        }
        catch (Exception error) when (IsCollectionFailure(error, cancellationToken))
        {
            sources.Add(MakeSource("hba:perccli", "perccli", 2, at, error));
        }
        return new PlatformSensorResult(temperatures, fans, sources);
    }

    private static bool IsCollectionFailure(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        error is IOException or InvalidDataException or TimeoutException or Win32Exception or
            JsonException or UnauthorizedAccessException;

    private static Source MakeSource(string id, string kind, double interval,
        string at, Exception? failure = null)
    {
        var message = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}";
        return new Source
        {
            Id = id, Kind = kind, RefreshSeconds = interval,
            State = failure is null ? "ok" : "error",
            LastAttemptAt = at,
            LastSuccessAt = failure is null ? at : null,
            DurationMs = null,
            Error = message is null ? null : new SourceError
            {
                Code = "collection_failed",
                Message = message[..Math.Min(240, message.Length)],
            },
        };
    }
}
