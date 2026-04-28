using System;
using System.IO;
using System.Text.Json;

namespace MusicBee.AI.Search
{
    public class Settings
    {
        public string Endpoint { get; set; } = "https://models.github.ai/inference";
        public string ChatModel { get; set; } = "openai/gpt-4o-mini";
        public string EmbeddingModel { get; set; } = "openai/text-embedding-3-small";
        public int EmbeddingDimensions { get; set; } = 1536;
        public string Token { get; set; } = "";

        // ---- Provider selection (independent for chat and embeddings) ----
        // "GitHubModels" or "Ollama". Anything else falls back to GitHubModels.
        public string ChatProvider { get; set; } = "GitHubModels";
        public string EmbeddingsProvider { get; set; } = "GitHubModels";

        // ---- Ollama-specific ----
        // Base URL for Ollama's OpenAI-compatible API. /v1/chat/completions and
        // /v1/embeddings are appended at request time. /api/tags is used for
        // model discovery (derived by stripping a trailing /v1).
        public string OllamaEndpoint { get; set; } = "http://localhost:11434/v1";
        public string OllamaChatModel { get; set; } = "";
        public string OllamaEmbeddingModel { get; set; } = "";

        // ---- Request pacing (per-lane) ----
        // Hard ceiling on outbound RPM per lane (chat + embeddings each get
        // their own bucket). The adaptive limiter starts here, halves on 429,
        // and recovers gradually after sustained success up to this ceiling.
        public int MaxRequestsPerMinute { get; set; } = 60;
        public int MinRequestsPerMinute { get; set; } = 4;
        // Max inputs coalesced into one POST on the embeddings lane.
        public int EmbeddingBatchSize { get; set; } = 16;

        public static Settings Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<Settings>(json);
                    if (loaded != null)
                    {
                        if (string.IsNullOrWhiteSpace(loaded.Token))
                        {
                            loaded.Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? "";
                        }
                        ApplyDefaults(loaded);
                        return loaded;
                    }
                }
            }
            catch { }

            return new Settings
            {
                Token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? ""
            };
        }

        // Older settings.json files won't have the newer keys. JSON
        // deserialisation leaves them at their CLR defaults (null / 0), so
        // explicitly substitute the schema defaults here.
        private static void ApplyDefaults(Settings s)
        {
            if (string.IsNullOrWhiteSpace(s.ChatProvider))         s.ChatProvider = "GitHubModels";
            if (string.IsNullOrWhiteSpace(s.EmbeddingsProvider))   s.EmbeddingsProvider = "GitHubModels";
            if (string.IsNullOrWhiteSpace(s.OllamaEndpoint))       s.OllamaEndpoint = "http://localhost:11434/v1";
            if (s.OllamaChatModel == null)                         s.OllamaChatModel = "";
            if (s.OllamaEmbeddingModel == null)                    s.OllamaEmbeddingModel = "";
            if (s.MaxRequestsPerMinute <= 0)                       s.MaxRequestsPerMinute = 60;
            if (s.MinRequestsPerMinute <= 0)                       s.MinRequestsPerMinute = 4;
            if (s.MinRequestsPerMinute > s.MaxRequestsPerMinute)   s.MinRequestsPerMinute = s.MaxRequestsPerMinute;
            if (s.EmbeddingBatchSize <= 0)                         s.EmbeddingBatchSize = 16;
        }

        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }
}
