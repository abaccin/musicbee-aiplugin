using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.AI;
using MusicBee.AI.Search.AI;

namespace MusicBee.AI.Search.Tests;

public class GitHubModelsChatClientStreamingTests
{
    [Fact]
    public async Task StreamingToolCalls_SplitAcrossChunks_AreCoalescedIntoOneFunctionCall()
    {
        // Simulates OpenAI-style SSE: first chunk carries id + name, subsequent
        // chunks only append more text to function.arguments. The previous
        // implementation produced one FunctionCallContent per chunk (most with
        // empty names), which GitHub Models then 400'd on the next turn.
        var sse = new StringBuilder();
        sse.Append("data: {\"id\":\"r1\",\"model\":\"m\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"tool_calls\":[{\"index\":0,\"id\":\"call_abc\",\"type\":\"function\",\"function\":{\"name\":\"SearchLibrary\",\"arguments\":\"{\\\"que\"}}]}}]}\n\n");
        sse.Append("data: {\"id\":\"r1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ry\\\":\\\"jazz\\\"\"}}]}}]}\n\n");
        sse.Append("data: {\"id\":\"r1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"}\"}}]}}]}\n\n");
        sse.Append("data: {\"id\":\"r1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
        sse.Append("data: [DONE]\n\n");

        var http = new HttpClient(new StubHandler(sse.ToString()));
        var client = new OpenAiCompatibleChatClient(
            new Uri("https://example/inference"), "m", () => "tok",
            maxRequestsPerMinute: 6000, minRequestsPerMinute: 100, httpClient: http);

        var calls = new List<FunctionCallContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "hi") }))
        {
            foreach (var c in update.Contents)
                if (c is FunctionCallContent fc) calls.Add(fc);
        }

        calls.Should().HaveCount(1);
        calls[0].Name.Should().Be("SearchLibrary");
        calls[0].CallId.Should().Be("call_abc");
        calls[0].Arguments.Should().ContainKey("query")
            .WhoseValue!.ToString().Should().Be("jazz");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) { _body = body; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream")
            };
            return Task.FromResult(resp);
        }
    }
}
