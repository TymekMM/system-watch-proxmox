// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Scan
{
    [JsonPropertyName("type")]
    public required string Type { get; set; }

    [JsonPropertyName("state")]
    public required string State { get; set; }

    [JsonPropertyName("started_at")]
    public required string? StartedAt { get; set; }

    [JsonPropertyName("finished_at")]
    public required string? FinishedAt { get; set; }

    [JsonPropertyName("progress_percent")]
    public required double? ProgressPercent { get; set; }

    [JsonPropertyName("scanned_bytes")]
    public required string? ScannedBytes { get; set; }

    [JsonPropertyName("issued_bytes")]
    public required string? IssuedBytes { get; set; }

    [JsonPropertyName("total_bytes")]
    public required string? TotalBytes { get; set; }

    [JsonPropertyName("scan_bytes_per_second")]
    public required double? ScanBytesPerSecond { get; set; }

    [JsonPropertyName("issue_bytes_per_second")]
    public required double? IssueBytesPerSecond { get; set; }

    [JsonPropertyName("eta_seconds")]
    public required double? EtaSeconds { get; set; }

    [JsonPropertyName("repaired_bytes")]
    public required string? RepairedBytes { get; set; }

    [JsonPropertyName("errors")]
    public required string? Errors { get; set; }

    [JsonPropertyName("raw_text")]
    public required string? RawText { get; set; }

}
