// Contract DTOs for schema/snapshot.schema.json (1.0.0-draft.2).
// UTC timestamps and unsigned decimal counters stay strings to preserve wire precision.
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public sealed class ZfsInventory
{
    [JsonPropertyName("inventory_state")]
    public required string InventoryState { get; set; }

    [JsonPropertyName("pool_count")]
    public required long? PoolCount { get; set; }

    [JsonPropertyName("pools")]
    public required List<Pool> Pools { get; set; } = new();

}
