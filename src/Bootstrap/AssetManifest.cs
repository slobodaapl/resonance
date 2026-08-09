using System.Text.Json.Serialization;

namespace Resonance.Bootstrap;

public sealed record AssetManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("revision")] string Revision,
    [property: JsonPropertyName("artifacts")] IReadOnlyList<AssetArtifact> Artifacts);

public sealed record AssetArtifact(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("url")] Uri Url,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("abi")] int Abi,
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("stage")] int Stage);

public sealed record AssetProgress(string Id, long BytesReceived, long TotalBytes);

