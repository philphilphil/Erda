using Erda.Core.Scheduling;
using Xunit;

namespace Erda.Tests;

/// <summary>The two WhatsApp texts the chat-health watch sends, and the duration wording in them.</summary>
public class ChatHealthAlertTests
{
    [Fact]
    public void Down_alert_names_the_endpoint_model_and_reason()
    {
        var text = ChatHealthAlert.FormatDown("http://127.0.0.1:10531/v1", "gpt-5.5", "HttpRequestException: refused");

        Assert.Contains("OpenAI proxy is not answering", text);
        Assert.Contains("http://127.0.0.1:10531/v1", text);
        Assert.Contains("gpt-5.5", text);
        Assert.Contains("refused", text);
        Assert.DoesNotContain("still down", text);
    }

    [Fact]
    public void Repeat_alert_says_how_long_it_has_been_down()
    {
        var text = ChatHealthAlert.FormatDown("http://proxy/v1", "gpt-5.5", "timeout", TimeSpan.FromHours(7));

        Assert.Contains("still down (7 hours)", text);
    }

    [Fact]
    public void Down_alert_survives_a_missing_reason()
    {
        var text = ChatHealthAlert.FormatDown("http://proxy/v1", "gpt-5.5", error: null);

        Assert.Contains("OpenAI proxy is not answering", text);
        Assert.DoesNotContain("Reason:", text);
    }

    [Fact]
    public void Recovery_notice_names_the_outage_length()
    {
        var text = ChatHealthAlert.FormatRecovered("http://proxy/v1", TimeSpan.FromMinutes(45));

        Assert.Contains("answering again", text);
        Assert.Contains("45 minutes", text);
    }

    [Theory]
    [InlineData(20, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(300, "5 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(9000, "2 hours")]
    [InlineData(180000, "2 days")]
    public void Humanize_rounds_down_to_a_readable_unit(int seconds, string expected)
        => Assert.Equal(expected, ChatHealthAlert.Humanize(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Humanize_treats_a_negative_span_as_zero()
        => Assert.Equal("less than a minute", ChatHealthAlert.Humanize(TimeSpan.FromSeconds(-5)));
}
