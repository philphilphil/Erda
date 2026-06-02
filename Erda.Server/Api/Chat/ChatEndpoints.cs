using System.Text.Json;
using Erda.Agents.WebChat;

namespace Erda.Server.Api;

/// <summary>
/// SSE streaming endpoint for the web-chat channel. The browser POSTs the user's message and
/// receives a <c>text/event-stream</c> of <c>{"delta":"…"}</c> frames followed by <c>{"done":true}</c>.
/// Follows the same flush-per-event pattern as <see cref="ActivityEndpoints"/>.
/// </summary>
public static class ChatEndpoints
{
    internal static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    /// <param name="Text">The user's message text.</param>
    internal record ChatRequest(string Text);

    public static RouteGroupBuilder MapChatEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/chat");

        g.MapPost("", StreamChatAsync);

        g.MapPost("/reset", (IWebChat webChat) =>
        {
            webChat.Reset();
            return Results.Ok();
        });

        // Lets the browser check whether its locally persisted history still belongs to the live
        // session. A null id means the agent has no current session (fresh start / after a restart).
        g.MapGet("/session", (IWebChat webChat) => Results.Ok(new { sessionId = webChat.SessionId }));

        return group;
    }

    /// <summary>
    /// Streams the agent's reply as SSE deltas. Client disconnect is treated as normal cancellation
    /// (no error event), matching how <see cref="ActivityEndpoints"/> handles it.
    /// </summary>
    internal static async Task StreamChatAsync(ChatRequest req, IWebChat webChat, HttpContext http)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";

        var ct = http.RequestAborted;
        try
        {
            await foreach (var delta in webChat.StreamReplyAsync(req.Text, ct))
            {
                var payload = JsonSerializer.Serialize(new { delta }, StreamJson);
                await http.Response.WriteAsync($"data: {payload}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }

            var donePayload = JsonSerializer.Serialize(new { done = true, sessionId = webChat.SessionId }, StreamJson);
            await http.Response.WriteAsync($"data: {donePayload}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal shutdown of this stream.
        }
        catch (Exception ex)
        {
            var payload = JsonSerializer.Serialize(new { error = ex.Message }, StreamJson);
            try
            {
                await http.Response.WriteAsync($"data: {payload}\n\n", CancellationToken.None);
                await http.Response.Body.FlushAsync(CancellationToken.None);
            }
            catch
            {
                // If writing the error also fails the connection is gone; nothing to do.
            }
        }
    }
}
