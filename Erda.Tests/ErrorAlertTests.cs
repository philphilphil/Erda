using Erda.Core.Scheduling;
using Erda.Core.Services.Seq;
using Xunit;

namespace Erda.Tests;

public class ErrorAlertTests
{
    [Fact]
    public void Includes_level_source_message_exception_and_analysis()
    {
        var e = new SeqError
        {
            Level = "Error",
            RenderedMessage = "DB timeout",
            ExceptionType = "System.TimeoutException",
            Timestamp = DateTimeOffset.UnixEpoch,
            Properties = new Dictionary<string, string> { ["Application"] = "Erda" },
        };

        var text = ErrorAlert.Format(e, "Likely the DB is down.");

        Assert.Contains("Error", text);
        Assert.Contains("Erda", text);
        Assert.Contains("DB timeout", text);
        Assert.Contains("System.TimeoutException", text);
        Assert.Contains("Likely the DB is down.", text);
    }

    [Fact]
    public void Uses_the_app_property_as_the_source_label()
    {
        var e = new SeqError
        {
            Level = "Error",
            RenderedMessage = "boom",
            Timestamp = DateTimeOffset.UnixEpoch,
            Properties = new Dictionary<string, string> { ["app"] = "Erda" },
        };
        Assert.Contains("Erda", ErrorAlert.Format(e, null));
    }

    [Fact]
    public void Surfaces_configured_properties_in_the_body()
    {
        // A constant-template event (Leporello scrape_error) carries its detail in properties.
        var e = new SeqError
        {
            Level = "Error",
            RenderedMessage = "scrape_error",
            Timestamp = DateTimeOffset.UnixEpoch,
            Properties = new Dictionary<string, string> { ["venue"] = "Konzerthaus", ["error"] = "HTTP 500" },
        };

        var text = ErrorAlert.Format(e, null, propertyNames: new[] { "venue", "error" });

        Assert.Contains("venue", text);
        Assert.Contains("Konzerthaus", text);
        Assert.Contains("error", text);
        Assert.Contains("HTTP 500", text);
    }

    [Fact]
    public void Skips_property_lines_that_are_absent_or_blank()
    {
        var e = new SeqError
        {
            Level = "Error",
            RenderedMessage = "scrape_error",
            Timestamp = DateTimeOffset.UnixEpoch,
            Properties = new Dictionary<string, string> { ["venue"] = "Konzerthaus" },
        };

        var text = ErrorAlert.Format(e, null, propertyNames: new[] { "venue", "error" });

        Assert.Contains("Konzerthaus", text);
        Assert.DoesNotContain("error:", text); // absent property produces no line
    }

    [Fact]
    public void Truncates_very_long_content()
    {
        var e = new SeqError { Level = "Error", RenderedMessage = new string('x', 6000), Timestamp = DateTimeOffset.UnixEpoch };
        var text = ErrorAlert.Format(e, null);
        Assert.True(text.Length <= 3501);
    }
}
