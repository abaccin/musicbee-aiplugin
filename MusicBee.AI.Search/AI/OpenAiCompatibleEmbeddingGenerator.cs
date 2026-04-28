using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search.AI
{
    /// <summary>
    /// Embedding generator targeting any OpenAI-compatible <c>/embeddings</c>
    /// endpoint (GitHub Models, Ollama's <c>/v1</c>, etc.).
    ///
    /// All outbound requests are routed through an internal
    /// <see cref="RequestPacer{T}"/> that paces calls to the configured RPM
    /// ceiling, retries 429 / 5xx with adaptive backoff, and BATCHES pending
    /// single-input requests into one POST with <c>inputs = [...]</c> to keep
    /// the request count tractable when ingesting large libraries.
    /// </summary>
    public sealed class OpenAiCompatibleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _model;
        private readonly Uri _endpoint;
        private readonly Uri _embeddingsUri;
        private readonly Func<string> _tokenProvider;
        private readonly int _dimensions;
        private readonly EmbeddingsPacer _pacer;

        public OpenAiCompatibleEmbeddingGenerator(
            Uri endpoint,
            string model,
            Func<string> tokenProvider,
            int dimensions,
            int maxRequestsPerMinute = 60,
            int minRequestsPerMinute = 4,
            int batchSize = 16,
            HttpClient httpClient = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _tokenProvider = tokenProvider ?? (() => null);
            _dimensions = dimensions;
            var baseStr = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? endpoint.AbsoluteUri
                : endpoint.AbsoluteUri + "/";
            _embeddingsUri = new Uri(new Uri(baseStr), "embeddings");
            if (httpClient == null) { _http = new HttpClient(); _ownsHttp = true; }
            else                    { _http = httpClient;       _ownsHttp = false; }

            _pacer = new EmbeddingsPacer(
                _http, _embeddingsUri, () => _model, _tokenProvider,
                Math.Max(1, batchSize),
                maxRequestsPerMinute, minRequestsPerMinute);
        }

        public void Dispose()
        {
            try { _pacer.Dispose(); } catch { }
            if (_ownsHttp) _http.Dispose();
        }

        public object GetService(Type serviceType, object serviceKey = null)
        {
            if (serviceType == typeof(EmbeddingGeneratorMetadata))
                return new EmbeddingGeneratorMetadata("openai-compat", _endpoint, _model, _dimensions);
            return serviceType?.IsInstanceOfType(this) == true ? this : null;
        }

        public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var inputs = values?.ToList() ?? new List<string>();
            var result = new GeneratedEmbeddings<Embedding<float>>();
            if (inputs.Count == 0) return result;

            // Each input is enqueued as its own pending item so the pacer can
            // coalesce inputs from CONCURRENT GenerateAsync callers into a
            // single POST. When a single caller passes >1 inputs they're
            // logically one batch already; we still go through the pacer so
            // they participate in pacing/retry.
            var tasks = new List<Task<float[]>>(inputs.Count);
            foreach (var text in inputs)
                tasks.Add(_pacer.EnqueueAsync(text, cancellationToken));

            var vectors = await Task.WhenAll(tasks).ConfigureAwait(false);
            foreach (var v in vectors) result.Add(new Embedding<float>(v));
            return result;
        }

        // ---- Pacer specialisation ----
        internal sealed class PendingEmbedding
        {
            public string Text;
            public TaskCompletionSource<float[]> Tcs = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration CtReg;
        }

        internal sealed class EmbeddingsPacer : RequestPacer<PendingEmbedding>
        {
            private readonly Uri _embeddingsUri;
            private readonly Func<string> _modelProvider;
            private readonly Func<string> _tokenProvider;
            private readonly int _batchSize;

            public EmbeddingsPacer(
                HttpClient http, Uri embeddingsUri,
                Func<string> modelProvider, Func<string> tokenProvider,
                int batchSize, int ceilingRpm, int floorRpm)
                : base("embeddings", http, ceilingRpm, ceilingRpm, floorRpm)
            {
                _embeddingsUri = embeddingsUri;
                _modelProvider = modelProvider;
                _tokenProvider = tokenProvider;
                _batchSize = batchSize;
            }

            protected override int MaxBatchSize => _batchSize;

            public Task<float[]> EnqueueAsync(string text, CancellationToken ct)
            {
                var p = new PendingEmbedding { Text = text };
                if (ct.CanBeCanceled)
                    p.CtReg = ct.Register(() => p.Tcs.TrySetCanceled(ct));
                Enqueue(p);
                return p.Tcs.Task;
            }

            protected override async Task DispatchAsync(IReadOnlyList<PendingEmbedding> batch, CancellationToken ct)
            {
                // Drop already-cancelled callers from the batch so we don't
                // pay tokens to embed text that nobody is waiting for.
                var live = new List<PendingEmbedding>(batch.Count);
                foreach (var b in batch)
                {
                    if (!b.Tcs.Task.IsCompleted) live.Add(b);
                    else b.CtReg.Dispose();
                }
                if (live.Count == 0) return;

                var req = new EmbeddingRequest
                {
                    Model = _modelProvider(),
                    Input = live.Select(p => p.Text ?? "").ToList()
                };

                HttpResponseMessage resp;
                try
                {
                    resp = await SendWithRetryAsync(() =>
                    {
                        var http = new HttpRequestMessage(HttpMethod.Post, _embeddingsUri)
                        {
                            Content = JsonContent.Create(req)
                        };
                        ApplyAuth(http, _tokenProvider);
                        return http;
                    }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    foreach (var p in live) p.Tcs.TrySetException(ex);
                    foreach (var p in live) p.CtReg.Dispose();
                    return;
                }

                using (resp)
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var ex = new HttpRequestException(
                            $"Embeddings request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
                        foreach (var p in live) p.Tcs.TrySetException(ex);
                        foreach (var p in live) p.CtReg.Dispose();
                        return;
                    }

                    EmbeddingResponse payload = null;
                    try
                    {
                        payload = await resp.Content.ReadFromJsonAsync<EmbeddingResponse>(cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        foreach (var p in live) p.Tcs.TrySetException(ex);
                        foreach (var p in live) p.CtReg.Dispose();
                        return;
                    }

                    var byIndex = new Dictionary<int, float[]>();
                    if (payload?.Data != null)
                    {
                        foreach (var d in payload.Data)
                            byIndex[d.Index] = d.Embedding ?? Array.Empty<float>();
                    }

                    for (int i = 0; i < live.Count; i++)
                    {
                        var vec = byIndex.TryGetValue(i, out var v) ? v : Array.Empty<float>();
                        live[i].Tcs.TrySetResult(vec);
                        live[i].CtReg.Dispose();
                    }
                }
            }

            protected override void FaultItem(PendingEmbedding item, Exception ex)
            {
                item.Tcs.TrySetException(ex);
                item.CtReg.Dispose();
            }
        }

        // ---- HTTP helpers ----
        // Auth header is OPTIONAL: when the token provider returns null/empty
        // (e.g. local Ollama), we send the request without an Authorization
        // header instead of throwing.
        internal static void ApplyAuth(HttpRequestMessage req, Func<string> tokenProvider)
        {
            var token = tokenProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // ---- DTOs ----
        private class EmbeddingRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("input")] public List<string> Input { get; set; }
        }
        private class EmbeddingResponse
        {
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("data")] public List<EmbeddingItem> Data { get; set; }
        }
        private class EmbeddingItem
        {
            [JsonPropertyName("index")] public int Index { get; set; }
            [JsonPropertyName("embedding")] public float[] Embedding { get; set; }
        }
    }
}
