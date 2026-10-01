// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Reading
{
    [JsonPropertyName("observed_at")]
    public required string? ObservedAt { get; set; }

    [JsonPropertyName("age_seconds")]
    public required double? AgeSeconds { get; set; }

    [JsonPropertyName("stale_after_seconds")]
    public required double StaleAfterSeconds { get; set; }

    [JsonPropertyName("freshness")]
    public required string Freshness { get; set; }

    [JsonPropertyName("last_attempt_at")]
    public required string? LastAttemptAt { get; set; }

    [JsonPropertyName("last_attempt_status")]
    public required string LastAttemptStatus { get; set; }

    [JsonPropertyName("reason")]
    public required string? Reason { get; set; }

}
