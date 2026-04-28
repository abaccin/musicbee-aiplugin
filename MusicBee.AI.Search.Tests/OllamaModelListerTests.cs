using FluentAssertions;
using MusicBee.AI.Search.AI;

namespace MusicBee.AI.Search.Tests;

public class OllamaModelListerTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/api/tags")]
    [InlineData("http://localhost:11434", "http://localhost:11434/api/tags")]
    [InlineData("https://example.com:9999/V1", "https://example.com:9999/api/tags")]
    public void BuildTagsUri_StripsTrailingV1AndAppendsApiTags(string endpoint, string expected)
    {
        OllamaModelLister.BuildTagsUri(endpoint).ToString().Should().Be(expected);
    }
}
