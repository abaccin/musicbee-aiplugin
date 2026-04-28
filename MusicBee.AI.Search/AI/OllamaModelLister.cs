using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search.AI
{
    /// <summary>
    /// Tiny client for Ollama's native <c>GET /api/tags</c> endpoint, used
    /// purely to populate the model dropdowns in the Settings window. The
    /// rest of the plugin talks to Ollama via its OpenAI-compatible
    /// <c>/v1/chat/completions</c> + <c>/v1/embeddings</c> endpoints reusing
    /// the standard OpenAI-compat clients.
    /// </summary>
    public sealed class OllamaModelLister
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public OllamaModelLister(HttpClient http = null)
        {
            if (http == null) { _http = new HttpClient(); _ownsHttp = true; }
            else              { _http = http;             _ownsHttp = false; }
        }

        /// <summary>
        /// Lists the names of all models currently installed in the Ollama
        /// instance at <paramref name="ollamaEndpoint"/>. Accepts either the
        /// OpenAI-compat base (<c>http://host:port/v1</c>) or the native base
        /// (<c>http://host:port</c>); a trailing <c>/v1</c> is stripped before
        /// resolving <c>/api/tags</c>.
        /// </summary>
        public async Task<IReadOnlyList<string>> ListModelsAsync(string ollamaEndpoint, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(ollamaEndpoint))
                throw new ArgumentException("Ollama endpoint is required.", nameof(ollamaEndpoint));

            var tagsUri = BuildTagsUri(ollamaEndpoint);
            using var req = new HttpRequestMessage(HttpMethod.Get, tagsUri);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Ollama /api/tags failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");
            }
            var payload = await resp.Content.ReadFromJsonAsync<TagsResponse>(cancellationToken: ct).ConfigureAwait(false);
            var names = new List<string>();
            if (payload?.Models != null)
            {
                foreach (var m in payload.Models)
                    if (!string.IsNullOrWhiteSpace(m?.Name)) names.Add(m.Name);
            }
            return names;
        }

        internal static Uri BuildTagsUri(string ollamaEndpoint)
        {
            var s = ollamaEndpoint.Trim().TrimEnd('/');
            const string v1Suffix = "/v1";
            if (s.EndsWith(v1Suffix, StringComparison.OrdinalIgnoreCase))
                s = s.Substring(0, s.Length - v1Suffix.Length);
            return new Uri(s + "/api/tags");
        }

        public void Dispose() { if (_ownsHttp) _http.Dispose(); }

        private class TagsResponse
        {
            [JsonPropertyName("models")] public List<TagsModel> Models { get; set; }
        }
        private class TagsModel
        {
            [JsonPropertyName("name")] public string Name { get; set; }
        }
    }
}
