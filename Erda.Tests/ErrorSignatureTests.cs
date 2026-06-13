using Erda.Core.Scheduling;
using Erda.Core.Services.Seq;
using Xunit;

namespace Erda.Tests;

public class ErrorSignatureTests
{
    private static SeqError Err(string level, string template, string? exType = null, string rendered = "") =>
        new() { Level = level, MessageTemplate = template, ExceptionType = exType, RenderedMessage = rendered };

    [Fact]
    public void Same_template_different_params_has_same_signature()
    {
        var a = Err("Error", "User {Id} not found", rendered: "User 1 not found");
        var b = Err("Error", "User {Id} not found", rendered: "User 2 not found");
        Assert.Equal(ErrorSignature.Compute(a), ErrorSignature.Compute(b));
    }

    [Fact]
    public void Different_level_or_exception_differs()
    {
        var baseline = Err("Error", "Boom", "System.Exception");
        Assert.NotEqual(ErrorSignature.Compute(baseline), ErrorSignature.Compute(Err("Fatal", "Boom", "System.Exception")));
        Assert.NotEqual(ErrorSignature.Compute(baseline), ErrorSignature.Compute(Err("Error", "Boom", "System.NullReferenceException")));
    }

    [Fact]
    public void Falls_back_to_rendered_when_no_template()
    {
        var a = Err("Error", "", rendered: "literal message");
        Assert.Contains("literal message", ErrorSignature.Compute(a));
    }

    private static SeqError ErrWithProps(string template, params (string Key, string Value)[] props) =>
        new()
        {
            Level = "Error",
            MessageTemplate = template,
            RenderedMessage = template,
            Properties = props.ToDictionary(p => p.Key, p => p.Value),
        };

    [Fact]
    public void Configured_properties_split_an_otherwise_identical_signature()
    {
        // Leporello's scrape_error: a constant template, detail lives in properties.
        var a = ErrWithProps("scrape_error", ("venue", "A"), ("error", "timeout"));
        var b = ErrWithProps("scrape_error", ("venue", "B"), ("error", "timeout"));
        var props = new[] { "venue", "error" };

        Assert.NotEqual(ErrorSignature.Compute(a, props), ErrorSignature.Compute(b, props));
        // Without the property list, they collapse to one signature (today's behavior).
        Assert.Equal(ErrorSignature.Compute(a), ErrorSignature.Compute(b));
    }

    [Fact]
    public void Same_configured_property_values_share_a_signature()
    {
        var a = ErrWithProps("scrape_error", ("venue", "A"), ("error", "timeout"));
        var b = ErrWithProps("scrape_error", ("venue", "A"), ("error", "timeout"));
        Assert.Equal(
            ErrorSignature.Compute(a, new[] { "venue", "error" }),
            ErrorSignature.Compute(b, new[] { "venue", "error" }));
    }

    [Fact]
    public void Missing_configured_property_is_treated_as_empty_and_does_not_throw()
    {
        var a = ErrWithProps("scrape_error", ("venue", "A"));
        var sig = ErrorSignature.Compute(a, new[] { "venue", "error" });
        Assert.Contains("venue=A", sig);
    }
}
