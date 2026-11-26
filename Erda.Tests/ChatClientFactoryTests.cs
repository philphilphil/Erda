using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using Erda.Agents.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Pins what actually goes on the wire to the LiteLLM endpoint: the Chat Completions route, the bearer
/// key, the model, the reasoning effort and web search. A transport captures the request, so no network
/// is touched.
/// </summary>
public class ChatClientFactoryTests
{
    /// <summary>Records each request and answers with a minimal, valid Chat Completions response.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            const string reply = """
                {"id":"c1","object":"chat.completion","created":1,"model":"claude-opus-5-5",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"hi"}}],
                 "usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(reply, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (ChatClientFactory Factory, CapturingHandler Handler) Make()
    {
        var handler = new CapturingHandler();
        var transport = new HttpClientPipelineTransport(new HttpClient(handler));
        return (new ChatClientFactory(TestChat.Options(), transport), handler);
    }

    [Fact]
    public async Task Sends_chat_completions_to_the_configured_endpoint_with_the_key_and_model()
    {
        var (factory, handler) = Make();

        var response = await factory.CreateChatClient().GetResponseAsync("hello",
            ChatClientFactory.BuildOptions(null, null, webSearch: false, "medium"));

        Assert.Equal("hi", response.Text);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://localhost:1234/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
        using var json = JsonDocument.Parse(body);
        Assert.Equal("claude-opus-5-5", json.RootElement.GetProperty("model").GetString());
    }

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    public async Task Maps_reasoning_effort_to_the_reasoning_effort_field(string effort)
    {
        var (factory, handler) = Make();

        await factory.CreateChatClient().GetResponseAsync("hello",
            ChatClientFactory.BuildOptions(null, null, webSearch: false, effort));

        using var json = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.Equal(effort, json.RootElement.GetProperty("reasoning_effort").GetString());
        // Opus 5.5 rejects sampling parameters; Erda must never send them.
        Assert.False(json.RootElement.TryGetProperty("temperature", out _));
        Assert.False(json.RootElement.TryGetProperty("top_p", out _));
    }

    [Fact]
    public async Task Web_search_on_sends_web_search_options()
    {
        var (factory, handler) = Make();

        await factory.CreateChatClient().GetResponseAsync("hello",
            ChatClientFactory.BuildOptions(null, null, webSearch: true, "medium"));

        using var json = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.True(json.RootElement.TryGetProperty("web_search_options", out _),
            $"expected web_search_options in: {handler.Requests.Single().Body}");
    }

    [Fact]
    public async Task Web_search_off_sends_no_web_search_options()
    {
        var (factory, handler) = Make();

        await factory.CreateChatClient().GetResponseAsync("hello",
            ChatClientFactory.BuildOptions(null, null, webSearch: false, "medium"));

        using var json = JsonDocument.Parse(handler.Requests.Single().Body);
        Assert.False(json.RootElement.TryGetProperty("web_search_options", out _));
    }

    [Fact]
    public void BuildOptions_adds_web_search_once_even_if_the_caller_already_has_it()
    {
        var options = ChatClientFactory.BuildOptions("sys", [new HostedWebSearchTool()], webSearch: true, "high");

        Assert.Equal("sys", options.Instructions);
        Assert.Single(options.Tools!.OfType<HostedWebSearchTool>());
        Assert.Equal(ReasoningEffort.High, options.Reasoning!.Effort);
    }

    [Fact]
    public void ToEffort_rejects_an_unknown_level()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChatClientFactory.ToEffort("minimal"));
    }
}
