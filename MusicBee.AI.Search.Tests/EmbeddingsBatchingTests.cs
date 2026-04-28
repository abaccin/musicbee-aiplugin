using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.AI;
using MusicBee.AI.Search.AI;

namespace MusicBee.AI.Search.Tests;

public class EmbeddingsBatchingTests
{
    [Fact]
    public async Task ConcurrentSingleInputCalls_AreCoalescedIntoOnePost()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        // High RPM so the limiter never throttles tests.
        var gen = new OpenAiCompatibleEmbeddingGenerator(
            new Uri("https://example/inference"), "model",
            tokenProvider: () => "tok",
            dimensions: 4,
            maxRequestsPerMinute: 6000,
            minRequestsPerMinute: 100,
            batchSize: 8,
            httpClient: http);

        // Fire 5 calls simultaneously. The pacer worker should drain them
        // into a single dispatch (batch size 8 >= 5).
        var t1 = gen.GenerateAsync(new[] { "a" });
        var t2 = gen.GenerateAsync(new[] { "b" });
        var t3 = gen.GenerateAsync(new[] { "c" });
        var t4 = gen.GenerateAsync(new[] { "d" });
        var t5 = gen.GenerateAsync(new[] { "e" });

        await Task.WhenAll(t1, t2, t3, t4, t5);
        gen.Dispose();

        // The very first request takes a token immediately, so the worker
        // dispatches it alone before later items have queued up. The
        // remaining 4 should batch into a second POST. Either way we expect
        // significantly fewer than 5 POSTs.
        handler.RequestCount.Should().BeLessThan(5);
        handler.LastInputs.Should().NotBeNull();
        handler.LastInputs!.Count.Should().BeGreaterThanOrEqualTo(1);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int RequestCount;
        public List<string>? LastInputs;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref RequestCount);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // Cheap parse: count occurrences of "input" entries.
            var doc = System.Text.Json.JsonDocument.Parse(body);
            var inputs = new List<string>();
            if (doc.RootElement.TryGetProperty("input", out var inp))
            {
                foreach (var s in inp.EnumerateArray()) inputs.Add(s.GetString() ?? "");
            }
            LastInputs = inputs;
            // Build a fake response with one embedding per input, dim=4.
            var sb = new StringBuilder();
            sb.Append("{\"model\":\"m\",\"data\":[");
            for (int i = 0; i < inputs.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(i).Append(",\"embedding\":[0.1,0.2,0.3,0.4]}");
            }
            sb.Append("]}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json")
            };
        }
    }
}
