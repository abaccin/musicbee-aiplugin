using FluentAssertions;

namespace MusicBee.AI.Search.Tests;

public class SettingsDefaultsTests
{
    [Fact]
    public void Load_FromMissingFile_AppliesAllDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = Settings.Load(path);
            s.ChatProvider.Should().Be("GitHubModels");
            s.EmbeddingsProvider.Should().Be("GitHubModels");
            s.OllamaEndpoint.Should().StartWith("http://");
            s.MaxRequestsPerMinute.Should().BeGreaterThan(0);
            s.MinRequestsPerMinute.Should().BeGreaterThan(0);
            s.EmbeddingBatchSize.Should().BeGreaterThan(0);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Load_FromOldJson_FillsInNewKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"Endpoint\":\"https://x\",\"ChatModel\":\"m\",\"EmbeddingModel\":\"e\",\"EmbeddingDimensions\":1536,\"Token\":\"\"}");
            var s = Settings.Load(path);
            s.ChatProvider.Should().Be("GitHubModels");
            s.EmbeddingsProvider.Should().Be("GitHubModels");
            s.OllamaEndpoint.Should().NotBeNullOrEmpty();
            s.OllamaChatModel.Should().Be("");
            s.MaxRequestsPerMinute.Should().Be(60);
            s.MinRequestsPerMinute.Should().Be(4);
            s.EmbeddingBatchSize.Should().Be(16);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void RoundTrip_PreservesNewKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new Settings
            {
                ChatProvider = "Ollama",
                EmbeddingsProvider = "Ollama",
                OllamaEndpoint = "http://host:11434/v1",
                OllamaChatModel = "llama3.1:8b",
                OllamaEmbeddingModel = "nomic-embed-text",
                MaxRequestsPerMinute = 30,
                MinRequestsPerMinute = 2,
                EmbeddingBatchSize = 8,
            };
            s.Save(path);
            var loaded = Settings.Load(path);
            loaded.ChatProvider.Should().Be("Ollama");
            loaded.EmbeddingsProvider.Should().Be("Ollama");
            loaded.OllamaEndpoint.Should().Be("http://host:11434/v1");
            loaded.OllamaChatModel.Should().Be("llama3.1:8b");
            loaded.OllamaEmbeddingModel.Should().Be("nomic-embed-text");
            loaded.MaxRequestsPerMinute.Should().Be(30);
            loaded.MinRequestsPerMinute.Should().Be(2);
            loaded.EmbeddingBatchSize.Should().Be(8);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
