using System.Text.Json;
using System.Threading.Channels;
using Erda.Data;
using Erda.Services;

namespace Erda.Api;

/// <summary>
/// JSON + Server-Sent-Events endpoints over <see cref="IActivityRecorder"/> for the panel's Activity
/// screen. The SPA fetches a snapshot from <c>GET /api/activity</c>, then opens <c>/api/activity/stream</c>
/// to receive new entries live (replacing the Blazor Server circuit push).
/// </summary>
public static class ActivityEndpoints
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);

    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder group)
    {
        var g = group.MapGroup("/activity");

        g.MapGet("", (IActivityRecorder recorder, int? max) =>
        {
            var dtos = recorder.Recent(max ?? 100)
                .Select(e => new ActivityDto(e.Id, e.TimestampUtc, e.Kind, e.Summary))
                .ToList();
            return Results.Ok(dtos);
        });

        g.MapGet("/stream", StreamActivityAsync);

        return group;
    }

    /// <summary>
    /// Streams newly-recorded activity entries as SSE frames until the client disconnects. Bridges the
    /// recorder's <c>Recorded</c> event to the response through a bounded channel (drop-oldest, so a
    /// slow client can never block the recorder), unsubscribing on disconnect.
    /// </summary>
    private static async Task StreamActivityAsync(HttpContext http, IActivityRecorder recorder)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering if one is in front

        var channel = Channel.CreateBounded<ActivityEntry>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        void OnRecorded(ActivityEntry entry) => channel.Writer.TryWrite(entry);
        recorder.Recorded += OnRecorded;

        var ct = http.RequestAborted;
        try
        {
            // Flush the response start immediately so the client's EventSource fires `onopen` and any
            // proxy in front stops buffering — otherwise headers wouldn't go out until the first event.
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            await foreach (var entry in channel.Reader.ReadAllAsync(ct))
            {
                var dto = new ActivityDto(entry.Id, entry.TimestampUtc, entry.Kind, entry.Summary);
                await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(dto, StreamJson)}\n\n", ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal shutdown of this stream.
        }
        finally
        {
            recorder.Recorded -= OnRecorded;
        }
    }
}
