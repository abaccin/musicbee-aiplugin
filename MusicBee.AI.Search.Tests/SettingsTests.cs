using FluentAssertions;
using MusicBee.AI.Search;
using System.IO;

namespace MusicBee.AI.Search.Tests;

public class SettingsTests
{
    [Fact]
    public void Defaults_PointAtGitHubModels()
    {
        var s = new Settings();
        s.Endpoint.Should().Be("https://models.github.ai/inference");
        s.ChatModel.Should().Be("openai/gpt-4o-mini");
        s.EmbeddingModel.Should().Be("openai/text-embedding-3-small");
        s.EmbeddingDimensions.Should().Be(1536);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mb_ai_{System.Guid.NewGuid():N}.json");
        try
        {
            var src = new Settings
            {
                Endpoint = "https://example.test/inference",
                ChatModel = "openai/gpt-5",
                EmbeddingModel = "openai/text-embedding-3-large",
                EmbeddingDimensions = 3072,
                Token = "ghp_secret"
            };
            src.Save(path);

            var loaded = Settings.Load(path);
            loaded.Endpoint.Should().Be(src.Endpoint);
            loaded.ChatModel.Should().Be(src.ChatModel);
            loaded.EmbeddingModel.Should().Be(src.EmbeddingModel);
            loaded.EmbeddingDimensions.Should().Be(src.EmbeddingDimensions);
            loaded.Token.Should().Be(src.Token);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mb_ai_missing_{System.Guid.NewGuid():N}.json");
        var s = Settings.Load(path);
        s.Should().NotBeNull();
        s.ChatModel.Should().Be("openai/gpt-4o-mini");
    }
}
