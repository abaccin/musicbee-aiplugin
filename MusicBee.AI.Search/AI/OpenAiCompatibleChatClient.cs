using Microsoft.Extensions.AI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MusicBee.AI.Search.AI
{
    /// <summary>
    /// Chat client targeting any OpenAI-compatible <c>/chat/completions</c>
    /// endpoint (GitHub Models, Ollama's <c>/v1</c>, etc.). Tool/function
    /// calling is supported. All requests are scheduled through an internal
    /// <see cref="RequestPacer{T}"/> so chat traffic shares a single sender,
    /// is paced to a configurable RPM, and retries 429 / 5xx with adaptive
    /// backoff that honours <c>Retry-After</c>.
    /// </summary>
    public sealed class OpenAiCompatibleChatClient : IChatClient
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly string _model;
        private readonly Uri _endpoint;
        private readonly Uri _chatCompletionsUri;
        private readonly Func<string> _tokenProvider;
        private readonly ChatPacer _pacer;

        public OpenAiCompatibleChatClient(
            Uri endpoint,
            string model,
            Func<string> tokenProvider,
            int maxRequestsPerMinute = 60,
            int minRequestsPerMinute = 4,
            HttpClient httpClient = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            _model = model ?? throw new ArgumentNullException(nameof(model));
            _tokenProvider = tokenProvider ?? (() => null);
            var baseStr = endpoint.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
                ? endpoint.AbsoluteUri
                : endpoint.AbsoluteUri + "/";
            _chatCompletionsUri = new Uri(new Uri(baseStr), "chat/completions");
            if (httpClient == null) { _http = new HttpClient(); _ownsHttp = true; }
            else                    { _http = httpClient;       _ownsHttp = false; }

            _pacer = new ChatPacer(_http, maxRequestsPerMinute, minRequestsPerMinute);
        }

        public void Dispose()
        {
            try { _pacer.Dispose(); } catch { }
            if (_ownsHttp) _http.Dispose();
        }

        public object GetService(Type serviceType, object serviceKey = null)
        {
            if (serviceType == typeof(ChatClientMetadata))
                return new ChatClientMetadata("openai-compat", _endpoint, _model);
            return serviceType?.IsInstanceOfType(this) == true ? this : null;
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, CancellationToken cancellationToken = default)
        {
            var req = BuildRequest(messages, options, stream: false);
            using var httpResp = await _pacer.SendAsync(
                () =>
                {
                    var httpReq = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
                    {
                        Content = JsonContent.Create(req, options: JsonOpts)
                    };
                    OpenAiCompatibleEmbeddingGenerator.ApplyAuth(httpReq, _tokenProvider);
                    return httpReq;
                },
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (!httpResp.IsSuccessStatusCode)
            {
                var body = await httpResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new HttpRequestException($"Chat request failed: {(int)httpResp.StatusCode} {httpResp.ReasonPhrase}: {body}");
            }
            var payload = await httpResp.Content.ReadFromJsonAsync<ChatCompletionResponse>(JsonOpts, cancellationToken).ConfigureAwait(false);
            return ToChatResponse(payload);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var req = BuildRequest(messages, options, stream: true);
            var httpResp = await _pacer.SendAsync(
                () =>
                {
                    var httpReq = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsUri)
                    {
                        Content = JsonContent.Create(req, options: JsonOpts)
                    };
                    OpenAiCompatibleEmbeddingGenerator.ApplyAuth(httpReq, _tokenProvider);
                    return httpReq;
                },
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            try
            {
                if (!httpResp.IsSuccessStatusCode)
                {
                    var body = await httpResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new HttpRequestException($"Chat request failed: {(int)httpResp.StatusCode} {httpResp.ReasonPhrase}: {body}");
                }

                using var stream = await httpResp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await foreach (var update in ReadStreamAsync(reader, cancellationToken).ConfigureAwait(false))
                    yield return update;
            }
            finally
            {
                httpResp.Dispose();
            }
        }

        // ---- Pacer specialisation ----
        // Chat doesn't batch (chat completions can't be merged). We just want
        // pacing + retry consolidation, so each request is its own batch.
        internal sealed class PendingChat
        {
            public Func<HttpRequestMessage> Factory;
            public HttpCompletionOption Completion;
            public TaskCompletionSource<HttpResponseMessage> Tcs = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration CtReg;
        }

        internal sealed class ChatPacer : RequestPacer<PendingChat>
        {
            public ChatPacer(HttpClient http, int ceilingRpm, int floorRpm)
                : base("chat", http, ceilingRpm, ceilingRpm, floorRpm) { }

            protected override int MaxBatchSize => 1;

            public Task<HttpResponseMessage> SendAsync(
                Func<HttpRequestMessage> factory,
                HttpCompletionOption completion,
                CancellationToken ct)
            {
                var p = new PendingChat { Factory = factory, Completion = completion };
                if (ct.CanBeCanceled)
                    p.CtReg = ct.Register(() => p.Tcs.TrySetCanceled(ct));
                Enqueue(p);
                return p.Tcs.Task;
            }

            protected override async Task DispatchAsync(IReadOnlyList<PendingChat> batch, CancellationToken ct)
            {
                var p = batch[0];
                if (p.Tcs.Task.IsCompleted) { p.CtReg.Dispose(); return; }
                try
                {
                    var resp = await SendWithRetryAsync(p.Factory, p.Completion, ct).ConfigureAwait(false);
                    if (!p.Tcs.TrySetResult(resp))
                        resp.Dispose();
                }
                catch (Exception ex)
                {
                    p.Tcs.TrySetException(ex);
                }
                finally
                {
                    p.CtReg.Dispose();
                }
            }

            protected override void FaultItem(PendingChat item, Exception ex)
            {
                item.Tcs.TrySetException(ex);
                item.CtReg.Dispose();
            }
        }

        // ---- Request building ----
        private ChatCompletionRequest BuildRequest(IEnumerable<ChatMessage> messages, ChatOptions options, bool stream)
        {
            var req = new ChatCompletionRequest
            {
                Model = options?.ModelId ?? _model,
                Stream = stream,
                Temperature = options?.Temperature,
                TopP = options?.TopP,
                MaxTokens = options?.MaxOutputTokens,
                Messages = new List<ChatRequestMessage>()
            };

            foreach (var m in messages)
            {
                var msg = new ChatRequestMessage
                {
                    Role = MapRole(m.Role),
                    Content = m.Text
                };

                var toolCalls = new List<ChatToolCall>();
                foreach (var c in m.Contents)
                {
                    if (c is FunctionCallContent fc)
                    {
                        if (string.IsNullOrEmpty(fc.Name)) continue; // backends 400 on empty names
                        toolCalls.Add(new ChatToolCall
                        {
                            Id = fc.CallId,
                            Type = "function",
                            Function = new ChatToolCallFunction
                            {
                                Name = fc.Name,
                                Arguments = JsonSerializer.Serialize(fc.Arguments ?? new Dictionary<string, object>())
                            }
                        });
                    }
                    else if (c is FunctionResultContent fr)
                    {
                        req.Messages.Add(new ChatRequestMessage
                        {
                            Role = "tool",
                            ToolCallId = fr.CallId,
                            Content = fr.Result?.ToString() ?? ""
                        });
                        msg = null;
                        break;
                    }
                }

                if (msg != null)
                {
                    if (toolCalls.Count > 0) msg.ToolCalls = toolCalls;
                    req.Messages.Add(msg);
                }
            }

            if (options?.Tools != null)
            {
                req.Tools = new List<ChatTool>();
                foreach (var t in options.Tools)
                {
                    if (t is AIFunction af)
                    {
                        var schema = af.JsonSchema.GetRawText();
                        req.Tools.Add(new ChatTool
                        {
                            Type = "function",
                            Function = new ChatToolFunction
                            {
                                Name = af.Name,
                                Description = af.Description,
                                Parameters = JsonDocument.Parse(schema).RootElement
                            }
                        });
                    }
                }
            }

            return req;
        }

        private static string MapRole(ChatRole role)
        {
            if (role == ChatRole.System) return "system";
            if (role == ChatRole.User) return "user";
            if (role == ChatRole.Assistant) return "assistant";
            if (role == ChatRole.Tool) return "tool";
            return role.Value;
        }

        private static ChatResponse ToChatResponse(ChatCompletionResponse r)
        {
            var msg = new ChatMessage(ChatRole.Assistant, r?.Choices?[0]?.Message?.Content ?? "");
            return new ChatResponse(msg)
            {
                ResponseId = r?.Id,
                ModelId = r?.Model,
                FinishReason = r?.Choices?[0]?.FinishReason switch
                {
                    "stop" => ChatFinishReason.Stop,
                    "length" => ChatFinishReason.Length,
                    "tool_calls" => ChatFinishReason.ToolCalls,
                    "content_filter" => ChatFinishReason.ContentFilter,
                    _ => null
                }
            };
        }

        // ---- Streaming SSE reader (with per-index tool-call coalescing) ----
        private async IAsyncEnumerable<ChatResponseUpdate> ReadStreamAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string responseId = Guid.NewGuid().ToString("N");
            string conversationId = null;
            string modelId = null;

            // Tool-call deltas are streamed by index; only the first chunk for
            // a given index carries id/function.name, later chunks just append
            // more text to function.arguments. Aggregate here and emit one
            // FunctionCallContent per call at the end -- otherwise we produce
            // multiple FunctionCallContents per call (most with empty names),
            // which the next turn echoes back as tool_calls with empty names
            // and the backend rejects with a 400.
            var pendingToolCalls = new Dictionary<int, PendingToolCall>();

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line.Substring(5).TrimStart();
                if (data == "[DONE]") break;

                ChatCompletionResponse chunk = null;
                try { chunk = JsonSerializer.Deserialize<ChatCompletionResponse>(data, JsonOpts); }
                catch { continue; }
                if (chunk?.Choices == null || chunk.Choices.Count == 0) continue;

                if (!string.IsNullOrEmpty(chunk.Id)) responseId = chunk.Id;
                if (!string.IsNullOrEmpty(chunk.Model)) modelId = chunk.Model;

                var choice = chunk.Choices[0];
                var delta = choice.Delta;
                if (delta == null) continue;

                if (delta.ToolCalls != null)
                {
                    foreach (var tc in delta.ToolCalls)
                    {
                        var idx = tc.Index ?? pendingToolCalls.Count;
                        if (!pendingToolCalls.TryGetValue(idx, out var pending))
                        {
                            pending = new PendingToolCall { Arguments = new StringBuilder() };
                            pendingToolCalls[idx] = pending;
                        }
                        if (!string.IsNullOrEmpty(tc.Id)) pending.Id = tc.Id;
                        if (tc.Function != null)
                        {
                            if (!string.IsNullOrEmpty(tc.Function.Name)) pending.Name = tc.Function.Name;
                            if (!string.IsNullOrEmpty(tc.Function.Arguments)) pending.Arguments.Append(tc.Function.Arguments);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                        ConversationId = conversationId,
                        Role = ChatRole.Assistant,
                        ModelId = modelId,
                        Contents = new List<AIContent> { new TextContent(delta.Content) }
                    };
                }
            }

            if (pendingToolCalls.Count > 0)
            {
                var contents = new List<AIContent>();
                foreach (var kv in pendingToolCalls)
                {
                    var p = kv.Value;
                    if (string.IsNullOrEmpty(p.Name)) continue;
                    contents.Add(new FunctionCallContent(
                        callId: p.Id ?? Guid.NewGuid().ToString("N"),
                        name: p.Name,
                        arguments: ParseArgs(p.Arguments.ToString())));
                }
                if (contents.Count > 0)
                {
                    yield return new ChatResponseUpdate
                    {
                        ResponseId = responseId,
                        MessageId = responseId,
                        ConversationId = conversationId,
                        Role = ChatRole.Assistant,
                        ModelId = modelId,
                        Contents = contents
                    };
                }
            }
        }

        private static Dictionary<string, object> ParseArgs(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            try { return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object> { ["raw"] = json }; }
        }

        private sealed class PendingToolCall
        {
            public string Id;
            public string Name;
            public StringBuilder Arguments;
        }

        // ---- DTOs ----
        private class ChatCompletionRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("messages")] public List<ChatRequestMessage> Messages { get; set; }
            [JsonPropertyName("temperature")] public float? Temperature { get; set; }
            [JsonPropertyName("top_p")] public float? TopP { get; set; }
            [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
            [JsonPropertyName("stream")] public bool Stream { get; set; }
            [JsonPropertyName("tools")] public List<ChatTool> Tools { get; set; }
        }
        private class ChatRequestMessage
        {
            [JsonPropertyName("role")] public string Role { get; set; }
            [JsonPropertyName("content")] public string Content { get; set; }
            [JsonPropertyName("tool_calls")] public List<ChatToolCall> ToolCalls { get; set; }
            [JsonPropertyName("tool_call_id")] public string ToolCallId { get; set; }
        }
        private class ChatTool
        {
            [JsonPropertyName("type")] public string Type { get; set; }
            [JsonPropertyName("function")] public ChatToolFunction Function { get; set; }
        }
        private class ChatToolFunction
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("description")] public string Description { get; set; }
            [JsonPropertyName("parameters")] public JsonElement Parameters { get; set; }
        }
        private class ChatToolCall
        {
            [JsonPropertyName("index")] public int? Index { get; set; }
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("type")] public string Type { get; set; }
            [JsonPropertyName("function")] public ChatToolCallFunction Function { get; set; }
        }
        private class ChatToolCallFunction
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("arguments")] public string Arguments { get; set; }
        }
        private class ChatCompletionResponse
        {
            [JsonPropertyName("id")] public string Id { get; set; }
            [JsonPropertyName("model")] public string Model { get; set; }
            [JsonPropertyName("choices")] public List<ChatCompletionChoice> Choices { get; set; }
        }
        private class ChatCompletionChoice
        {
            [JsonPropertyName("index")] public int Index { get; set; }
            [JsonPropertyName("message")] public ChatRequestMessage Message { get; set; }
            [JsonPropertyName("delta")] public ChatRequestMessage Delta { get; set; }
            [JsonPropertyName("finish_reason")] public string FinishReason { get; set; }
        }
    }
}
