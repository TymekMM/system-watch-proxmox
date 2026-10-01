// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class Device
{
    [JsonPropertyName("id")]
    public required string Id { get; set; }

    [JsonPropertyName("identity_stability")]
    public required string IdentityStability { get; set; }

    [JsonPropertyName("path")]
    public required string? Path { get; set; }

    [JsonPropertyName("aliases")]
    public required List<string> Aliases { get; set; } = new();

    [JsonPropertyName("model")]
    public required string? Model { get; set; }

    [JsonPropertyName("serial")]
    public required string? Serial { get; set; }

    [JsonPropertyName("transport")]
    public required string Transport { get; set; }

    [JsonPropertyName("media")]
    public required string Media { get; set; }

    [JsonPropertyName("power_state")]
    public required string PowerState { get; set; }

    [JsonPropertyName("zfs_membership")]
    public required string ZfsMembership { get; set; }

    [JsonPropertyName("pool_ids")]
    public required List<string> PoolIds { get; set; } = new();

    [JsonPropertyName("temperature_ids")]
    public required List<string> TemperatureIds { get; set; } = new();

    [JsonPropertyName("usage")]
    public required string Usage { get; set; }

}
