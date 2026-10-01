// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Temperature
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("label")]
    public required string Label { get; set; }

    [JsonPropertyName("category")]
    public required string Category { get; set; }

    [JsonPropertyName("source_id")]
    public required string SourceId { get; set; }

    [JsonPropertyName("device_id")]
    public required string? DeviceId { get; set; }

    [JsonPropertyName("value_celsius")]
    public required double? ValueCelsius { get; set; }

    [JsonPropertyName("warning_celsius")]
    public required double? WarningCelsius { get; set; }

    [JsonPropertyName("critical_celsius")]
    public required double? CriticalCelsius { get; set; }

    [JsonPropertyName("threshold_origin")]
    public required string ThresholdOrigin { get; set; }

    [JsonPropertyName("health")]
    public required string Health { get; set; }

    [JsonPropertyName("legacy_system_member")]
    public required bool LegacySystemMember { get; set; }

    [JsonPropertyName("reading")]
    public required Reading Reading { get; set; }

    [JsonPropertyName("source_thresholds_celsius")]
    public required SourceThresholds? SourceThresholdsCelsius { get; set; }

    [JsonPropertyName("reported_status")]
    public required string? ReportedStatus { get; set; }

}
