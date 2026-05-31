using System.Text;
using Erda.Configuration;
using Microsoft.Extensions.Options;
using global::Seq.Api;                 // fully qualified: our namespace ends in ".Seq", which would shadow it
using global::Seq.Api.Model.Events;

namespace Erda.Services.Seq;

/// <summary>Queries Seq for error events.</summary>
public interface ISeqClient
{
    Task<IReadOnlyList<SeqError>> QueryErrorsAsync(
        string filter, DateTimeOffset? fromUtc, int count, CancellationToken cancellationToken = default);
}

/// <summary>
/// Wraps the official <see cref="SeqConnection"/> client and maps Seq's <see cref="EventEntity"/>
/// onto our pure <see cref="SeqError"/>. The connection is created lazily and reused.
/// </summary>
public sealed class SeqClient(IOptions<SeqOptions> options, ILogger<SeqClient> logger) : ISeqClient, IDisposable
{
    private SeqConnection? _connection;

    private SeqConnection Connection()
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ServerUrl))
            throw new InvalidOperationException("Seq:ServerUrl is not configured.");
        return _connection ??= new SeqConnection(o.ServerUrl, string.IsNullOrWhiteSpace(o.ApiKey) ? null : o.ApiKey);
    }

    public async Task<IReadOnlyList<SeqError>> QueryErrorsAsync(
        string filter, DateTimeOffset? fromUtc, int count, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Querying Seq: filter=[{Filter}] from={From:u} count={Count}", filter, fromUtc, count);
        var events = await Connection().Events.ListAsync(
            filter: filter,
            count: count,
            render: true,
            fromDateUtc: fromUtc?.UtcDateTime,
            cancellationToken: cancellationToken);

        var result = new List<SeqError>(events.Count);
        foreach (var e in events)
            result.Add(Map(e));
        return result;
    }

    private static SeqError Map(EventEntity e)
    {
        var template = ReconstructTemplate(e);
        return new SeqError
        {
            Id = e.Id ?? "",
            Timestamp = ParseTimestamp(e.Timestamp),
            Level = e.Level ?? "",
            MessageTemplate = template,
            RenderedMessage = string.IsNullOrEmpty(e.RenderedMessage) ? template : e.RenderedMessage,
            Exception = e.Exception,
            ExceptionType = ExceptionType(e.Exception),
            Properties = MapProperties(e),
        };
    }

    private static string ReconstructTemplate(EventEntity e)
    {
        if (e.MessageTemplateTokens is null)
            return e.RenderedMessage ?? "";
        var sb = new StringBuilder();
        foreach (var t in e.MessageTemplateTokens)
        {
            if (!string.IsNullOrEmpty(t.PropertyName))
                sb.Append('{').Append(t.PropertyName).Append('}');
            else
                sb.Append(t.Text ?? t.RawText ?? "");
        }
        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> MapProperties(EventEntity e)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (e.Properties is not null)
        {
            foreach (var p in e.Properties)
            {
                if (p?.Name is { Length: > 0 } name && !dict.ContainsKey(name))
                    dict[name] = p.Value?.ToString() ?? "";
            }
        }
        return dict;
    }

    private static DateTimeOffset ParseTimestamp(string? timestamp) =>
        DateTimeOffset.TryParse(timestamp, out var ts) ? ts : DateTimeOffset.MinValue;

    private static string? ExceptionType(string? exception)
    {
        if (string.IsNullOrWhiteSpace(exception))
            return null;
        var firstLine = exception.Split('\n', 2)[0].Trim();
        var colon = firstLine.IndexOf(':');
        return colon > 0 ? firstLine[..colon].Trim() : firstLine;
    }

    public void Dispose() => _connection?.Dispose();
}
