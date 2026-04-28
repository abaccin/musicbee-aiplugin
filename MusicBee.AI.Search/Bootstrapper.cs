using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MusicBee.AI.Search.AI;
using MusicBee.AI.Search.Storage;
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search
{
    /// <summary>
    /// Composition root. Builds the embedding generator, vector store, and
    /// track ingestor / chat service used by both the MusicBee plugin and the Console host.
    ///
    /// Providers (chat / embeddings) can be swapped at runtime via
    /// <see cref="ApplyChangedSettings"/>. When the embedding identity
    /// (provider, model, dimensions) changes, the local vector store is
    /// dropped and <see cref="EmbeddingProviderChanged"/> is raised so the
    /// host can re-trigger ingest.
    /// </summary>
    public sealed class Bootstrapper : IDisposable
    {
        private Settings _settings;
        private readonly ILoggerFactory _loggerFactory;

        public string DataDirectory { get; }
        public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
        public string StorePath => Path.Combine(DataDirectory, "musicbee_ai_search.bin");
        public string DimensionMarkerPath => Path.Combine(DataDirectory, "embedding.meta.json");

        public IEmbeddingGenerator<string, Embedding<float>> EmbeddingGenerator { get; private set; }
        public IChatClient ChatClient { get; private set; }
        public TrackStore Store { get; private set; }
        public TrackIngestor TrackIngestor { get; private set; }
        public SemanticSearch SemanticSearch { get; private set; }
        public ChatService ChatService { get; private set; }

        /// <summary>
        /// Raised after <see cref="ApplyChangedSettings"/> when the embedding
        /// provider/model/dimensions changed and the vector store has been
        /// rebuilt. The host should re-run ingest.
        /// </summary>
        public event EventHandler EmbeddingProviderChanged;

        /// <summary>
        /// Raised after <see cref="ApplyChangedSettingsAsync"/> any time the
        /// underlying <see cref="ChatService"/> / <see cref="SemanticSearch"/>
        /// instances were swapped out so UI can re-wire its event handlers.
        /// </summary>
        public event EventHandler ServicesChanged;

        public Bootstrapper(string baseDataPath, Settings settings = null, ILoggerFactory loggerFactory = null)
        {
            if (string.IsNullOrEmpty(baseDataPath)) throw new ArgumentNullException(nameof(baseDataPath));

            DataDirectory = Path.Combine(baseDataPath, "musicbee_ai_search");
            Directory.CreateDirectory(DataDirectory);

            _settings = settings ?? Settings.Load(SettingsPath);
            _loggerFactory = loggerFactory ?? LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information));

            HandleDimensionChange(_settings);

            EmbeddingGenerator = BuildEmbeddingGenerator(_settings);
            ChatClient = BuildChatClient(_settings);

            Store = new TrackStore(StorePath);
            Store.InitialiseAsync().GetAwaiter().GetResult();

            TrackIngestor = new TrackIngestor(EmbeddingGenerator, Store,
                _loggerFactory.CreateLogger<TrackIngestor>());

            SemanticSearch = new SemanticSearch(Store, EmbeddingGenerator);
            ChatService = new ChatService(ChatClient, SemanticSearch);
        }

        public Settings Settings => _settings;

        // ---- Provider-aware client construction ----
        private static IEmbeddingGenerator<string, Embedding<float>> BuildEmbeddingGenerator(Settings s)
        {
            var isOllama = string.Equals(s.EmbeddingsProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
            var endpoint = new Uri(isOllama ? s.OllamaEndpoint : s.Endpoint);
            var model = isOllama ? s.OllamaEmbeddingModel : s.EmbeddingModel;
            // Ollama doesn't need an Authorization header; passing null means
            // the client skips it. GitHub Models still requires the PAT.
            Func<string> tokenProvider = isOllama ? (Func<string>)(() => null) : (() => s.Token);
            return new OpenAiCompatibleEmbeddingGenerator(
                endpoint, model ?? "", tokenProvider, s.EmbeddingDimensions,
                s.MaxRequestsPerMinute, s.MinRequestsPerMinute, s.EmbeddingBatchSize);
        }

        private IChatClient BuildChatClient(Settings s)
        {
            var isOllama = string.Equals(s.ChatProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
            var endpoint = new Uri(isOllama ? s.OllamaEndpoint : s.Endpoint);
            var model = isOllama ? s.OllamaChatModel : s.ChatModel;
            Func<string> tokenProvider = isOllama ? (Func<string>)(() => null) : (() => s.Token);
            var raw = new OpenAiCompatibleChatClient(
                endpoint, model ?? "", tokenProvider,
                s.MaxRequestsPerMinute, s.MinRequestsPerMinute);
            return new ChatClientBuilder(raw)
                .UseFunctionInvocation()
                .Build();
        }

        /// <summary>
        /// Applies updated settings: rebuilds the embedding/chat clients with
        /// the new provider/model, swaps them into the existing search and
        /// chat services, persists the settings, and -- if the embedding
        /// identity changed -- rebuilds the vector store and raises
        /// <see cref="EmbeddingProviderChanged"/> so the host re-runs ingest.
        /// </summary>
        public async Task ApplyChangedSettingsAsync(Settings updated, CancellationToken cancellationToken = default)
        {
            if (updated == null) throw new ArgumentNullException(nameof(updated));

            var oldEmbeddingId = EmbeddingIdentity(_settings);
            var newEmbeddingId = EmbeddingIdentity(updated);

            updated.Save(SettingsPath);
            _settings = updated;

            // Swap chat client (cheap; just rebuild + dispose old).
            var oldChat = ChatClient;
            ChatClient = BuildChatClient(_settings);
            ChatService = new ChatService(ChatClient, SemanticSearch);
            (oldChat as IDisposable)?.Dispose();

            // Swap embedding generator.
            var oldEmb = EmbeddingGenerator;
            EmbeddingGenerator = BuildEmbeddingGenerator(_settings);

            if (oldEmbeddingId != newEmbeddingId)
            {
                // Embedding space changed -> existing vectors are meaningless.
                await RebuildAsync(cancellationToken).ConfigureAwait(false);
                HandleDimensionChange(_settings);
                EmbeddingProviderChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Same embedding identity -> just rewire ingest + search to
                // the new generator instance so they pick up pacing changes.
                TrackIngestor = new TrackIngestor(EmbeddingGenerator, Store,
                    _loggerFactory.CreateLogger<TrackIngestor>());
                SemanticSearch = new SemanticSearch(Store, EmbeddingGenerator);
                ChatService = new ChatService(ChatClient, SemanticSearch);
            }

            (oldEmb as IDisposable)?.Dispose();
            ServicesChanged?.Invoke(this, EventArgs.Empty);
        }

        private static string EmbeddingIdentity(Settings s)
        {
            var isOllama = string.Equals(s.EmbeddingsProvider, "Ollama", StringComparison.OrdinalIgnoreCase);
            var model = isOllama ? s.OllamaEmbeddingModel : s.EmbeddingModel;
            var endpoint = isOllama ? s.OllamaEndpoint : s.Endpoint;
            return $"{(isOllama ? "Ollama" : "GitHubModels")}|{endpoint}|{model}|{s.EmbeddingDimensions}";
        }

        /// <summary>
        /// Drops the local vector store file and recreates an empty store so the
        /// ingest pipeline can rebuild from scratch. Caller is responsible for
        /// re-running ingest.
        /// </summary>
        public async Task RebuildAsync(CancellationToken cancellationToken = default)
        {
            try { Store?.Dispose(); } catch { }

            try { if (File.Exists(StorePath)) File.Delete(StorePath); } catch { /* best effort */ }
            try { if (File.Exists(StorePath + ".tmp")) File.Delete(StorePath + ".tmp"); } catch { }
            // Clean up artefacts from older SQLite-backed / sqlite-vec builds.
            CleanupLegacyArtefacts();

            Store = new TrackStore(StorePath);
            await Store.InitialiseAsync(cancellationToken).ConfigureAwait(false);

            TrackIngestor = new TrackIngestor(EmbeddingGenerator, Store,
                _loggerFactory.CreateLogger<TrackIngestor>());
            SemanticSearch.SetStore(Store);
        }

        private void HandleDimensionChange(Settings s)
        {
            try
            {
                var current = EmbeddingIdentity(s);
                if (File.Exists(DimensionMarkerPath))
                {
                    var doc = JsonSerializer.Deserialize<DimMeta>(File.ReadAllText(DimensionMarkerPath));
                    if (doc != null && doc.Identity != current)
                    {
                        if (File.Exists(StorePath)) File.Delete(StorePath);
                    }
                }
                File.WriteAllText(DimensionMarkerPath, JsonSerializer.Serialize(new DimMeta
                {
                    Identity = current,
                    // Keep legacy fields populated for forward/backward compat.
                    Model = string.Equals(s.EmbeddingsProvider, "Ollama", StringComparison.OrdinalIgnoreCase)
                        ? s.OllamaEmbeddingModel : s.EmbeddingModel,
                    Dimensions = s.EmbeddingDimensions,
                    Provider = string.Equals(s.EmbeddingsProvider, "Ollama", StringComparison.OrdinalIgnoreCase)
                        ? "Ollama" : "GitHubModels"
                }));
            }
            catch
            {
                // best-effort
            }
        }

        private void CleanupLegacyArtefacts()
        {
            foreach (var name in new[] { "musicbee_ai_search.db", "musicbee_ai_search.db-wal", "musicbee_ai_search.db-shm" })
            {
                try
                {
                    var p = Path.Combine(DataDirectory, name);
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { /* best effort */ }
            }
        }

        public void Dispose()
        {
            Store?.Dispose();
            (EmbeddingGenerator as IDisposable)?.Dispose();
            (ChatClient as IDisposable)?.Dispose();
        }

        private class DimMeta
        {
            public string Identity { get; set; }
            public string Provider { get; set; }
            public string Model { get; set; }
            public int Dimensions { get; set; }
        }
    }
}
