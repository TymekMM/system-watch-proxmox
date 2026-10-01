// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Source
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("kind")]
    public required string Kind { get; set; }

    [JsonPropertyName("refresh_seconds")]
    public required double RefreshSeconds { get; set; }

    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("last_attempt_at")]
    public required string? LastAttemptAt { get; set; }

    [JsonPropertyName("last_success_at")]
    public required string? LastSuccessAt { get; set; }

    [JsonPropertyName("duration_ms")]
    public required double? DurationMs { get; set; }

    [JsonPropertyName("error")]
    public required SourceError? Error { get; set; }

}
