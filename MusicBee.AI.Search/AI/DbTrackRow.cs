using System.Text.Json.Serialization;

public class DbTrackRow
{
    public const string CollectionName = "music-tracks";

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("artist")]
    public string Artist { get; set; }

    [JsonPropertyName("album")]
    public string Album { get; set; }

    [JsonPropertyName("genre")]
    public string Genre { get; set; }

    [JsonPropertyName("year")]
    public string Year { get; set; }

    [JsonPropertyName("comment")]
    public string Comment { get; set; }

    [JsonPropertyName("rating")]
    public string Rating { get; set; }

    [JsonPropertyName("embedding")]
    public float[] Embedding { get; set; }

    [JsonPropertyName("fingerprint")]
    public string Fingerprint { get; set; }

    public override string ToString()
        => $"{Title} - {Artist} ({Album}, {Year})";
}
