using Erda.Core.Configuration;
using Erda.Core.Scheduling;
using Erda.Core.Services.Seq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Erda.Tests;

/// <summary>
/// Live integration check against a real Seq instance. Runs only when ERDA_SEQ_IT_URL is set
/// (e.g. the local docker-compose Seq); otherwise it's a no-op so the normal suite stays offline.
/// Ingest some Error events first (see scripts/seq-smoke or MORNING.md).
/// </summary>
public class SeqClientIntegrationTests
{
    [Fact]
    public async Task Queries_and_maps_error_events_from_a_live_seq()
    {
        var url = Environment.GetEnvironmentVariable("ERDA_SEQ_IT_URL");
        if (string.IsNullOrWhiteSpace(url))
            return; // not configured -> skip (offline run)

        var apiKey = Environment.GetEnvironmentVariable("ERDA_SEQ_IT_APIKEY");
        var client = new SeqClient(
            Options.Create(new SeqOptions { ServerUrl = url, ApiKey = apiKey }),
            NullLogger<SeqClient>.Instance);

        var filter = SeqFilter.ForMinLevel("Error");
        var errors = await client.QueryErrorsAsync(filter, DateTimeOffset.UtcNow.AddHours(-1), 50, default);

        Assert.NotEmpty(errors);
        var e = errors[0];
        Assert.True(e.Level is "Error" or "Fatal");
        Assert.NotEqual(default, e.Timestamp);
        Assert.False(string.IsNullOrWhiteSpace(e.Display));
    }
}
