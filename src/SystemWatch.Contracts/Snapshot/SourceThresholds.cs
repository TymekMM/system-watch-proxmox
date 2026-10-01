// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class SourceThresholds
{
    [JsonPropertyName("lower_nonrecoverable")]
    public required double? LowerNonrecoverable { get; set; }

    [JsonPropertyName("lower_critical")]
    public required double? LowerCritical { get; set; }

    [JsonPropertyName("lower_noncritical")]
    public required double? LowerNoncritical { get; set; }

    [JsonPropertyName("upper_noncritical")]
    public required double? UpperNoncritical { get; set; }

    [JsonPropertyName("upper_critical")]
    public required double? UpperCritical { get; set; }

    [JsonPropertyName("upper_nonrecoverable")]
    public required double? UpperNonrecoverable { get; set; }

}
