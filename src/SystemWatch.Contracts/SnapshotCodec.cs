using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemWatch.Contracts;

public static class SnapshotCodec
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static SnapshotDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<SnapshotDocument>(json, Options)
        ?? throw new JsonException("Expected a snapshot object.");

    public static string Serialize(SnapshotDocument document) =>
        JsonSerializer.Serialize(document, Options);
}
